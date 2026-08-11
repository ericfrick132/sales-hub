using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SalesHub.Api.Dtos;
using SalesHub.Core.Abstractions;
using SalesHub.Core.Domain.Entities;
using SalesHub.Core.Domain.Enums;
using SalesHub.Infrastructure.Persistence;
using SalesHub.Infrastructure.Services;

namespace SalesHub.Api.Controllers;

[ApiController]
[Route("api/leads")]
[Authorize]
public class LeadsController : ControllerBase
{
    private readonly ApplicationDbContext _db;
    private readonly IMessageRenderer _renderer;
    private readonly PipelineService _pipeline;
    private readonly LeadRebalancer _rebalancer;
    private readonly IPhoneNormalizer _phone;
    private readonly IGooglePlacesEnricher _enricher;
    private readonly IEvolutionClient _evo;
    private readonly IProductStatusNotifier _statusNotifier;
    private readonly ILogger<LeadsController> _log;

    public LeadsController(
        ApplicationDbContext db,
        IMessageRenderer renderer,
        PipelineService pipeline,
        LeadRebalancer rebalancer,
        IPhoneNormalizer phone,
        IGooglePlacesEnricher enricher,
        IEvolutionClient evo,
        IProductStatusNotifier statusNotifier,
        ILogger<LeadsController> log)
    {
        _db = db; _renderer = renderer; _pipeline = pipeline; _rebalancer = rebalancer; _phone = phone; _enricher = enricher;
        _evo = evo; _statusNotifier = statusNotifier; _log = log;
    }

    public record AssignRequest(Guid SellerId, bool AutoQueue = true);

    [HttpPost("{id:guid}/assign")]
    public async Task<ActionResult<LeadDto>> Assign(Guid id, [FromBody] AssignRequest req, CancellationToken ct)
    {
        if (!CurrentUser.IsAdmin(User)) return Forbid();
        var lead = await _db.Leads.Include(l => l.Product).FirstOrDefaultAsync(l => l.Id == id, ct);
        if (lead is null) return NotFound();

        var seller = await _db.Sellers.Include(s => s.EvolutionInstance).FirstOrDefaultAsync(s => s.Id == req.SellerId, ct);
        if (seller is null) return BadRequest(new { error = "Vendedor no encontrado" });
        if (!seller.IsActive) return BadRequest(new { error = "Vendedor inactivo" });

        lead.SellerId = seller.Id;
        lead.AssignedAt = DateTimeOffset.UtcNow;
        lead.Status = LeadStatus.Assigned;
        if (lead.Product is not null)
        {
            lead.RenderedMessage = _renderer.Render(lead, lead.Product, seller);
            lead.WhatsappLink = string.IsNullOrWhiteSpace(lead.WhatsappPhone)
                ? null
                : $"https://wa.me/{lead.WhatsappPhone}?text={Uri.EscapeDataString(lead.RenderedMessage ?? "")}";
        }

        // Si el admin pidió encolar, lo hacemos siempre que haya instancia + teléfono + mensaje.
        // El OutboxSender va a chequear SendingEnabled + Status=Connected al momento de mandar,
        // así que es seguro encolar aunque el seller esté momentáneamente desconectado o pausado:
        // los items se quedan Scheduled hasta que el seller pueda mandar.
        // Idempotencia: si el lead ya tiene cadencia pendiente (Scheduled/Sending) no
        // re-encolamos — reasignar no debe duplicar mensajes.
        if (req.AutoQueue
            && seller.EvolutionInstance is not null
            && !string.IsNullOrWhiteSpace(lead.WhatsappPhone)
            && lead.RenderedMessage is not null
            && lead.Product is not null)
        {
            var alreadyQueued = await _db.Outbox.AnyAsync(
                o => o.LeadId == lead.Id
                  && (o.Status == OutboxStatus.Scheduled || o.Status == OutboxStatus.Sending), ct);
            if (!alreadyQueued)
            {
                OutboxEnqueueHelper.EnqueueLeadMessages(
                    _db, _renderer, lead, lead.Product, seller,
                    lead.WhatsappPhone, seller.EvolutionInstance.InstanceName);
                lead.Status = LeadStatus.Queued;
                lead.QueuedAt = DateTimeOffset.UtcNow;
            }
        }

        await _db.SaveChangesAsync(ct);
        lead.Seller = seller;
        return ToDto(lead);
    }

    [HttpPost("reassign-orphans")]
    public async Task<ActionResult<PipelineService.ReassignOrphansResult>> ReassignOrphans(
        [FromQuery] bool autoQueue = true, CancellationToken ct = default)
    {
        if (!CurrentUser.IsAdmin(User)) return Forbid();
        var result = await _pipeline.ReassignOrphansAsync(autoQueue, ct);
        return result;
    }

    /// <summary>
    /// Reasignación masiva del botón "Reasignar todo": corre el rebalanceo COMPLETO al instante
    /// (sin esperar el tick de 5 min). Suelta al pool los leads sin contactar pegados a un
    /// vendedor que ya no corresponde —por whitelist cambiada o por línea caída (force)—, re-corre
    /// el assigner sobre todo el pool y drena backlog hacia las líneas dedicadas. Solo toca leads
    /// que todavía no arrancaron conversación (sin enviar / sin responder).
    /// </summary>
    [HttpPost("rebalance-now")]
    public async Task<ActionResult<LeadRebalancer.RebalanceResult>> RebalanceNow(CancellationToken ct = default)
    {
        if (!CurrentUser.IsAdmin(User)) return Forbid();
        var result = await _rebalancer.RebalanceAsync(ct, force: true);
        _log.LogWarning("rebalance-now (manual): {Mismatch} liberados por whitelist, {Rescued} rescatados, {Orphans} asignados, {Drained} drenados",
            result.MismatchReleased, result.Rescued, result.OrphansAssigned, result.Drained);
        return result;
    }

    /// <summary>
    /// Botón "Reasignar todo": reasigna cada lead sin contactar al VENDEDOR DUEÑO de esa app
    /// (whitelist), esté conectado o no. Ver <see cref="PipelineService.ReassignByOwnershipAsync"/>.
    /// </summary>
    [HttpPost("reassign-by-owner")]
    public async Task<ActionResult<PipelineService.ReassignByOwnerResult>> ReassignByOwner(CancellationToken ct = default)
    {
        if (!CurrentUser.IsAdmin(User)) return Forbid();
        var result = await _pipeline.ReassignByOwnershipAsync(ct);
        _log.LogWarning("reassign-by-owner (manual): {Reassigned} reasignados ({Queued} en cola, {Waiting} esperando conexión), {AlreadyOk} ya ok, {Pooled} al pool sin dueño, {NoProduct} sin producto",
            result.Reassigned, result.Queued, result.WaitingSellerOffline, result.AlreadyOk, result.PooledNoOwner, result.NoProduct);
        return result;
    }

    public record BulkReassignRequest(
        Guid SellerId,
        LeadSource[]? Source,
        string? ProductKey,
        LeadStatus? Status,
        Guid? FromSellerId,
        bool IncludeContacted = true,
        bool AutoQueue = true,
        bool DryRun = false);

    public record BulkReassignResult(
        int Matched, int AlreadyOnTarget, int MovedUncontacted, int MovedContacted,
        int SkippedContacted, int Queued, int WaitingNoInstance, int OutboxCancelled, bool DryRun);

    /// <summary>
    /// Reasignación masiva por filtro: manda TODOS los leads que matchean (origen, app, estado,
    /// vendedor actual) a UN vendedor elegido. Caso típico: "todos los Meta Lead Ads a tal vendedor".
    /// Los leads sin contactar se re-renderizan y (con AutoQueue) se encolan en la línea nueva;
    /// los que ya tienen conversación solo cambian de dueño y se les cancela cualquier envío
    /// pendiente para que la línea vieja no les siga escribiendo. Con DryRun=true devuelve los
    /// contadores sin tocar nada (preview del modal).
    /// </summary>
    [HttpPost("bulk-reassign")]
    public async Task<ActionResult<BulkReassignResult>> BulkReassign([FromBody] BulkReassignRequest req, CancellationToken ct)
    {
        if (!CurrentUser.IsAdmin(User)) return Forbid();

        // Sin filtro moverías la base entera de un click — lo bloqueamos acá y no en la UI.
        var hasFilter = req.Source is { Length: > 0 } || !string.IsNullOrWhiteSpace(req.ProductKey)
                     || req.Status is not null || req.FromSellerId is not null;
        if (!hasFilter) return BadRequest(new { error = "Elegí al menos un filtro (origen, app, estado o vendedor actual)" });

        var seller = await _db.Sellers.Include(s => s.EvolutionInstance).FirstOrDefaultAsync(s => s.Id == req.SellerId, ct);
        if (seller is null) return BadRequest(new { error = "Vendedor no encontrado" });
        if (!seller.IsActive) return BadRequest(new { error = "Vendedor inactivo" });

        var q = _db.Leads.Include(l => l.Product).AsQueryable();
        if (req.Source is { Length: > 0 }) q = q.Where(l => req.Source.Contains(l.Source));
        if (!string.IsNullOrWhiteSpace(req.ProductKey)) q = q.Where(l => l.ProductKey == req.ProductKey);
        if (req.Status is not null) q = q.Where(l => l.Status == req.Status);
        if (req.FromSellerId is not null) q = q.Where(l => l.SellerId == req.FromSellerId);
        var leads = await q.ToListAsync(ct);

        // Con conversación arrancada = ya salió algo o ya respondió: cambiar de dueño es solo
        // bookkeeping (la charla sigue a mano); sin contactar = se puede re-encolar tranquilo.
        static bool Contacted(Lead l) => l.SentAt != null || l.FirstReplyAt != null
            || (l.Status != LeadStatus.New && l.Status != LeadStatus.Assigned && l.Status != LeadStatus.Queued);

        var alreadyOnTarget = leads.Count(l => l.SellerId == seller.Id);
        var candidates = leads.Where(l => l.SellerId != seller.Id).ToList();
        var uncontacted = candidates.Where(l => !Contacted(l)).ToList();
        var contacted = candidates.Where(Contacted).ToList();
        var skippedContacted = req.IncludeContacted ? 0 : contacted.Count;
        if (!req.IncludeContacted) contacted.Clear();

        var canQueue = seller.EvolutionInstance is not null;
        var queued = 0; var waiting = 0;

        // Cancelar el outbox pendiente de todo lo que se mueve: los sin contactar se re-encolan
        // en la línea nueva; a los contactados no les puede seguir escribiendo la línea vieja.
        var affected = uncontacted.Select(l => l.Id).Concat(contacted.Select(l => l.Id)).ToList();
        var outboxCancelled = 0;
        for (var i = 0; i < affected.Count; i += 1000)
        {
            var slice = affected.Skip(i).Take(1000).ToList();
            var pendingQ = _db.Outbox.Where(o => slice.Contains(o.LeadId)
                && (o.Status == OutboxStatus.Scheduled || o.Status == OutboxStatus.Sending));
            if (req.DryRun) { outboxCancelled += await pendingQ.CountAsync(ct); continue; }
            var pending = await pendingQ.ToListAsync(ct);
            foreach (var o in pending) o.Status = OutboxStatus.Cancelled;
            outboxCancelled += pending.Count;
        }

        foreach (var lead in uncontacted)
        {
            var queueable = req.AutoQueue && canQueue && lead.Product is not null && !string.IsNullOrWhiteSpace(lead.WhatsappPhone);
            if (queueable) queued++; else waiting++;
            if (req.DryRun) continue;

            lead.SellerId = seller.Id;
            lead.AssignedAt = DateTimeOffset.UtcNow;
            lead.QueuedAt = null;
            if (lead.Product is not null)
            {
                lead.RenderedMessage = _renderer.Render(lead, lead.Product, seller);
                lead.WhatsappLink = string.IsNullOrWhiteSpace(lead.WhatsappPhone)
                    ? null
                    : $"https://wa.me/{lead.WhatsappPhone}?text={Uri.EscapeDataString(lead.RenderedMessage ?? "")}";
            }
            if (queueable)
            {
                // Igual que el assign individual: encolamos aunque el seller esté desconectado;
                // el OutboxSender chequea Connected + SendingEnabled al momento de mandar.
                OutboxEnqueueHelper.EnqueueLeadMessages(
                    _db, _renderer, lead, lead.Product!, seller,
                    lead.WhatsappPhone!, seller.EvolutionInstance!.InstanceName);
                lead.Status = LeadStatus.Queued;
                lead.QueuedAt = DateTimeOffset.UtcNow;
            }
            else
            {
                lead.Status = LeadStatus.Assigned;
            }
        }

        if (!req.DryRun)
        {
            foreach (var lead in contacted)
            {
                // Solo cambia el dueño: estado y AssignedAt quedan como estaban para no
                // reordenar el historial ni pisar métricas de la conversación.
                lead.SellerId = seller.Id;
            }
            await _db.SaveChangesAsync(ct);
            _log.LogWarning(
                "bulk-reassign → {Seller}: {Uncontacted} sin contactar ({Queued} encolados), {Contacted} con conversación, {Cancelled} outbox cancelados (filtros: source={Source} product={Product} status={Status} from={From})",
                seller.DisplayName, uncontacted.Count, queued, contacted.Count, outboxCancelled,
                req.Source is { Length: > 0 } ? string.Join(",", req.Source) : "-", req.ProductKey ?? "-", req.Status?.ToString() ?? "-", req.FromSellerId?.ToString() ?? "-");
        }

        return new BulkReassignResult(
            leads.Count, alreadyOnTarget, uncontacted.Count, contacted.Count,
            skippedContacted, queued, waiting, outboxCancelled, req.DryRun);
    }

    /// <summary>
    /// Limpieza puntual: leads WhatsAppInbound duplicados (mismo teléfono+producto) que dejó
    /// el bug del chat-sync (dedup post-creación → leads fantasma en serie). Borra SOLO los
    /// que no tienen ningún mensaje; conserva el que tiene el hilo (o el más viejo).
    /// </summary>
    [HttpPost("dedup-inbound")]
    public async Task<IActionResult> DedupInbound(CancellationToken ct = default)
    {
        if (!CurrentUser.IsAdmin(User)) return Forbid();

        var inbound = await _db.Leads
            .Where(l => l.Source == Core.Domain.Enums.LeadSource.WhatsAppInbound)
            .ToListAsync(ct);

        var msgCounts = await _db.ConversationMessages
            .GroupBy(m => m.LeadId)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count, ct);

        var toDelete = new List<Core.Domain.Entities.Lead>();
        foreach (var group in inbound
                     .GroupBy(l => (l.ProductKey, l.WhatsappPhone))
                     .Where(g => g.Count() > 1))
        {
            var keeper = group
                .OrderByDescending(l => msgCounts.GetValueOrDefault(l.Id))
                .ThenBy(l => l.CreatedAt)
                .First();
            toDelete.AddRange(group.Where(l =>
                l.Id != keeper.Id && msgCounts.GetValueOrDefault(l.Id) == 0));
        }

        if (toDelete.Count > 0)
        {
            _db.Leads.RemoveRange(toDelete);
            await _db.SaveChangesAsync(ct);
        }
        _log.LogWarning("dedup-inbound: {N} leads fantasma borrados", toDelete.Count);
        return Ok(new { deleted = toDelete.Count });
    }

    public record MapLeadDto(
        Guid Id, string Name, string ProductKey, string? City, string? Province, string? Address,
        string? WhatsappPhone, string? SellerName,
        double Latitude, double Longitude, Core.Domain.Enums.LeadStatus Status, Guid? SellerId);

    [HttpGet("map")]
    public async Task<ActionResult<IEnumerable<MapLeadDto>>> Map(
        [FromQuery] string? productKey, [FromQuery] Guid? sellerId,
        [FromQuery] int limit = 2000, CancellationToken ct = default)
    {
        // Admins see all leads; sellers see only their own.
        var isAdmin = CurrentUser.IsAdmin(User);
        var callerId = CurrentUser.Id(User);

        var q = _db.Leads.AsNoTracking()
            .Where(l => l.Latitude != null && l.Longitude != null);
        if (!isAdmin) q = q.Where(l => l.SellerId == callerId);
        else if (sellerId is not null) q = q.Where(l => l.SellerId == sellerId);
        if (!string.IsNullOrWhiteSpace(productKey)) q = q.Where(l => l.ProductKey == productKey);

        var rows = await q.Include(l => l.Seller).OrderByDescending(l => l.CreatedAt).Take(Math.Min(limit, 5000))
            .Select(l => new MapLeadDto(l.Id, l.Name, l.ProductKey, l.City, l.Province, l.Address,
                l.WhatsappPhone, l.Seller != null ? l.Seller.DisplayName : null,
                l.Latitude!.Value, l.Longitude!.Value, l.Status, l.SellerId))
            .ToListAsync(ct);
        return rows;
    }

    public record GeoStatsProductCount(string ProductKey, string? ProductName, int Count);
    public record GeoStatsLastJob(
        Guid Id, string ProductKey, string? ProductName, string? Category,
        string Query, int LeadsCreated, int RawItems,
        Guid SellerId, string? SellerName, DateTimeOffset CapturedAt);
    public record GeoStatsCellDto(
        string LocalityGid2,
        string LocalityName,
        string AdminLevel1Name,
        string CountryCode,
        double CentroidLat,
        double CentroidLng,
        int LeadsCount,
        IEnumerable<GeoStatsProductCount> Products,
        GeoStatsLastJob? LastJob,
        IEnumerable<string> AssignedSellers);

    /// <summary>
    /// Cobertura por localidad (GADM gid2): cuántos leads se cargaron en cada
    /// zona, breakdown por producto y último search-job (qué query/categoría
    /// trajo los últimos leads). Para pintar el mapa de "país completado".
    /// </summary>
    [HttpGet("geo-stats")]
    public async Task<ActionResult<IEnumerable<GeoStatsCellDto>>> GeoStats(
        [FromQuery] string? productKey,
        [FromQuery] string? category,
        [FromQuery] int days = 90,
        [FromQuery] Guid? sellerId = null,
        CancellationToken ct = default)
    {
        var isAdmin = CurrentUser.IsAdmin(User);
        var callerId = CurrentUser.Id(User);
        var since = DateTimeOffset.UtcNow.AddDays(-Math.Clamp(days, 1, 365));

        var leadsQ = _db.Leads.AsNoTracking()
            .Where(l => l.LocalityGid2 != null && l.CreatedAt >= since);
        if (!isAdmin) leadsQ = leadsQ.Where(l => l.SellerId == callerId);
        else if (sellerId is not null) leadsQ = leadsQ.Where(l => l.SellerId == sellerId);
        if (!string.IsNullOrWhiteSpace(productKey)) leadsQ = leadsQ.Where(l => l.ProductKey == productKey);
        if (!string.IsNullOrWhiteSpace(category)) leadsQ = leadsQ.Where(l => l.SearchCategory == category);

        // Aggregation: count per (gid2, productKey).
        var perCell = await leadsQ
            .GroupBy(l => new { l.LocalityGid2, l.ProductKey })
            .Select(g => new { g.Key.LocalityGid2, g.Key.ProductKey, Count = g.Count() })
            .ToListAsync(ct);

        if (perCell.Count == 0) return new List<GeoStatsCellDto>();

        var gidSet = perCell.Select(x => x.LocalityGid2!).Distinct().ToList();
        var localities = await _db.Localities.AsNoTracking()
            .Where(l => gidSet.Contains(l.Gid2))
            .ToDictionaryAsync(l => l.Gid2, ct);

        var productNames = await _db.Products.AsNoTracking()
            .ToDictionaryAsync(p => p.ProductKey, p => p.DisplayName, ct);

        // Last search job per gid2 (under same scope).
        var jobsQ = _db.SearchJobs.AsNoTracking()
            .Include(j => j.Seller)
            .Where(j => j.LocalityGid2 != null && gidSet.Contains(j.LocalityGid2!));
        if (!isAdmin) jobsQ = jobsQ.Where(j => j.SellerId == callerId);
        else if (sellerId is not null) jobsQ = jobsQ.Where(j => j.SellerId == sellerId);
        if (!string.IsNullOrWhiteSpace(productKey)) jobsQ = jobsQ.Where(j => j.ProductKey == productKey);
        if (!string.IsNullOrWhiteSpace(category)) jobsQ = jobsQ.Where(j => j.Category == category);

        var lastJobs = await jobsQ
            .GroupBy(j => j.LocalityGid2!)
            .Select(g => g.OrderByDescending(j => j.ScheduledAt).First())
            .ToListAsync(ct);
        var lastJobByGid = lastJobs.ToDictionary(j => j.LocalityGid2!);

        // Sellers assigned to each locality in the result set.
        var assignedByGid = await _db.SellerLocalities.AsNoTracking()
            .Where(sl => gidSet.Contains(sl.LocalityGid2))
            .Select(sl => new { sl.LocalityGid2, sl.Seller!.DisplayName })
            .ToListAsync(ct);
        var assignedMap = assignedByGid
            .GroupBy(x => x.LocalityGid2)
            .ToDictionary(g => g.Key, g => g.Select(x => x.DisplayName).OrderBy(n => n).ToList());

        var byGid = perCell.GroupBy(x => x.LocalityGid2!).Select(g =>
        {
            var gid = g.Key;
            var products = g.Select(x => new GeoStatsProductCount(
                x.ProductKey,
                productNames.GetValueOrDefault(x.ProductKey),
                x.Count)).OrderByDescending(p => p.Count).ToList();
            var total = products.Sum(p => p.Count);
            localities.TryGetValue(gid, out var loc);
            GeoStatsLastJob? last = null;
            if (lastJobByGid.TryGetValue(gid, out var j))
            {
                last = new GeoStatsLastJob(
                    j.Id, j.ProductKey, productNames.GetValueOrDefault(j.ProductKey),
                    j.Category, j.Query, j.LeadsCreated, j.RawItems,
                    j.SellerId, j.Seller?.DisplayName,
                    j.FinishedAt ?? j.ScheduledAt);
            }
            return new GeoStatsCellDto(
                gid,
                loc?.Name ?? gid,
                loc?.AdminLevel1Name ?? string.Empty,
                loc?.CountryCode ?? string.Empty,
                loc?.CentroidLat ?? 0,
                loc?.CentroidLng ?? 0,
                total,
                products,
                last,
                assignedMap.GetValueOrDefault(gid) ?? new List<string>());
        }).OrderByDescending(c => c.LeadsCount).ToList();

        return byGid;
    }

    [HttpGet("mine")]
    public async Task<ActionResult<IEnumerable<LeadDto>>> Mine(
        [FromQuery] LeadStatus? status, [FromQuery] string? productKey, [FromQuery] Guid? sellerId,
        [FromQuery] LeadSource[]? source, [FromQuery] int limit = 200, CancellationToken ct = default)
    {
        var isAdmin = CurrentUser.IsAdmin(User);
        var callerId = CurrentUser.Id(User);
        var q = _db.Leads.AsNoTracking()
            .Include(l => l.Product)
            .Include(l => l.Seller)
            .AsQueryable();
        // Admins see all leads (with optional ?sellerId= filter); sellers see only their own.
        if (!isAdmin) q = q.Where(l => l.SellerId == callerId);
        else if (sellerId is not null) q = q.Where(l => l.SellerId == sellerId);
        if (status is not null) q = q.Where(l => l.Status == status);
        if (!string.IsNullOrWhiteSpace(productKey)) q = q.Where(l => l.ProductKey == productKey);
        // Filtro por fuente(s): permite ?source=MetaLeadAd&source=WhatsAppAd (drill-down de anuncios).
        if (source is { Length: > 0 }) q = q.Where(l => source.Contains(l.Source));
        q = q.OrderByDescending(l => l.AssignedAt ?? l.CreatedAt).Take(Math.Min(limit, 500));
        return (await q.ToListAsync(ct)).Select(ToDto).ToList();
    }

    /// <summary>
    /// Devuelve la cadencia que efectivamente se mandaría a este lead, renderizada
    /// con los placeholders del producto. Usa <see cref="OutboxEnqueueHelper.ResolveStepsForLead"/>
    /// para respetar el override por categoría (mismo motor que el envío real).
    /// Si el producto no tiene steps, devuelve <c>HasSteps=false</c> + el template legacy.
    /// </summary>
    [HttpGet("{id:guid}/preview")]
    public async Task<ActionResult<LeadPreviewDto>> Preview(Guid id, CancellationToken ct)
    {
        var callerId = CurrentUser.Id(User);
        var lead = await _db.Leads
            .Include(l => l.Product)
            .Include(l => l.Seller)
            .FirstOrDefaultAsync(l => l.Id == id, ct);
        if (lead is null) return NotFound();
        if (!CurrentUser.IsAdmin(User) && lead.SellerId != callerId) return Forbid();
        if (lead.Product is null) return BadRequest(new { error = "Lead sin producto" });

        var (steps, category) = OutboxEnqueueHelper.ResolveStepsForLead(lead, lead.Product);

        if (steps.Count == 0)
        {
            var legacy = !string.IsNullOrWhiteSpace(lead.RenderedMessage)
                ? lead.RenderedMessage!
                : _renderer.Render(lead, lead.Product, lead.Seller);
            return new LeadPreviewDto(false, string.Empty, legacy, new List<LeadPreviewStepDto>());
        }

        // Resolver nombres + tipos de los assets que aparecen en algún step.
        // Tomamos sólo la PRIMERA variante de cada step para el preview (la rotación
        // round-robin se decide al enqueue real, acá queremos algo determinístico).
        var firstAssetIds = steps
            .Select(s => s.MediaAssetIds is { Count: > 0 } ? s.MediaAssetIds[0] : s.MediaAssetId)
            .Where(g => g is not null)
            .Select(g => g!.Value)
            .Distinct()
            .ToList();
        var assetById = firstAssetIds.Count == 0
            ? new Dictionary<Guid, (string FileName, string MimeType)>()
            : await _db.MediaAssets.AsNoTracking()
                .Where(a => firstAssetIds.Contains(a.Id))
                .Select(a => new { a.Id, a.FileName, a.MimeType })
                .ToDictionaryAsync(a => a.Id, a => (a.FileName, a.MimeType), ct);

        var result = new List<LeadPreviewStepDto>();
        for (var i = 0; i < steps.Count; i++)
        {
            var step = steps[i];
            var hasMulti = step.MediaAssetIds is { Count: > 0 };
            var firstAsset = hasMulti ? step.MediaAssetIds[0] : step.MediaAssetId;
            int? variants = hasMulti
                ? step.MediaAssetIds.Count
                : (step.MediaAssetId is not null ? 1 : (int?)null);

            string? kind = null;
            string? fileName = null;
            if (firstAsset is not null && assetById.TryGetValue(firstAsset.Value, out var info))
            {
                fileName = info.FileName;
                var mt = info.MimeType ?? string.Empty;
                if (mt.StartsWith("audio/", StringComparison.OrdinalIgnoreCase)) kind = "audio";
                else if (mt.StartsWith("image/", StringComparison.OrdinalIgnoreCase)) kind = "image";
                else if (mt.Equals("application/pdf", StringComparison.OrdinalIgnoreCase)) kind = "pdf";
                else if (mt.StartsWith("video/", StringComparison.OrdinalIgnoreCase)) kind = "video";
                else kind = "file";
            }

            var rendered = string.IsNullOrWhiteSpace(step.Text)
                ? string.Empty
                : _renderer.RenderTemplate(step.Text, lead, lead.Product, lead.Seller);

            // Mismo skip que OutboxEnqueueHelper: step sin texto y sin media = no se manda.
            if (string.IsNullOrWhiteSpace(rendered) && kind is null) continue;

            result.Add(new LeadPreviewStepDto(i, rendered, step.DelaySeconds, kind, fileName, variants));
        }

        return new LeadPreviewDto(true, category, null, result);
    }

    public record PhoneRepairRow(Guid LeadId, string Name, string ProductKey, string Status, string OldPhone, string? NewPhone, string Outcome);
    public record PhoneRepairResult(int Scanned, int Fixed, int Unfixable, int Duplicates, bool Applied, List<PhoneRepairRow> Rows);

    /// <summary>
    /// Repara teléfonos argentinos mal cargados (típico de formularios Meta: dígitos de más,
    /// 54 duplicado, 0/15, etc.). Determinístico primero; si quedan ambiguos genera candidatos
    /// y le pregunta a WhatsApp cuál existe (CheckNumbers vía una instancia conectada).
    /// Dry-run por default; con apply=true pisa el teléfono del lead Y de su outbox pendiente.
    /// Solo leads pre-envío (New/Assigned/Queued) — a los ya contactados no les cambia el número.
    /// </summary>
    [HttpPost("repair-phones")]
    public async Task<ActionResult<PhoneRepairResult>> RepairPhones(
        [FromQuery] bool apply = false, [FromQuery] string? productKey = null,
        [FromQuery] LeadSource? source = null, CancellationToken ct = default)
    {
        if (!CurrentUser.IsAdmin(User)) return Forbid();

        var pre = new[] { LeadStatus.New, LeadStatus.Assigned, LeadStatus.Queued };
        var q = _db.Leads.Include(l => l.Product)
            .Where(l => pre.Contains(l.Status) && l.WhatsappPhone != null && l.WhatsappPhone != "");
        if (!string.IsNullOrWhiteSpace(productKey)) q = q.Where(l => l.ProductKey == productKey);
        // Filtro por origen: el caso de uso real es MetaLeadAd (telefonos tipeados a mano en el
        // form). Los de Maps guardados como 54-sin-9 hoy mandan igual — no tocarlos en masa.
        if (source is not null) q = q.Where(l => l.Source == source);
        var leads = (await q.ToListAsync(ct)).Where(l => !_phone.IsCanonicalAr(l.WhatsappPhone)).ToList();

        var rows = new List<PhoneRepairRow>();
        var fixes = new Dictionary<Guid, string>();
        var pendingCheck = new List<(Lead lead, IReadOnlyList<string> candidates)>();

        foreach (var lead in leads)
        {
            var strict = _phone.NormalizeArStrict(lead.WhatsappPhone);
            if (strict is not null) { fixes[lead.Id] = strict; continue; }
            var candidates = _phone.ArRepairCandidates(lead.WhatsappPhone);
            if (candidates.Count == 0)
                rows.Add(new PhoneRepairRow(lead.Id, lead.Name, lead.ProductKey, lead.Status.ToString(),
                    lead.WhatsappPhone!, null, "unfixable"));
            else
                pendingCheck.Add((lead, candidates));
        }

        // Verificación contra WhatsApp de los ambiguos, en un solo batch por instancia conectada.
        if (pendingCheck.Count > 0)
        {
            var instance = await _db.EvolutionInstances.AsNoTracking()
                .Where(i => i.Status == Core.Domain.Enums.InstanceStatus.Connected)
                .OrderBy(i => i.InstanceName)
                .Select(i => i.InstanceName)
                .FirstOrDefaultAsync(ct);
            if (instance is null)
            {
                foreach (var (lead, _) in pendingCheck)
                    rows.Add(new PhoneRepairRow(lead.Id, lead.Name, lead.ProductKey, lead.Status.ToString(),
                        lead.WhatsappPhone!, null, "sin instancia conectada para verificar"));
            }
            else
            {
                var allCandidates = pendingCheck.SelectMany(p => p.candidates).Distinct().ToList();
                var existing = new HashSet<string>();
                foreach (var chunk in allCandidates.Chunk(50))
                {
                    var check = await _evo.CheckNumbersAsync(instance, chunk, ct);
                    foreach (var r in check.Where(r => r.Exists)) existing.Add(r.Number);
                }
                foreach (var (lead, candidates) in pendingCheck)
                {
                    // primero en orden de plausibilidad que exista en WhatsApp
                    var pick = candidates.FirstOrDefault(existing.Contains);
                    if (pick is null)
                        rows.Add(new PhoneRepairRow(lead.Id, lead.Name, lead.ProductKey, lead.Status.ToString(),
                            lead.WhatsappPhone!, null, "ningun candidato existe en WhatsApp"));
                    else fixes[lead.Id] = pick;
                }
            }
        }

        // Duplicados: si el número reparado ya es de otro lead, no pisamos (quedaría repetido).
        var fixedPhones = fixes.Values.Distinct().ToList();
        var taken = await _db.Leads.AsNoTracking()
            .Where(l => fixedPhones.Contains(l.WhatsappPhone!))
            .Select(l => new { l.Id, l.WhatsappPhone })
            .ToListAsync(ct);
        var takenByOther = taken.GroupBy(t => t.WhatsappPhone!).ToDictionary(g => g.Key, g => g.Select(x => x.Id).ToList());

        var dup = 0;
        foreach (var lead in leads)
        {
            if (!fixes.TryGetValue(lead.Id, out var newPhone)) continue;
            if (takenByOther.TryGetValue(newPhone, out var owners) && owners.Any(id => id != lead.Id))
            {
                dup++;
                rows.Add(new PhoneRepairRow(lead.Id, lead.Name, lead.ProductKey, lead.Status.ToString(),
                    lead.WhatsappPhone!, newPhone, "duplicado: ya existe un lead con ese numero"));
                fixes.Remove(lead.Id);
                continue;
            }
            rows.Add(new PhoneRepairRow(lead.Id, lead.Name, lead.ProductKey, lead.Status.ToString(),
                lead.WhatsappPhone!, newPhone, apply ? "fixed" : "fixable"));
        }

        if (apply && fixes.Count > 0)
        {
            var ids = fixes.Keys.ToList();
            var outbox = await _db.Outbox
                .Where(o => ids.Contains(o.LeadId) && o.Status == OutboxStatus.Scheduled)
                .ToListAsync(ct);
            foreach (var lead in leads)
            {
                if (!fixes.TryGetValue(lead.Id, out var newPhone)) continue;
                lead.WhatsappPhone = newPhone;
                lead.UpdatedAt = DateTimeOffset.UtcNow;
            }
            foreach (var o in outbox) o.WhatsappPhone = fixes[o.LeadId];
            await _db.SaveChangesAsync(ct);
        }

        return new PhoneRepairResult(leads.Count, fixes.Count, rows.Count(r => r.Outcome.StartsWith("unfixable") || r.Outcome.StartsWith("ningun") || r.Outcome.StartsWith("sin ")), dup, apply, rows);
    }

    public record ReviveResult(int Scanned, int Revived, bool Applied, Dictionary<string, int> ByProduct, List<string> Sample);

    /// <summary>
    /// Revive leads con envíos FANTASMA: la instancia aceptó el send (quedaron Sent) pero la
    /// sesión estaba zombie y nada llegó al WhatsApp real. Vuelve los leads a Queued, borra los
    /// mensajes salientes fantasma de la conversación y re-encola la cadencia desde cero.
    /// Solo leads Sent SIN respuesta (si respondió, el mensaje llegó — no es fantasma).
    /// Dry-run por default. El re-envío recién sale cuando el seller tenga envío ON y línea viva.
    /// </summary>
    [HttpPost("revive-phantom-sends")]
    public async Task<ActionResult<ReviveResult>> RevivePhantomSends(
        [FromQuery] string instance, [FromQuery] DateTimeOffset since,
        [FromQuery] bool apply = false, CancellationToken ct = default)
    {
        if (!CurrentUser.IsAdmin(User)) return Forbid();
        if (string.IsNullOrWhiteSpace(instance)) return BadRequest(new { error = "instance requerido" });

        var sellerIds = await _db.Sellers
            .Where(s => s.EvolutionInstance != null && s.EvolutionInstance.InstanceName == instance)
            .Select(s => s.Id).ToListAsync(ct);
        if (sellerIds.Count == 0) return BadRequest(new { error = $"Ningún seller usa la instancia '{instance}'" });

        var leads = await _db.Leads
            .Include(l => l.Product)
            .Include(l => l.Seller).ThenInclude(s => s!.EvolutionInstance)
            .Where(l => l.SellerId != null && sellerIds.Contains(l.SellerId.Value)
                     && l.Status == LeadStatus.Sent
                     && l.SentAt != null && l.SentAt >= since
                     && l.FirstReplyAt == null)
            .ToListAsync(ct);

        var byProduct = leads.GroupBy(l => l.ProductKey).ToDictionary(g => g.Key, g => g.Count());
        var sample = leads.Take(15).Select(l => $"{l.Name} ({l.ProductKey}, sent {l.SentAt:HH:mm})").ToList();

        var revived = 0;
        if (apply)
        {
            var leadIds = leads.Select(l => l.Id).ToList();
            // Mensajes salientes fantasma del hilo (nunca llegaron): fuera, así el re-envío
            // arranca limpio en /conversaciones y el bot no cree que ya hubo contacto.
            var phantoms = await _db.ConversationMessages
                .Where(m => leadIds.Contains(m.LeadId)
                         && m.Direction == MessageDirection.Outbound
                         && m.Timestamp >= since)
                .ToListAsync(ct);
            _db.ConversationMessages.RemoveRange(phantoms);

            foreach (var lead in leads)
            {
                if (lead.Product is null || lead.Seller?.EvolutionInstance is null
                    || string.IsNullOrWhiteSpace(lead.WhatsappPhone)) continue;
                lead.Status = LeadStatus.Queued;
                lead.SentAt = null;
                lead.QueuedAt = DateTimeOffset.UtcNow;
                lead.UpdatedAt = DateTimeOffset.UtcNow;
                OutboxEnqueueHelper.EnqueueLeadMessages(
                    _db, _renderer, lead, lead.Product, lead.Seller, lead.WhatsappPhone,
                    lead.Seller.EvolutionInstance.InstanceName);
                revived++;
            }
            await _db.SaveChangesAsync(ct);
            _log.LogWarning("revive-phantom-sends: {N} leads re-encolados (instancia {Inst}, desde {Since})",
                revived, instance, since);
        }

        return new ReviveResult(leads.Count, revived, apply, byProduct, sample);
    }

    [HttpGet("pool")]
    public async Task<ActionResult<IEnumerable<LeadDto>>> Pool(
        [FromQuery] string? productKey, [FromQuery] int limit = 200, CancellationToken ct = default)
    {
        var q = _db.Leads.AsNoTracking()
            .Include(l => l.Product)
            .Where(l => l.SellerId == null && l.Status == LeadStatus.New);
        if (!string.IsNullOrWhiteSpace(productKey)) q = q.Where(l => l.ProductKey == productKey);
        q = q.OrderByDescending(l => l.CreatedAt).Take(Math.Min(limit, 500));
        return (await q.ToListAsync(ct)).Select(ToDto).ToList();
    }

    [HttpPost("{id:guid}/claim")]
    public async Task<ActionResult<LeadDto>> Claim(Guid id, CancellationToken ct)
    {
        var sellerId = CurrentUser.Id(User);
        var lead = await _db.Leads.Include(l => l.Product).FirstOrDefaultAsync(l => l.Id == id, ct);
        if (lead is null) return NotFound();
        if (lead.SellerId is not null && lead.SellerId != sellerId) return Conflict();

        var seller = await _db.Sellers.FirstAsync(s => s.Id == sellerId, ct);
        lead.SellerId = sellerId;
        lead.AssignedAt = DateTimeOffset.UtcNow;
        lead.Status = LeadStatus.Assigned;
        if (lead.Product is not null)
        {
            lead.RenderedMessage = _renderer.Render(lead, lead.Product, seller);
            if (!string.IsNullOrWhiteSpace(lead.WhatsappPhone))
                lead.WhatsappLink = $"https://wa.me/{lead.WhatsappPhone}?text={Uri.EscapeDataString(lead.RenderedMessage ?? "")}";
        }
        await _db.SaveChangesAsync(ct);
        lead.Seller = seller;
        return ToDto(lead);
    }

    [HttpPost("{id:guid}/release")]
    public async Task<ActionResult<LeadDto>> Release(Guid id, CancellationToken ct)
    {
        var sellerId = CurrentUser.Id(User);
        var lead = await _db.Leads.Include(l => l.Product).FirstOrDefaultAsync(l => l.Id == id, ct);
        if (lead is null) return NotFound();
        if (lead.SellerId != sellerId && !CurrentUser.IsAdmin(User)) return Forbid();

        // Cancelar la cadencia pendiente del seller saliente. Sin esto las rows quedan
        // Scheduled a nombre del seller viejo: si reconecta las manda igual, y mientras
        // tanto bloquean el re-encolado al asignar (chequeo de idempotencia de Assign).
        await CancelPendingOutboxAsync(new[] { lead.Id }, ct);

        lead.SellerId = null;
        lead.AssignedAt = null;
        lead.Status = LeadStatus.New;
        await _db.SaveChangesAsync(ct);
        return ToDto(lead);
    }

    public record ReleaseFrozenRequest(List<Guid> SellerIds);
    public record ReleaseFrozenResult(int LeadsReleased, int OutboxCancelled, Dictionary<string, int> BySeller);

    /// <summary>
    /// Rescate masivo: libera al pool los leads Assigned/Queued de los sellers indicados
    /// (típicamente desconectados hace tiempo) y cancela su outbox pendiente, para que
    /// reassign-orphans los reparta entre los sellers que sí pueden enviar.
    /// </summary>
    [HttpPost("release-frozen")]
    public async Task<ActionResult<ReleaseFrozenResult>> ReleaseFrozen(
        [FromBody] ReleaseFrozenRequest req, CancellationToken ct)
    {
        if (!CurrentUser.IsAdmin(User)) return Forbid();
        if (req.SellerIds is not { Count: > 0 }) return BadRequest(new { error = "SellerIds vacío" });

        var leads = await _db.Leads
            .Where(l => l.SellerId != null && req.SellerIds.Contains(l.SellerId.Value)
                     && (l.Status == LeadStatus.Assigned || l.Status == LeadStatus.Queued))
            .ToListAsync(ct);

        var cancelled = await CancelPendingOutboxAsync(leads.Select(l => l.Id).ToList(), ct);

        var bySeller = new Dictionary<string, int>();
        var sellerNames = await _db.Sellers
            .Where(s => req.SellerIds.Contains(s.Id))
            .ToDictionaryAsync(s => s.Id, s => s.DisplayName, ct);
        foreach (var lead in leads)
        {
            var name = sellerNames.GetValueOrDefault(lead.SellerId!.Value, lead.SellerId.Value.ToString());
            bySeller[name] = bySeller.GetValueOrDefault(name) + 1;
            lead.SellerId = null;
            lead.AssignedAt = null;
            lead.QueuedAt = null;
            lead.Status = LeadStatus.New;
        }

        await _db.SaveChangesAsync(ct);
        _log.LogWarning("release-frozen: {Leads} leads liberados, {Rows} rows de outbox canceladas ({Sellers})",
            leads.Count, cancelled, string.Join(", ", bySeller.Select(kv => $"{kv.Key}={kv.Value}")));
        return new ReleaseFrozenResult(leads.Count, cancelled, bySeller);
    }

    private async Task<int> CancelPendingOutboxAsync(IReadOnlyCollection<Guid> leadIds, CancellationToken ct)
    {
        var pending = await _db.Outbox
            .Where(o => leadIds.Contains(o.LeadId)
                     && (o.Status == OutboxStatus.Scheduled || o.Status == OutboxStatus.Sending))
            .ToListAsync(ct);
        foreach (var row in pending) row.Status = OutboxStatus.Cancelled;
        return pending.Count;
    }

    [HttpPatch("{id:guid}")]
    public async Task<ActionResult<LeadDto>> UpdateStatus(Guid id, [FromBody] UpdateLeadStatusRequest req, CancellationToken ct)
    {
        var sellerId = CurrentUser.Id(User);
        var lead = await _db.Leads.Include(l => l.Product).Include(l => l.Seller).FirstOrDefaultAsync(l => l.Id == id, ct);
        if (lead is null) return NotFound();
        if (lead.SellerId != sellerId && !CurrentUser.IsAdmin(User)) return Forbid();

        lead.Status = req.Status;
        if (req.Notes is not null) lead.Notes = req.Notes;
        if (req.Status == LeadStatus.Replied && lead.FirstReplyAt is null) lead.FirstReplyAt = DateTimeOffset.UtcNow;
        if (req.Status == LeadStatus.DemoScheduled && lead.DemoScheduledAt is null) lead.DemoScheduledAt = DateTimeOffset.UtcNow;
        if (req.Status is LeadStatus.Closed or LeadStatus.Lost) lead.ClosedAt = DateTimeOffset.UtcNow;
        lead.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);

        // Status-back al producto de origen (no-op si el lead no es de producto o no hay
        // StatusWebhookUrl configurado para ese productKey).
        await _statusNotifier.NotifyAsync(
            lead.ProductKey, lead.ExternalId, req.Status.ToString(), req.Status == LeadStatus.Closed, ct);

        return ToDto(lead);
    }

    [HttpPatch("{id:guid}/info")]
    public async Task<ActionResult<LeadDto>> UpdateInfo(Guid id, [FromBody] UpdateLeadInfoRequest req, CancellationToken ct)
    {
        var sellerId = CurrentUser.Id(User);
        var lead = await _db.Leads.Include(l => l.Product).Include(l => l.Seller).FirstOrDefaultAsync(l => l.Id == id, ct);
        if (lead is null) return NotFound();
        if (lead.SellerId != sellerId && !CurrentUser.IsAdmin(User)) return Forbid();

        var name = req.Name?.Trim();
        if (!string.IsNullOrWhiteSpace(name)) lead.Name = name;

        var phoneChanged = false;
        if (req.WhatsappPhone is not null)
        {
            var phone = req.WhatsappPhone.Trim();
            var newPhone = string.IsNullOrWhiteSpace(phone) ? null : phone;
            if (newPhone != lead.WhatsappPhone)
            {
                lead.WhatsappPhone = newPhone;
                phoneChanged = true;
            }
        }

        if ((phoneChanged || !string.IsNullOrWhiteSpace(name)) && lead.Product is not null && lead.Seller is not null)
        {
            lead.RenderedMessage = _renderer.Render(lead, lead.Product, lead.Seller);
            lead.WhatsappLink = string.IsNullOrWhiteSpace(lead.WhatsappPhone)
                ? null
                : $"https://wa.me/{lead.WhatsappPhone}?text={Uri.EscapeDataString(lead.RenderedMessage ?? "")}";
        }

        lead.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);
        return ToDto(lead);
    }

    /// <summary>
    /// "Enviar ahora": dispara la cadencia del producto al lead inmediatamente,
    /// salteándose la cola humanizada (cap diario, ventana horaria, delays
    /// largos). Igualmente persiste todo: cada step queda como MessageOutbox
    /// Sent (para que las stats por audio cuenten) + ConversationMessage
    /// outbound + el lead pasa a Sent.
    /// </summary>
    [HttpPost("{id:guid}/send-now")]
    public async Task<IActionResult> SendNow(Guid id, CancellationToken ct)
    {
        // "Enviar ahora" es un request HTTP SINCRÓNICO: no puede bloquearse minutos o el proxy
        // lo mata (salía el 1er mensaje y después "fallaba"). A diferencia del sender de fondo,
        // acá NO esperamos los delays largos de la cadencia ni la duración COMPLETA del audio.
        const int MaxStepDelaySeconds = 3;   // cap chico del delay entre steps (mandamos casi de corrido)
        const int MaxRecordingSeconds = 4;   // "grabando…" un toque, no los ~75s reales del audio
        const int IntraStepDelayMs = 1500;   // texto previo + audio en mismo step (legacy)

        var callerId = CurrentUser.Id(User);
        var isAdmin = CurrentUser.IsAdmin(User);

        var lead = await _db.Leads
            .Include(l => l.Product)
            .Include(l => l.Seller).ThenInclude(s => s!.EvolutionInstance)
            .FirstOrDefaultAsync(l => l.Id == id, ct);
        if (lead is null) return NotFound();
        if (!isAdmin && lead.SellerId != callerId) return Forbid();
        if (string.IsNullOrWhiteSpace(lead.WhatsappPhone)) return BadRequest(new { error = "Lead sin teléfono WhatsApp" });
        if (lead.Product is null) return BadRequest(new { error = "Lead sin producto" });

        Seller? seller;
        // Si el lead no tiene seller asignado y el caller es seller, lo tomamos.
        // Si el lead ya tiene uno, recargamos para garantizar que la instancia
        // venga incluida (la lectura del lead.Seller con ThenInclude a veces
        // no trae la instancia si el FK es viejo).
        if (lead.SellerId is null)
        {
            if (isAdmin) return BadRequest(new { error = "Asigná el lead a un vendedor primero" });
            seller = await _db.Sellers.Include(s => s.EvolutionInstance).FirstOrDefaultAsync(s => s.Id == callerId, ct);
            if (seller is null) return BadRequest(new { error = "Vendedor inválido" });
            lead.SellerId = seller.Id;
            lead.AssignedAt = DateTimeOffset.UtcNow;
        }
        else
        {
            seller = await _db.Sellers.Include(s => s.EvolutionInstance)
                .FirstOrDefaultAsync(s => s.Id == lead.SellerId, ct);
            if (seller is null) return BadRequest(new { error = "El vendedor asignado al lead no existe" });
        }

        // ─── Ruta por dispositivo físico (bridge) ───────────────────────────
        // Si el vendedor tiene un celu vinculado, la línea vive ahí: encolamos la
        // cadencia con prioridad manual y el device la pullea con el pacing
        // anti-ban por línea (cap diario + gap). Evolution ni se consulta.
        var device = await _db.Devices
            .Where(d => d.SellerId == seller.Id)
            .OrderByDescending(d => d.LastHeartbeatAt ?? DateTimeOffset.MinValue)
            .FirstOrDefaultAsync(ct);
        if (device is not null)
            return await QueueSendNowForBridgeAsync(lead, seller, device, ct);

        if (seller.EvolutionInstance is null)
            return BadRequest(new { error = $"El vendedor {seller.DisplayName} no tiene un dispositivo vinculado ni WhatsApp configurado. Vinculá un celu en /sellers." });

        // Si la DB dice no-Connected, re-verificamos live. Y si Evolution
        // también dice "close"/"disconnected", intentamos despertar la
        // instancia con instance/connect (lo mismo que hace /connect en el
        // frontend antes de mostrar el status). Las sesiones de Baileys
        // entran en idle silencioso después de un rato sin uso, pero el
        // WhatsApp sigue vinculado — el connect las reanima.
        if (seller.EvolutionInstance.Status != InstanceStatus.Connected)
        {
            var instanceName = seller.EvolutionInstance.InstanceName;
            var live = await _evo.GetInstanceStatusAsync(instanceName, ct);
            if (live.Status is not "open" and not "connected")
            {
                await _evo.GetQrCodeAsync(instanceName, ct);
                await Task.Delay(800, ct);
                live = await _evo.GetInstanceStatusAsync(instanceName, ct);
            }
            if (live.Status is not "open" and not "connected")
            {
                var hint = seller.Id == callerId
                    ? "Andá a /connect y escaneá el QR."
                    : $"Asegurate que el vendedor {seller.DisplayName} tenga WhatsApp Connected (status real: {live.Status}).";
                return BadRequest(new { error = $"WhatsApp del vendedor no está conectado. {hint}" });
            }
            seller.EvolutionInstance.Status = InstanceStatus.Connected;
            await _db.SaveChangesAsync(ct);
        }

        // Cadencia: si la categoría del lead tiene override, esos steps; sino default.
        var (steps, _) = OutboxEnqueueHelper.ResolveStepsForLead(lead, lead.Product);
        var instance = seller.EvolutionInstance.InstanceName;

        // Cancelamos los outbox rows pendientes del lead para no duplicar.
        var pending = await _db.Outbox
            .Where(o => o.LeadId == lead.Id && (o.Status == OutboxStatus.Scheduled || o.Status == OutboxStatus.Sending))
            .ToListAsync(ct);
        foreach (var p in pending) p.Status = OutboxStatus.Cancelled;

        var sent = 0;
        // Si el producto no tiene steps, fallback al template legacy.
        if (steps.Count == 0)
        {
            var msg = !string.IsNullOrWhiteSpace(lead.RenderedMessage)
                ? lead.RenderedMessage!
                : _renderer.Render(lead, lead.Product, seller);
            var rec = await PersistSentAsync(lead, seller, instance, msg, mediaAssetId: null, ct);
            var ok = await _evo.SendTextAsync(instance, lead.WhatsappPhone!, msg, ct);
            if (!ok)
            {
                await MarkSendFailedAsync(rec, "Falló el envío", ct);
                return StatusCode(502, new { error = "Falló el envío" });
            }
            sent = 1;
        }
        else
        {
            var jid = $"{lead.WhatsappPhone}@s.whatsapp.net";
            for (var i = 0; i < steps.Count; i++)
            {
                var step = steps[i];
                var hasMedia = step.MediaAssetId is not null || (step.MediaAssetIds is { Count: > 0 });
                if (string.IsNullOrWhiteSpace(step.Text) && !hasMedia) continue;

                Guid? mediaAssetId = step.MediaAssetIds is { Count: > 0 } ? step.MediaAssetIds[0] : step.MediaAssetId;

                // Delay del paso: silencioso (la persona aún no empezó nada).
                // El indicador "grabando audio…" / "escribiendo…" lo mostramos
                // recién cuando estamos a punto de enviar — y para audio dura
                // exactamente la duración del archivo.
                if (i > 0)
                {
                    var d = Math.Min(Math.Max(0, step.DelaySeconds), MaxStepDelaySeconds);
                    if (d > 0) await Task.Delay(d * 1000, ct);
                }

                var rendered = string.IsNullOrWhiteSpace(step.Text)
                    ? string.Empty
                    : _renderer.RenderTemplate(step.Text, lead, lead.Product, seller);

                try
                {
                    if (mediaAssetId is null)
                    {
                        if (!string.IsNullOrWhiteSpace(rendered))
                        {
                            var rec = await PersistSentAsync(lead, seller, instance, rendered, null, ct);
                            var ok = await _evo.SendTextAsync(instance, lead.WhatsappPhone!, rendered, ct);
                            if (!ok)
                            {
                                await MarkSendFailedAsync(rec, $"Falló el step {i + 1} (texto)", ct);
                                return StatusCode(502, new { error = $"Falló el step {i + 1} (texto)" });
                            }
                            sent++;
                        }
                    }
                    else
                    {
                        var asset = await _db.MediaAssets.AsNoTracking().FirstOrDefaultAsync(m => m.Id == mediaAssetId, ct);
                        if (asset is null) return BadRequest(new { error = $"Step {i + 1}: media no existe" });

                        if (asset.MimeType.StartsWith("audio/", StringComparison.OrdinalIgnoreCase))
                        {
                            if (!string.IsNullOrWhiteSpace(rendered))
                            {
                                var recPre = await PersistSentAsync(lead, seller, instance, rendered, null, ct);
                                var pre = await _evo.SendTextAsync(instance, lead.WhatsappPhone!, rendered, ct);
                                if (!pre)
                                {
                                    await MarkSendFailedAsync(recPre, $"Falló el step {i + 1} (texto previo al audio)", ct);
                                    return StatusCode(502, new { error = $"Falló el step {i + 1} (texto previo al audio)" });
                                }
                                sent++;
                                await Task.Delay(IntraStepDelayMs, ct);
                            }
                            // Convertimos a OGG/Opus y obtenemos duración real.
                            // Mostramos "grabando audio…" por exactamente esa
                            // duración, esperamos, y recién ahí enviamos.
                            var prep = await _evo.PrepareVoiceNoteAsync(asset.Content, ct);
                            // Mostramos "grabando…" un ratito (NO la duración completa del audio, que
                            // colgaba el request) y mandamos.
                            var presence = Math.Min(Math.Max(1, prep.DurationSeconds), MaxRecordingSeconds);
                            await _evo.SetPresenceRecordingAsync(instance, jid, presence, ct);
                            await Task.Delay(presence * 1000, ct);
                            var recAudio = await PersistSentAsync(lead, seller, instance, $"[audio: {asset.FileName}]", asset.Id, ct);
                            var okv = await _evo.SendPreparedVoiceNoteAsync(instance, lead.WhatsappPhone!, prep.OggBytes, ct);
                            if (!okv)
                            {
                                await MarkSendFailedAsync(recAudio, $"Falló el step {i + 1} (audio)", ct);
                                return StatusCode(502, new { error = $"Falló el step {i + 1} (audio)" });
                            }
                            sent++;
                        }
                        else
                        {
                            var caption = string.IsNullOrWhiteSpace(rendered) ? null : rendered;
                            var recMedia = await PersistSentAsync(lead, seller, instance, caption ?? $"[{asset.MimeType}: {asset.FileName}]", asset.Id, ct);
                            var okm = await _evo.SendMediaAsync(instance, lead.WhatsappPhone!, asset.Content, asset.MimeType, asset.FileName, caption, ct);
                            if (!okm)
                            {
                                await MarkSendFailedAsync(recMedia, $"Falló el step {i + 1} (adjunto)", ct);
                                return StatusCode(502, new { error = $"Falló el step {i + 1} (adjunto)" });
                            }
                            sent++;
                        }
                    }
                }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "send-now lead {Lead} step {Step} failed", lead.Id, i + 1);
                    return StatusCode(502, new { error = $"Step {i + 1}: {ex.Message}" });
                }
            }
        }

        if (sent > 0)
        {
            lead.Status = LeadStatus.Sent;
            lead.SentAt = DateTimeOffset.UtcNow;
        }
        await _db.SaveChangesAsync(ct);
        return Ok(new { ok = true, sent });
    }

    /// <summary>
    /// "Enviar ahora" cuando la línea del vendedor sale por un celu físico: no se manda
    /// nada sincrónico — se encolan los steps de TEXTO con <see cref="MessageOutbox.BridgeManualPriority"/>
    /// y el bridge los pullea respetando cap diario + gap por línea. El celu no puede
    /// mandar media, así que esos steps se saltean y se avisa en la respuesta.
    /// </summary>
    private async Task<IActionResult> QueueSendNowForBridgeAsync(Lead lead, Seller seller, Device device, CancellationToken ct)
    {
        var (steps, cadenceCategory) = OutboxEnqueueHelper.ResolveStepsForLead(lead, lead.Product!);
        var instance = seller.EvolutionInstance?.InstanceName ?? string.Empty;

        // Cancelamos los outbox rows pendientes del lead para no duplicar
        // (mismo criterio que la ruta sincrónica).
        var pending = await _db.Outbox
            .Where(o => o.LeadId == lead.Id && (o.Status == OutboxStatus.Scheduled || o.Status == OutboxStatus.Sending))
            .ToListAsync(ct);
        foreach (var p in pending) p.Status = OutboxStatus.Cancelled;

        var when = DateTimeOffset.UtcNow;
        var queued = 0;
        var skippedMedia = 0;

        void Enqueue(string text, int stepIndex)
        {
            _db.Outbox.Add(new MessageOutbox
            {
                Id = Guid.NewGuid(),
                LeadId = lead.Id,
                SellerId = seller.Id,
                Channel = MessageChannel.WhatsApp,
                EvolutionInstance = instance,
                WhatsappPhone = lead.WhatsappPhone!,
                Message = text,
                StepIndex = stepIndex,
                CadenceCategory = cadenceCategory,
                Priority = MessageOutbox.BridgeManualPriority,
                ScheduledAt = when,
                Status = OutboxStatus.Scheduled
            });
            queued++;
            // +1s para orden estable entre steps; el pacing real lo pone el bridge.
            when = when.AddSeconds(1);
        }

        for (var i = 0; i < steps.Count; i++)
        {
            var step = steps[i];
            var hasMedia = step.MediaAssetId is not null || (step.MediaAssetIds is { Count: > 0 });
            if (hasMedia) { skippedMedia++; continue; }
            if (string.IsNullOrWhiteSpace(step.Text)) continue;
            Enqueue(_renderer.RenderTemplate(step.Text, lead, lead.Product!, seller), i);
        }

        // Producto sin steps de texto utilizables: fallback al template legacy.
        if (queued == 0)
        {
            var msg = !string.IsNullOrWhiteSpace(lead.RenderedMessage)
                ? lead.RenderedMessage!
                : _renderer.Render(lead, lead.Product!, seller);
            if (string.IsNullOrWhiteSpace(msg))
                return BadRequest(new { error = "La cadencia de este producto no tiene ningún step de texto que el celu pueda mandar." });
            Enqueue(msg, 0);
        }

        lead.Status = lead.Status is LeadStatus.New or LeadStatus.Assigned ? LeadStatus.Queued : lead.Status;
        await _db.SaveChangesAsync(ct);

        var online = device.Status == DeviceStatus.Online
                     && device.LastHeartbeatAt is not null
                     && DateTimeOffset.UtcNow - device.LastHeartbeatAt.Value < TimeSpan.FromSeconds(120);
        var note = online
            ? $"Encolado al celu {device.Name}: {queued} mensaje(s) salen en los próximos minutos (pacing anti-ban de la línea)."
            : $"Encolado al celu {device.Name} ({queued} mensaje(s)) — el celu está offline ahora, salen cuando vuelva.";
        if (skippedMedia > 0)
            note += $" {skippedMedia} paso(s) con audio/imagen no salen por el celu (solo texto).";

        return Ok(new { ok = true, sent = 0, queued, via = "device", device = device.Name, deviceOnline = online, skippedMedia, message = note });
    }

    /// <summary>
    /// Registra el envío (outbox + conversación) y COMMITEA antes de mandar por Evolution.
    /// El eco fromMe del webhook llega en ~1s y el takeover lo matchea contra estos
    /// registros — sin commit previo lo toma como mensaje manual y mutea el bot.
    /// Si el send después falla, marcar con <see cref="MarkSendFailedAsync"/>.
    /// </summary>
    private async Task<(MessageOutbox Outbox, ConversationMessage Message)> PersistSentAsync(
        Lead lead, Seller seller, string instance, string text, Guid? mediaAssetId, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var ob = new MessageOutbox
        {
            Id = Guid.NewGuid(),
            LeadId = lead.Id,
            SellerId = seller.Id,
            EvolutionInstance = instance,
            WhatsappPhone = lead.WhatsappPhone!,
            Message = text,
            MediaAssetId = mediaAssetId,
            ScheduledAt = now,
            SentAt = now,
            Status = OutboxStatus.Sent,
            Attempts = 1
        };
        _db.Outbox.Add(ob);
        var cm = new ConversationMessage
        {
            Id = Guid.NewGuid(),
            LeadId = lead.Id,
            SellerId = seller.Id,
            Direction = MessageDirection.Outbound,
            Status = MessageDeliveryStatus.Sent,
            Text = text,
            EvolutionInstance = instance,
            Timestamp = now,
            IsRead = true
        };
        _db.ConversationMessages.Add(cm);
        await _db.SaveChangesAsync(ct);
        return (ob, cm);
    }

    private async Task MarkSendFailedAsync((MessageOutbox Outbox, ConversationMessage Message) rec, string error, CancellationToken ct)
    {
        rec.Outbox.Status = OutboxStatus.Failed;
        rec.Outbox.Error = error;
        rec.Message.Status = MessageDeliveryStatus.Failed;
        await _db.SaveChangesAsync(ct);
    }

    [HttpPost("{id:guid}/queue")]
    public async Task<ActionResult<LeadDto>> Queue(Guid id, [FromBody] QueueLeadRequest? req, CancellationToken ct)
    {
        var sellerId = CurrentUser.Id(User);
        var lead = await _db.Leads.Include(l => l.Product).Include(l => l.Seller).ThenInclude(s => s!.EvolutionInstance)
            .FirstOrDefaultAsync(l => l.Id == id, ct);
        if (lead is null) return NotFound();
        if (lead.SellerId != sellerId && !CurrentUser.IsAdmin(User)) return Forbid();
        if (string.IsNullOrWhiteSpace(lead.WhatsappPhone)) return BadRequest(new { error = "Lead sin teléfono WhatsApp" });

        if (lead.SellerId is null || lead.Seller is null)
            return BadRequest(new { error = "Lead sin vendedor asignado. Asignalo primero." });

        var seller = lead.Seller;
        if (seller.EvolutionInstance is null)
            return BadRequest(new { error = "El vendedor no tiene instancia de WhatsApp configurada." });

        // No exigimos Status==Connected acá: el OutboxSender ya filtra al momento de mandar.
        // Si está desconectado, el item se queda Scheduled hasta que reconecte.
        if (lead.Product is null) return BadRequest(new { error = "Lead sin producto." });

        // Idempotencia: si ya hay cadencia pendiente para este lead, no re-encolamos.
        // Sin esto, re-clickear "Encolar" duplica toda la cadencia.
        var alreadyQueued = await _db.Outbox.AnyAsync(
            o => o.LeadId == lead.Id
              && (o.Status == OutboxStatus.Scheduled || o.Status == OutboxStatus.Sending), ct);
        if (alreadyQueued)
            return BadRequest(new { error = "El lead ya está en cola." });

        if (string.IsNullOrWhiteSpace(lead.RenderedMessage))
            lead.RenderedMessage = _renderer.Render(lead, lead.Product, seller);
        OutboxEnqueueHelper.EnqueueLeadMessages(
            _db, _renderer, lead, lead.Product, seller,
            lead.WhatsappPhone!, seller.EvolutionInstance.InstanceName,
            req?.At);
        lead.Status = LeadStatus.Queued;
        lead.QueuedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);
        return ToDto(lead);
    }

    public record SimilarLeadDto(Guid Id, string Name, string ProductKey, string? ProductName, LeadStatus Status, Guid? SellerId, string? SellerName, DateTimeOffset CreatedAt);

    [HttpGet("search")]
    public async Task<ActionResult<IEnumerable<SimilarLeadDto>>> SearchSimilar([FromQuery] string q, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(q) || q.Trim().Length < 3) return new List<SimilarLeadDto>();
        var needle = $"%{q.Trim()}%";
        // Cross-user lookup: a seller should see if another seller already loaded the same lead
        // so the team avoids contacting the same business twice. Seller name is included so the
        // duplicate-finder knows who to coordinate with.
        var rows = await _db.Leads.AsNoTracking().Include(l => l.Product).Include(l => l.Seller)
            .Where(l => EF.Functions.ILike(l.Name, needle))
            .OrderByDescending(l => l.CreatedAt).Take(8)
            .Select(l => new SimilarLeadDto(l.Id, l.Name, l.ProductKey, l.Product != null ? l.Product.DisplayName : null,
                l.Status, l.SellerId, l.Seller != null ? l.Seller.DisplayName : null, l.CreatedAt))
            .ToListAsync(ct);
        return rows;
    }

    [HttpPost("bulk-import")]
    public async Task<ActionResult<BulkImportResult>> BulkImport([FromBody] BulkImportRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.RawText))
            return BadRequest(new { error = "Falta el texto a importar" });
        if (string.IsNullOrWhiteSpace(req.ProductKey))
            return BadRequest(new { error = "Falta el producto" });

        var product = await _db.Products.FirstOrDefaultAsync(p => p.ProductKey == req.ProductKey, ct);
        if (product is null)
            return BadRequest(new { error = $"Producto '{req.ProductKey}' no existe" });

        var callerId = CurrentUser.Id(User);
        var isAdmin = CurrentUser.IsAdmin(User);
        Guid? sellerId = req.AssignToCaller
            ? callerId
            : (isAdmin ? req.SellerId : null);

        Seller? seller = null;
        if (sellerId is not null)
        {
            seller = await _db.Sellers.FirstOrDefaultAsync(s => s.Id == sellerId.Value, ct);
            if (seller is null) return BadRequest(new { error = "Vendedor no encontrado" });
        }

        var parsed = MapsTextParser.Parse(req.RawText);
        var now = DateTimeOffset.UtcNow;
        var items = new List<BulkImportItem>();

        // Dedupe set para el mismo batch (varios items con mismo phone en el paste).
        var seenInBatch = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var p in parsed)
        {
            // Skip permanently closed lugares — no tiene sentido contactarlos.
            if (string.Equals(p.BusinessStatus, "permanently_closed", StringComparison.OrdinalIgnoreCase))
            {
                items.Add(new BulkImportItem(p.Name, p.Phone, p.Address, p.Rating, p.TotalReviews, "closed", "Cerrado permanentemente"));
                continue;
            }

            // Pre-dedupe POR NOMBRE para no gastar API call de Places en duplicados.
            // (El paste rara vez trae el mismo lugar 2 veces, pero la base sí puede tenerlo
            // de una corrida previa.)
            var nameLower = p.Name.Trim().ToLower();
            var nameBatchKey = $"name:{nameLower}";
            if (!seenInBatch.Add(nameBatchKey))
            {
                items.Add(new BulkImportItem(p.Name, p.Phone, p.Address, p.Rating, p.TotalReviews, "duplicate", "Duplicado en el paste (mismo nombre)"));
                continue;
            }

            var existsByName = await _db.Leads.AnyAsync(
                l => l.ProductKey == product.ProductKey && l.Name.ToLower() == nameLower, ct);
            if (existsByName)
            {
                items.Add(new BulkImportItem(p.Name, p.Phone, p.Address, p.Rating, p.TotalReviews, "duplicate", "Ya existe en la base (mismo nombre)"));
                continue;
            }

            // Enriquecimiento opcional con Google Places: trae teléfono / website / lat-lng
            // para ítems donde el listado pegado no incluyó esos datos. ~$0.04/lead.
            string? enrichedPhone = p.Phone;
            string? enrichedWebsite = null;
            string? placeId = null;
            double? lat = null, lng = null;
            string? formattedAddress = p.Address;
            var rating = p.Rating;
            var reviews = p.TotalReviews;
            var bizStatus = p.BusinessStatus;
            var enrichmentNote = (string?)null;

            if (req.EnrichWithPlacesApi)
            {
                var enriched = await _enricher.EnrichAsync(
                    p.Name, p.Address, req.City, product.CountryName, product.Language, ct);
                if (enriched is not null)
                {
                    placeId = enriched.PlaceId;
                    if (string.IsNullOrWhiteSpace(enrichedPhone)) enrichedPhone = enriched.Phone;
                    enrichedWebsite = enriched.Website;
                    lat = enriched.Latitude; lng = enriched.Longitude;
                    rating ??= enriched.Rating;
                    reviews ??= enriched.TotalReviews;
                    formattedAddress = enriched.FormattedAddress ?? formattedAddress;

                    // Si Google reporta cerrado permanentemente, lo respetamos.
                    if (string.Equals(enriched.BusinessStatus, "CLOSED_PERMANENTLY", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(enriched.BusinessStatus, "permanently_closed", StringComparison.OrdinalIgnoreCase))
                    {
                        items.Add(new BulkImportItem(p.Name, enrichedPhone, formattedAddress, rating, reviews, "closed", "Cerrado permanentemente (Google)"));
                        continue;
                    }
                    bizStatus ??= enriched.BusinessStatus;
                }
                else
                {
                    enrichmentNote = "no se encontró en Places";
                }
            }

            var normalized = _phone.Normalize(enrichedPhone, product.PhonePrefix);

            // Segundo dedupe: por placeId (más robusto) y por phone (si el enrich consiguió uno).
            if (!string.IsNullOrWhiteSpace(placeId))
            {
                if (!seenInBatch.Add($"pid:{placeId}"))
                {
                    items.Add(new BulkImportItem(p.Name, normalized, formattedAddress, rating, reviews, "duplicate", "Duplicado en el paste (place_id)"));
                    continue;
                }
                var existsByPlace = await _db.Leads.AnyAsync(
                    l => l.ProductKey == product.ProductKey && l.PlaceId == placeId, ct);
                if (existsByPlace)
                {
                    items.Add(new BulkImportItem(p.Name, normalized, formattedAddress, rating, reviews, "duplicate", "Ya existe en la base (place_id)"));
                    continue;
                }
            }
            if (!string.IsNullOrWhiteSpace(normalized))
            {
                if (!seenInBatch.Add($"phone:{normalized}"))
                {
                    items.Add(new BulkImportItem(p.Name, normalized, formattedAddress, rating, reviews, "duplicate", "Duplicado en el paste (teléfono)"));
                    continue;
                }
                var existsByPhone = await _db.Leads.AnyAsync(
                    l => l.ProductKey == product.ProductKey && l.WhatsappPhone == normalized, ct);
                if (existsByPhone)
                {
                    items.Add(new BulkImportItem(p.Name, normalized, formattedAddress, rating, reviews, "duplicate", "Ya existe en la base (teléfono)"));
                    continue;
                }
            }

            try
            {
                var lead = new Lead
                {
                    Id = Guid.NewGuid(),
                    ProductKey = product.ProductKey,
                    Source = req.Source,
                    Name = p.Name.Trim(),
                    City = string.IsNullOrWhiteSpace(req.City) ? null : req.City.Trim(),
                    WhatsappPhone = normalized,
                    Website = enrichedWebsite,
                    Address = formattedAddress,
                    Latitude = lat,
                    Longitude = lng,
                    PlaceId = placeId,
                    Rating = rating,
                    TotalReviews = reviews,
                    BusinessStatus = bizStatus,
                    SearchQuery = "bulk-import",
                    SearchCategory = p.Type,
                    SellerId = sellerId,
                    AssignedAt = sellerId is not null ? now : null,
                    Status = sellerId is not null && req.Status == LeadStatus.New
                        ? LeadStatus.Assigned
                        : req.Status,
                    CreatedAt = now,
                    UpdatedAt = now
                };

                if (lead.Status >= LeadStatus.Sent) lead.SentAt = now;

                _db.Leads.Add(lead);
                await _db.SaveChangesAsync(ct);

                items.Add(new BulkImportItem(p.Name, normalized, formattedAddress, rating, reviews, "inserted", enrichmentNote, lead.Id));
            }
            catch (Exception ex)
            {
                items.Add(new BulkImportItem(p.Name, normalized, formattedAddress, rating, reviews, "error", ex.Message));
            }
        }

        return new BulkImportResult(
            Parsed: parsed.Count,
            Inserted: items.Count(i => i.Outcome == "inserted"),
            Duplicates: items.Count(i => i.Outcome == "duplicate"),
            Closed: items.Count(i => i.Outcome == "closed"),
            Errors: items.Count(i => i.Outcome == "error"),
            Items: items);
    }

    [HttpPost]
    public async Task<ActionResult<LeadDto>> CreateManual([FromBody] CreateManualLeadRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.Name)) return BadRequest(new { error = "Falta el nombre del lead" });
        if (string.IsNullOrWhiteSpace(req.ProductKey)) return BadRequest(new { error = "Falta el producto" });

        var product = await _db.Products.FirstOrDefaultAsync(p => p.ProductKey == req.ProductKey, ct);
        if (product is null) return BadRequest(new { error = $"Producto '{req.ProductKey}' no existe" });

        var callerId = CurrentUser.Id(User);
        var isAdmin = CurrentUser.IsAdmin(User);
        var sellerId = isAdmin && req.SellerId is not null ? req.SellerId.Value : callerId;
        var seller = await _db.Sellers
            .Include(s => s.EvolutionInstance)
            .FirstOrDefaultAsync(s => s.Id == sellerId, ct);
        if (seller is null) return BadRequest(new { error = "Vendedor no encontrado" });

        var now = DateTimeOffset.UtcNow;
        // Con AutoQueue forzamos Assigned (que dispara la cadencia); sin AutoQueue
        // se respeta el status del request (default Sent — para registrar contactos
        // ya hechos a mano).
        var status = req.AutoQueue ? LeadStatus.Assigned : (req.Status ?? LeadStatus.Sent);

        var phone = string.IsNullOrWhiteSpace(req.WhatsappPhone) ? null : req.WhatsappPhone.Trim();
        if (req.AutoQueue)
        {
            if (string.IsNullOrWhiteSpace(phone))
                return BadRequest(new { error = "Con auto-encolar el lead necesita WhatsApp" });
            if (seller.EvolutionInstance is null)
                return BadRequest(new { error = "El vendedor no tiene instancia de WhatsApp configurada" });
        }

        var lead = new Lead
        {
            Id = Guid.NewGuid(),
            ProductKey = req.ProductKey,
            Source = req.Source,
            Name = req.Name.Trim(),
            City = string.IsNullOrWhiteSpace(req.City) ? null : req.City.Trim(),
            WhatsappPhone = phone,
            InstagramHandle = string.IsNullOrWhiteSpace(req.InstagramHandle) ? null : req.InstagramHandle.Trim(),
            Website = string.IsNullOrWhiteSpace(req.Website) ? null : req.Website.Trim(),
            Notes = string.IsNullOrWhiteSpace(req.Notes) ? null : req.Notes.Trim(),
            SellerId = sellerId,
            AssignedAt = now,
            Status = status,
            CreatedAt = now,
            UpdatedAt = now
        };

        // Mark timestamps based on status, since the seller is recording past activity.
        if (status >= LeadStatus.Sent) lead.SentAt = now;
        if (status == LeadStatus.Replied) lead.FirstReplyAt = now;
        if (status is LeadStatus.Closed or LeadStatus.Lost) lead.ClosedAt = now;

        // Encolar la cadencia si el caller lo pidió. Misma lógica que /assign.
        if (req.AutoQueue)
        {
            lead.RenderedMessage = _renderer.Render(lead, product, seller);
            lead.WhatsappLink = string.IsNullOrWhiteSpace(lead.RenderedMessage)
                ? null
                : $"https://wa.me/{lead.WhatsappPhone}?text={Uri.EscapeDataString(lead.RenderedMessage)}";
            OutboxEnqueueHelper.EnqueueLeadMessages(
                _db, _renderer, lead, product, seller,
                lead.WhatsappPhone!, seller.EvolutionInstance!.InstanceName);
            lead.Status = LeadStatus.Queued;
            lead.QueuedAt = now;
        }

        _db.Leads.Add(lead);
        await _db.SaveChangesAsync(ct);

        await _db.Entry(lead).Reference(l => l.Product).LoadAsync(ct);
        await _db.Entry(lead).Reference(l => l.Seller).LoadAsync(ct);
        return ToDto(lead);
    }

    private static LeadDto ToDto(Lead l) => new(
        l.Id, l.ProductKey, l.Product?.DisplayName, l.Source, l.Name, l.City, l.Province,
        l.WhatsappPhone, l.Website, l.InstagramHandle, l.FacebookUrl, l.Rating, l.TotalReviews,
        l.Score, l.Status, l.SellerId, l.Seller?.DisplayName, l.RenderedMessage, l.WhatsappLink,
        l.AssignedAt, l.SentAt, l.FirstReplyAt, l.Notes, l.CreatedAt,
        l.SearchCategory, l.SearchQuery);
}
