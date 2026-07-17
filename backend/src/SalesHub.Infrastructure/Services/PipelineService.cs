using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SalesHub.Core.Abstractions;
using SalesHub.Core.Domain.Entities;
using SalesHub.Core.Domain.Enums;
using SalesHub.Infrastructure.Apify;
using SalesHub.Infrastructure.Options;
using SalesHub.Infrastructure.Persistence;

namespace SalesHub.Infrastructure.Services;

public record PipelineRunOptions(
    string? ProductKey,
    LeadSource[] Sources,
    string? City,
    string? Province,
    string? Category,
    int MaxPerSource,
    bool AutoQueueMessages);

public class PipelineService
{
    private readonly ApplicationDbContext _db;
    private readonly IEnumerable<IApifySource> _sources;
    private readonly IPhoneNormalizer _phone;
    private readonly IMessageRenderer _renderer;
    private readonly ILeadAssigner _assigner;
    private readonly IEvolutionClient _evo;
    private readonly ApifyUsageMonitor _usage;
    private readonly IWebsiteContactExtractor _websiteExtractor;
    private readonly GoogleOptions _google;
    private readonly ApifyOptions _apify;
    private readonly ILogger<PipelineService> _log;

    public PipelineService(
        ApplicationDbContext db,
        IEnumerable<IApifySource> sources,
        IPhoneNormalizer phone,
        IMessageRenderer renderer,
        ILeadAssigner assigner,
        IEvolutionClient evo,
        ApifyUsageMonitor usage,
        IWebsiteContactExtractor websiteExtractor,
        IOptions<GoogleOptions> google,
        IOptions<ApifyOptions> apify,
        ILogger<PipelineService> log)
    {
        _db = db; _sources = sources; _phone = phone; _renderer = renderer;
        _assigner = assigner; _evo = evo; _usage = usage; _websiteExtractor = websiteExtractor;
        _google = google.Value; _apify = apify.Value; _log = log;
    }

    public class CircuitBreakerException : Exception
    {
        public CircuitBreakerException(string msg) : base(msg) { }
    }

    public async Task<int> RunAsync(PipelineRunOptions opts, CancellationToken ct)
    {
        var products = opts.ProductKey is null
            ? await _db.Products.Where(p => p.Active).ToListAsync(ct)
            : await _db.Products.Where(p => p.ProductKey == opts.ProductKey && p.Active).ToListAsync(ct);

        if (products.Count == 0)
        {
            _log.LogWarning("No active products matching {Key}", opts.ProductKey);
            return 0;
        }

        // Apify circuit breaker: don't launch if account is saturated.
        var apifySources = opts.Sources.Any(s => s != LeadSource.GooglePlaces);
        if (apifySources)
        {
            var block = await _usage.WhyNotRunAsync(ct: ct);
            if (block is not null)
            {
                _log.LogWarning("Pipeline aborted by Apify circuit breaker: {Reason}", block);
                throw new CircuitBreakerException(block);
            }
        }

        var totalCreated = 0;
        foreach (var product in products)
        {
            var (city, province, _) = await PickTargetAsync(product, opts, ct);
            foreach (var src in _sources.Where(s => opts.Sources.Contains(s.Source)))
            {
                var perRunCap = opts.MaxPerSource;
                if (src.Source == LeadSource.GooglePlaces)
                {
                    var since = DateTimeOffset.UtcNow.Date;
                    if (_google.PlacesDailyCap > 0)
                    {
                        var todayRuns = await _db.ScrapeLogs
                            .CountAsync(l => l.Source == LeadSource.GooglePlaces && l.RunAt >= since, ct);
                        if (todayRuns >= _google.PlacesDailyCap)
                        {
                            _log.LogWarning("Google Places global runs/day cap hit ({Count}/{Cap}); skipping {Product}",
                                todayRuns, _google.PlacesDailyCap, product.ProductKey);
                            continue;
                        }
                    }
                    if (product.GooglePlacesDailyLeadCap > 0)
                    {
                        var leadsToday = await _db.Leads
                            .CountAsync(l => l.ProductKey == product.ProductKey && l.Source == LeadSource.GooglePlaces && l.CreatedAt >= since, ct);
                        var remaining = product.GooglePlacesDailyLeadCap - leadsToday;
                        if (remaining <= 0)
                        {
                            _log.LogInformation("Per-product Google Places lead cap reached for {Product} ({Count}/{Cap}); skipping",
                                product.ProductKey, leadsToday, product.GooglePlacesDailyLeadCap);
                            continue;
                        }
                        perRunCap = Math.Min(perRunCap, remaining);
                    }
                }
                if (src.Source != LeadSource.GooglePlaces && _apify.DailyRunCap > 0)
                {
                    var since = DateTimeOffset.UtcNow.Date;
                    var todayCount = await _db.ScrapeLogs
                        .CountAsync(l => l.Source != LeadSource.GooglePlaces && l.RunAt >= since, ct);
                    if (todayCount >= _apify.DailyRunCap)
                    {
                        _log.LogWarning("Apify daily cap hit ({Count}/{Cap}); skipping {Product}/{Source}",
                            todayCount, _apify.DailyRunCap, product.ProductKey, src.Source);
                        continue;
                    }
                }

                var run = new ApifyRun
                {
                    Id = Guid.NewGuid(),
                    Source = src.Source,
                    ActorId = src.GetType().Name,
                    ProductKey = product.ProductKey,
                    StartedAt = DateTimeOffset.UtcNow
                };
                _db.ApifyRuns.Add(run);
                await _db.SaveChangesAsync(ct);

                try
                {
                    var res = await src.RunAsync(new SourceRunRequest(product, city, province, opts.Category, perRunCap), ct);
                    var created = await IngestLeadsAsync(res.Leads, product, src.Source, opts.AutoQueueMessages, ct);
                    totalCreated += created;

                    run.FinishedAt = DateTimeOffset.UtcNow;
                    run.Status = "success";
                    run.ItemsCount = res.RawItems;
                    run.LeadsCreated = created;

                    _db.ScrapeLogs.Add(new ScrapeLog
                    {
                        ProductKey = product.ProductKey,
                        Country = product.Country,
                        City = city,
                        Category = opts.Category,
                        Source = src.Source,
                        ResultsCount = created,
                        Status = created > 0 ? "done" : "empty"
                    });
                    await _db.SaveChangesAsync(ct);
                }
                catch (Exception ex)
                {
                    _log.LogError(ex, "Source {Src} failed for product {P}", src.Source, product.ProductKey);
                    run.FinishedAt = DateTimeOffset.UtcNow;
                    run.Status = "error";
                    run.Error = ex.Message;
                    _db.ScrapeLogs.Add(new ScrapeLog
                    {
                        ProductKey = product.ProductKey,
                        Country = product.Country,
                        City = city,
                        Category = opts.Category,
                        Source = src.Source,
                        ResultsCount = 0,
                        Status = "error",
                        Error = ex.Message
                    });
                    await _db.SaveChangesAsync(ct);
                }
            }
        }
        return totalCreated;
    }

    private async Task<int> IngestLeadsAsync(
        IReadOnlyList<Lead> leads, Product product, LeadSource source, bool autoQueue, CancellationToken ct)
    {
        var created = 0;
        var discarded = 0;
        foreach (var lead in leads)
        {
            lead.WhatsappPhone = _phone.Normalize(lead.RawPhone, product.PhonePrefix);

            // Fallback: si Google no trajo teléfono pero sí website, lo crawleamos buscando
            // tel: / wa.me / patrones de tel argentinos, y de paso sacamos IG/FB.
            if (string.IsNullOrWhiteSpace(lead.WhatsappPhone) && !string.IsNullOrWhiteSpace(lead.Website))
            {
                var info = await _websiteExtractor.ExtractAsync(lead.Website, ct);
                if (!string.IsNullOrWhiteSpace(info.Phone))
                {
                    lead.RawPhone ??= info.Phone;
                    lead.WhatsappPhone = _phone.Normalize(info.Phone, product.PhonePrefix);
                }
                if (string.IsNullOrWhiteSpace(lead.InstagramHandle) && !string.IsNullOrWhiteSpace(info.InstagramHandle))
                    lead.InstagramHandle = info.InstagramHandle;
                if (string.IsNullOrWhiteSpace(lead.FacebookUrl) && !string.IsNullOrWhiteSpace(info.FacebookUrl))
                    lead.FacebookUrl = info.FacebookUrl;
            }

            // Quality filter: descartar leads que no sirven para venta.
            if (!PassesQualityFilter(lead))
            {
                discarded++;
                continue;
            }

            // Dedup: same product_key + whatsapp_phone, OR same product_key + place_id.
            var exists = false;
            if (!string.IsNullOrWhiteSpace(lead.WhatsappPhone))
            {
                exists = await _db.Leads.AnyAsync(l => l.ProductKey == product.ProductKey && l.WhatsappPhone == lead.WhatsappPhone, ct);
            }
            if (!exists && !string.IsNullOrWhiteSpace(lead.PlaceId))
            {
                exists = await _db.Leads.AnyAsync(l => l.ProductKey == product.ProductKey && l.PlaceId == lead.PlaceId, ct);
            }
            if (exists) continue;

            lead.Id = Guid.NewGuid();
            lead.Product = product;
            lead.Source = source;
            lead.Status = LeadStatus.New;

            _db.Leads.Add(lead);
            created++;

            // Region-aware: prioriza gid2 (M:N seller_localities); si el lead no trae
            // gid2, cae al matching por string (provincia/ciudad). Sin owner → round-robin
            // entre los sin-región o global como último recurso.
            var sellerId = await _assigner.PickForLeadAsync(product.ProductKey, lead.LocalityGid2, lead.Province, lead.City, ct);
            if (sellerId is not null)
            {
                lead.SellerId = sellerId;
                lead.AssignedAt = DateTimeOffset.UtcNow;
                lead.Status = LeadStatus.Assigned;

                var seller = await _db.Sellers.Include(s => s.EvolutionInstance).FirstAsync(s => s.Id == sellerId.Value, ct);
                lead.RenderedMessage = _renderer.Render(lead, product, seller);
                lead.WhatsappLink = BuildWhatsappLink(lead.WhatsappPhone, lead.RenderedMessage);

                if (autoQueue && seller.SendingEnabled && seller.EvolutionInstance is { Status: InstanceStatus.Connected } inst && !string.IsNullOrWhiteSpace(lead.WhatsappPhone))
                {
                    OutboxEnqueueHelper.EnqueueLeadMessages(
                        _db, _renderer, lead, product, seller,
                        lead.WhatsappPhone, inst.InstanceName);
                    lead.Status = LeadStatus.Queued;
                    lead.QueuedAt = DateTimeOffset.UtcNow;
                }
                // Fallback Instagram: sin WhatsApp pero con handle de IG → DM por IG.
                else if (autoQueue && seller.SendingEnabled
                         && await TryQueueInstagramAsync(lead, product, seller, ct))
                {
                    lead.Status = LeadStatus.Queued;
                    lead.QueuedAt = DateTimeOffset.UtcNow;
                }
            }
        }
        await _db.SaveChangesAsync(ct);
        return created;
    }

    /// <summary>
    /// Si el lead no tiene WhatsApp pero sí <see cref="Lead.InstagramHandle"/> y existe
    /// al menos una cuenta de IG disponible (la del seller o cualquiera), encola el
    /// outreach inicial por el canal Instagram. WhatsApp tiene prioridad: si el lead
    /// tiene teléfono, no encolamos por IG para no contactar dos veces.
    /// Devuelve true si encoló algo.
    /// </summary>
    private async Task<bool> TryQueueInstagramAsync(Lead lead, Product product, Seller seller, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(lead.InstagramHandle)) return false;
        if (!string.IsNullOrWhiteSpace(lead.WhatsappPhone)) return false;

        // ¿Hay alguna cuenta de IG que pueda mandar? (el sender elige la del seller
        // o cualquiera; acá sólo chequeamos que exista, el login se valida al enviar.)
        var hasIgAccount = await _db.InstagramAccounts
            .AnyAsync(a => a.IsActive && !a.IsActionBlocked, ct);
        if (!hasIgAccount) return false;

        var n = OutboxEnqueueHelper.EnqueueLeadMessages(
            _db, _renderer, lead, product, seller,
            whatsappPhone: string.Empty, instanceName: string.Empty,
            scheduledAt: null, channel: MessageChannel.Instagram);
        return n > 0;
    }

    public record ReassignOrphansResult(
        int Scanned,
        int Assigned,
        int Queued,
        Dictionary<string, int> StillOrphanByProduct);

    /// <summary>
    /// Re-corre el assigner sobre leads ya creados que quedaron sin vendedor (Status=New, SellerId=null).
    /// Útil cuando el admin recién acaba de configurar whitelist/regiones y quiere repartir el backlog.
    /// </summary>
    public async Task<ReassignOrphansResult> ReassignOrphansAsync(bool autoQueue, CancellationToken ct)
    {
        var orphans = await _db.Leads
            .Include(l => l.Product)
            .Where(l => l.SellerId == null && l.Status == LeadStatus.New)
            .OrderBy(l => l.CreatedAt)
            .ToListAsync(ct);

        var assigned = 0;
        var queued = 0;
        var stillOrphan = new Dictionary<string, int>();

        foreach (var lead in orphans)
        {
            if (lead.Product is null) continue;
            var sellerId = await _assigner.PickForLeadAsync(lead.ProductKey, lead.LocalityGid2, lead.Province, lead.City, ct);
            if (sellerId is null)
            {
                stillOrphan[lead.ProductKey] = stillOrphan.GetValueOrDefault(lead.ProductKey) + 1;
                continue;
            }
            lead.SellerId = sellerId;
            lead.AssignedAt = DateTimeOffset.UtcNow;
            lead.Status = LeadStatus.Assigned;
            var seller = await _db.Sellers.Include(s => s.EvolutionInstance).FirstAsync(s => s.Id == sellerId.Value, ct);
            lead.RenderedMessage = _renderer.Render(lead, lead.Product, seller);
            lead.WhatsappLink = BuildWhatsappLink(lead.WhatsappPhone, lead.RenderedMessage);
            assigned++;

            if (autoQueue
                && seller.SendingEnabled
                && seller.EvolutionInstance is { Status: InstanceStatus.Connected } inst
                && !string.IsNullOrWhiteSpace(lead.WhatsappPhone))
            {
                OutboxEnqueueHelper.EnqueueLeadMessages(
                    _db, _renderer, lead, lead.Product, seller,
                    lead.WhatsappPhone, inst.InstanceName);
                lead.Status = LeadStatus.Queued;
                lead.QueuedAt = DateTimeOffset.UtcNow;
                queued++;
            }
            // Fallback Instagram: sin WhatsApp pero con handle de IG → DM por IG.
            else if (autoQueue && seller.SendingEnabled
                     && await TryQueueInstagramAsync(lead, lead.Product, seller, ct))
            {
                lead.Status = LeadStatus.Queued;
                lead.QueuedAt = DateTimeOffset.UtcNow;
                queued++;
            }
        }

        await _db.SaveChangesAsync(ct);
        return new ReassignOrphansResult(orphans.Count, assigned, queued, stillOrphan);
    }

    public record ReassignByOwnerResult(
        int Scanned,
        int Reassigned,
        int Queued,
        int WaitingSellerOffline,
        int AlreadyOk,
        int PooledNoOwner,
        int NoProduct,
        Dictionary<string, int> NoOwnerByProduct);

    /// <summary>
    /// Reasignación por DUEÑO de la app (botón "Reasignar todo"). A diferencia del assigner normal
    /// —que solo asigna a vendedores listos para enviar AHORA— acá el criterio es la PROPIEDAD:
    /// cada lead sin contactar va al vendedor cuya whitelist incluye ese producto, esté conectado o
    /// no. Si hay varios dueños, prioriza uno LISTO (conectado + enviando) para que salga ya y balancea
    /// por carga; si ningún dueño está listo, igual se lo asigna al dueño (queda Assigned y sale solo
    /// cuando ese vendedor conecte — lo encola el LeadRebalancer). Dueños dedicados (whitelist con el
    /// producto) ganan sobre catch-all (whitelist vacía). Si NADIE tiene esa app → al pool + se reporta.
    /// Nunca toca leads que ya arrancaron conversación (SentAt/FirstReplyAt).
    /// </summary>
    public async Task<ReassignByOwnerResult> ReassignByOwnershipAsync(CancellationToken ct)
    {
        // Candidatos a DUEÑO (independiente de conexión): activos; Seller siempre, Admin solo con
        // whitelist explícita (para no arrastrarle todo al admin que la dejó vacía).
        var sellers = (await _db.Sellers.Include(s => s.EvolutionInstance)
                .Where(s => s.IsActive)
                .ToListAsync(ct))
            .Where(s => s.Role == SellerRole.Seller
                     || (s.Role == SellerRole.Admin && s.VerticalsWhitelist is { Count: > 0 }))
            .ToList();

        static bool IsReady(Seller s) =>
            s.SendingEnabled && s.EvolutionInstance is { Status: InstanceStatus.Connected };

        // Dueños de un producto: dedicados (whitelist lo contiene) primero; si no hay, catch-all
        // (whitelist vacía) como fallback.
        List<Seller> OwnersFor(string productKey)
        {
            var dedicated = sellers
                .Where(s => s.VerticalsWhitelist is { Count: > 0 } && s.VerticalsWhitelist.Contains(productKey))
                .ToList();
            return dedicated.Count > 0
                ? dedicated
                : sellers.Where(s => s.VerticalsWhitelist is not { Count: > 0 }).ToList();
        }

        // Balanceo por carga de las últimas 24h (mismo criterio que el assigner normal).
        var counts = await _db.Leads
            .Where(l => l.SellerId != null && l.AssignedAt >= DateTimeOffset.UtcNow.AddHours(-24))
            .GroupBy(l => l.SellerId!.Value)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count, ct);

        Seller PickOwner(List<Seller> owners)
        {
            var ready = owners.Where(IsReady).ToList();
            var pool = ready.Count > 0 ? ready : owners;
            return pool.OrderBy(s => counts.GetValueOrDefault(s.Id)).ThenBy(_ => Guid.NewGuid()).First();
        }

        var leads = await _db.Leads.Include(l => l.Product)
            .Where(l => l.SentAt == null && l.FirstReplyAt == null
                     && (l.Status == LeadStatus.New || l.Status == LeadStatus.Assigned || l.Status == LeadStatus.Queued))
            .ToListAsync(ct);

        // Fase 1: decidir (sin tocar la DB todavía).
        var moves = new List<(Lead lead, Seller target)>();
        var pools = new List<Lead>();
        var noOwner = new Dictionary<string, int>();
        var alreadyOk = 0;
        var noProduct = 0;

        foreach (var lead in leads)
        {
            if (lead.Product is null || string.IsNullOrWhiteSpace(lead.ProductKey)) { noProduct++; continue; }

            var owners = OwnersFor(lead.ProductKey);
            if (owners.Count == 0)
            {
                noOwner[lead.ProductKey] = noOwner.GetValueOrDefault(lead.ProductKey) + 1;
                if (lead.SellerId != null) pools.Add(lead); // pegado a alguien que no corresponde → pool
                continue;
            }

            // Ya está en un dueño válido → no lo movemos (evita churn y re-render inútil).
            if (lead.SellerId != null && owners.Any(o => o.Id == lead.SellerId.Value)) { alreadyOk++; continue; }

            var target = PickOwner(owners);
            counts[target.Id] = counts.GetValueOrDefault(target.Id) + 1;
            moves.Add((lead, target));
        }

        // Cancelar en bloque el outbox pendiente de todo lo que se mueve o se suelta (1 query por chunk).
        var affected = moves.Select(m => m.lead.Id).Concat(pools.Select(l => l.Id)).ToList();
        for (var i = 0; i < affected.Count; i += 1000)
        {
            var slice = affected.Skip(i).Take(1000).ToList();
            var pending = await _db.Outbox
                .Where(o => slice.Contains(o.LeadId)
                         && (o.Status == OutboxStatus.Scheduled || o.Status == OutboxStatus.Sending))
                .ToListAsync(ct);
            foreach (var o in pending) o.Status = OutboxStatus.Cancelled;
        }

        // Fase 2: aplicar.
        foreach (var lead in pools)
        {
            lead.SellerId = null; lead.AssignedAt = null; lead.QueuedAt = null; lead.Status = LeadStatus.New;
        }

        var reassigned = 0; var queued = 0; var waiting = 0;
        foreach (var (lead, target) in moves)
        {
            lead.SellerId = target.Id;
            lead.AssignedAt = DateTimeOffset.UtcNow;
            lead.QueuedAt = null;
            lead.RenderedMessage = _renderer.Render(lead, lead.Product!, target);
            lead.WhatsappLink = BuildWhatsappLink(lead.WhatsappPhone, lead.RenderedMessage);
            reassigned++;

            if (IsReady(target) && !string.IsNullOrWhiteSpace(lead.WhatsappPhone))
            {
                OutboxEnqueueHelper.EnqueueLeadMessages(
                    _db, _renderer, lead, lead.Product!, target,
                    lead.WhatsappPhone, target.EvolutionInstance!.InstanceName);
                lead.Status = LeadStatus.Queued; lead.QueuedAt = DateTimeOffset.UtcNow; queued++;
            }
            else if (IsReady(target) && await TryQueueInstagramAsync(lead, lead.Product!, target, ct))
            {
                lead.Status = LeadStatus.Queued; lead.QueuedAt = DateTimeOffset.UtcNow; queued++;
            }
            else
            {
                // Dueño desconectado/pausado: queda Assigned; el LeadRebalancer lo encola cuando conecte.
                lead.Status = LeadStatus.Assigned; waiting++;
            }
        }

        await _db.SaveChangesAsync(ct);
        return new ReassignByOwnerResult(
            leads.Count, reassigned, queued, waiting, alreadyOk, pools.Count, noProduct, noOwner);
    }

    /// <summary>
    /// Descarta leads que no valen la pena contactar: sin ningún canal, negocios cerrados,
    /// o establecimientos con rating bajo + suficientes reviews para confiar en el dato.
    /// </summary>
    private static bool PassesQualityFilter(Lead lead)
    {
        // Sin ningún canal de contacto no sirve.
        if (string.IsNullOrWhiteSpace(lead.WhatsappPhone)
            && string.IsNullOrWhiteSpace(lead.InstagramHandle)
            && string.IsNullOrWhiteSpace(lead.Website)
            && string.IsNullOrWhiteSpace(lead.FacebookUrl))
            return false;

        // Negocio cerrado permanentemente.
        if (!string.IsNullOrWhiteSpace(lead.BusinessStatus)
            && (lead.BusinessStatus.Equals("closed", StringComparison.OrdinalIgnoreCase)
                || lead.BusinessStatus.Equals("permanently_closed", StringComparison.OrdinalIgnoreCase)
                || lead.BusinessStatus.Equals("CLOSED_PERMANENTLY", StringComparison.OrdinalIgnoreCase)))
            return false;

        // Rating bajo con suficiente masa crítica = reputación mala consolidada.
        if (lead.Rating is { } r && r < 2.5 && lead.TotalReviews is { } n && n >= 10)
            return false;

        return true;
    }

    private static string? BuildWhatsappLink(string? phone, string? message)
    {
        if (string.IsNullOrWhiteSpace(phone)) return null;
        var text = Uri.EscapeDataString(message ?? string.Empty);
        return $"https://wa.me/{phone}?text={text}";
    }

    private async Task<(string? City, string? Province, string? Category)> PickTargetAsync(Product product, PipelineRunOptions opts, CancellationToken ct)
    {
        if (opts.City is not null) return (opts.City, opts.Province, opts.Category);

        // Pick oldest scraped city for this product in the country, biased by population.
        var cooldown = DateTimeOffset.UtcNow.AddDays(-30);
        var cities = await _db.Cities.Where(c => c.Country == product.Country).ToListAsync(ct);
        var recent = await _db.ScrapeLogs
            .Where(s => s.ProductKey == product.ProductKey && s.RunAt >= cooldown)
            .Select(s => s.City)
            .ToListAsync(ct);
        var recentSet = new HashSet<string>(recent.Where(r => r is not null)!, StringComparer.OrdinalIgnoreCase);

        var pool = cities.Where(c => !recentSet.Contains(c.City)).ToList();
        if (pool.Count == 0) pool = cities;
        if (pool.Count == 0) return (null, null, opts.Category);

        var pick = pool
            .OrderByDescending(c => (int)c.PopulationBucket)
            .ThenBy(_ => Guid.NewGuid())
            .First();
        return (pick.City, pick.Province, opts.Category);
    }
}
