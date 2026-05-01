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
            }
        }
        await _db.SaveChangesAsync(ct);
        return created;
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
        }

        await _db.SaveChangesAsync(ct);
        return new ReassignOrphansResult(orphans.Count, assigned, queued, stillOrphan);
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
