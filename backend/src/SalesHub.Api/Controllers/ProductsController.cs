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
[Route("api/products")]
[Authorize]
public class ProductsController : ControllerBase
{
    private readonly ApplicationDbContext _db;
    private readonly IMessageRenderer _renderer;
    public ProductsController(ApplicationDbContext db, IMessageRenderer renderer)
    {
        _db = db; _renderer = renderer;
    }

    [HttpGet]
    public async Task<ActionResult<IEnumerable<ProductDto>>> List(CancellationToken ct)
    {
        var products = await _db.Products.OrderBy(p => p.DisplayName).ToListAsync(ct);
        return products.Select(ToDto).ToList();
    }

    [HttpPost]
    public async Task<ActionResult<ProductDto>> Create([FromBody] CreateOrUpdateProductRequest req, CancellationToken ct)
    {
        if (!CurrentUser.IsAdmin(User)) return Forbid();
        if (await _db.Products.AnyAsync(p => p.ProductKey == req.ProductKey, ct))
            return Conflict(new { error = "product_key duplicado" });
        var p = Map(new Product { Id = Guid.NewGuid() }, req);
        _db.Products.Add(p);
        await _db.SaveChangesAsync(ct);
        return ToDto(p);
    }

    [HttpPut("{id:guid}")]
    public async Task<ActionResult<ProductDto>> Update(Guid id, [FromBody] CreateOrUpdateProductRequest req, CancellationToken ct)
    {
        if (!CurrentUser.IsAdmin(User)) return Forbid();
        var p = await _db.Products.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (p is null) return NotFound();
        Map(p, req);
        p.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);
        return ToDto(p);
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        if (!CurrentUser.IsAdmin(User)) return Forbid();
        var p = await _db.Products.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (p is null) return NotFound();
        p.Active = false;
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }

    public record ResyncQueueResult(
        int LeadsRequeued, int OldRowsCancelled, int NewRowsCreated,
        int SkippedNoSeller, int SkippedNoInstance, int SkippedNoPhone,
        int SkippedAlreadyStarted);

    /// <summary>
    /// Cancela todas las filas Scheduled del outbox para este producto y las re-encola
    /// usando la cadencia actual. Útil después de editar MessageSteps/CategoryCadences:
    /// las filas viejas tienen el snapshot pre-rendereado, este endpoint las regenera con
    /// el render-at-send semantics activo (StepIndex poblado). Admin only, one-shot.
    /// </summary>
    [HttpPost("{id:guid}/resync-queue")]
    public async Task<ActionResult<ResyncQueueResult>> ResyncQueue(
        Guid id, [FromQuery] bool dryRun = false, CancellationToken ct = default)
    {
        if (!CurrentUser.IsAdmin(User)) return Forbid();
        var product = await _db.Products.FirstOrDefaultAsync(p => p.Id == id, ct);
        if (product is null) return NotFound();

        var leadIdsForProduct = await _db.Leads
            .Where(l => l.ProductKey == product.ProductKey)
            .Select(l => l.Id)
            .ToListAsync(ct);
        if (leadIdsForProduct.Count == 0)
            return new ResyncQueueResult(0, 0, 0, 0, 0, 0, 0);

        var oldRows = await _db.Outbox
            .Where(o => o.Status == OutboxStatus.Scheduled && leadIdsForProduct.Contains(o.LeadId))
            .ToListAsync(ct);
        var affectedLeadIds = oldRows.Select(r => r.LeadId).Distinct().ToList();

        // Si un lead ya tuvo algún step Sent/Sending, re-encolar reenviaría el step 0
        // (el helper siempre arranca de cero). Saltamos esos leads: dejamos Cancelled
        // sus filas Scheduled pero no las regeneramos. Para esos leads, el admin
        // tiene que decidir manualmente cómo recuperar (probablemente no quiere
        // duplicar el primer mensaje).
        var leadsWithProgress = await _db.Outbox
            .Where(o => affectedLeadIds.Contains(o.LeadId)
                     && (o.Status == OutboxStatus.Sent || o.Status == OutboxStatus.Sending))
            .Select(o => o.LeadId).Distinct()
            .ToListAsync(ct);
        var leadsCleanToRequeue = affectedLeadIds.Except(leadsWithProgress).ToList();

        if (dryRun)
            return new ResyncQueueResult(
                leadsCleanToRequeue.Count, oldRows.Count, 0, 0, 0, 0, leadsWithProgress.Count);

        foreach (var r in oldRows) r.Status = OutboxStatus.Cancelled;

        var leads = await _db.Leads
            .Include(l => l.Seller).ThenInclude(s => s!.EvolutionInstance)
            .Where(l => leadsCleanToRequeue.Contains(l.Id))
            .ToListAsync(ct);

        int requeued = 0, newRows = 0, noSeller = 0, noInstance = 0, noPhone = 0;
        foreach (var lead in leads)
        {
            if (string.IsNullOrWhiteSpace(lead.WhatsappPhone)) { noPhone++; continue; }
            if (lead.Seller is null) { noSeller++; continue; }
            if (lead.Seller.EvolutionInstance is null) { noInstance++; continue; }

            var rows = OutboxEnqueueHelper.EnqueueLeadMessages(
                _db, _renderer, lead, product, lead.Seller,
                lead.WhatsappPhone!, lead.Seller.EvolutionInstance.InstanceName);
            if (rows > 0) { requeued++; newRows += rows; }
        }
        await _db.SaveChangesAsync(ct);

        return new ResyncQueueResult(
            requeued, oldRows.Count, newRows, noSeller, noInstance, noPhone, leadsWithProgress.Count);
    }

    public record AudioStatRow(
        Guid MediaAssetId,
        string FileName,
        string MimeType,
        int Sent,
        int Replied,
        int Interested,
        int DemoScheduled,
        int Closed);

    /// <summary>Performance por audio del producto. Solo cuenta outbox rows
    /// efectivamente enviados (Status = Sent) cuyo asset pertenece al producto.</summary>
    [HttpGet("{productKey}/audio-stats")]
    public async Task<ActionResult<IEnumerable<AudioStatRow>>> AudioStats(string productKey, CancellationToken ct)
    {
        if (!CurrentUser.IsAdmin(User)) return Forbid();

        var assets = await _db.MediaAssets.AsNoTracking()
            .Where(m => m.ProductKey == productKey && m.MimeType.StartsWith("audio/"))
            .Select(m => new { m.Id, m.FileName, m.MimeType })
            .ToListAsync(ct);
        if (assets.Count == 0) return new List<AudioStatRow>();

        var assetIds = assets.Select(a => a.Id).ToList();

        // Outbox sent rows with one of these assets, joined to lead status.
        var sentRows = await _db.Outbox.AsNoTracking()
            .Where(o => o.MediaAssetId != null
                     && assetIds.Contains(o.MediaAssetId!.Value)
                     && o.Status == OutboxStatus.Sent)
            .Join(_db.Leads.AsNoTracking(), o => o.LeadId, l => l.Id, (o, l) => new
            {
                AssetId = o.MediaAssetId!.Value,
                LeadStatus = l.Status,
                Replied = l.FirstReplyAt != null
            })
            .ToListAsync(ct);

        var grouped = sentRows.GroupBy(r => r.AssetId).ToDictionary(g => g.Key, g => g.ToList());

        return assets.Select(a =>
        {
            var rows = grouped.TryGetValue(a.Id, out var list) ? list : new();
            return new AudioStatRow(
                MediaAssetId: a.Id,
                FileName: a.FileName,
                MimeType: a.MimeType,
                Sent: rows.Count,
                Replied: rows.Count(r => r.Replied),
                Interested: rows.Count(r => r.LeadStatus == LeadStatus.Interested
                                         || r.LeadStatus == LeadStatus.DemoScheduled
                                         || r.LeadStatus == LeadStatus.Closed),
                DemoScheduled: rows.Count(r => r.LeadStatus == LeadStatus.DemoScheduled
                                             || r.LeadStatus == LeadStatus.Closed),
                Closed: rows.Count(r => r.LeadStatus == LeadStatus.Closed));
        })
        .OrderByDescending(r => r.Sent)
        .ToList();
    }

    private static Product Map(Product p, CreateOrUpdateProductRequest r)
    {
        p.ProductKey = r.ProductKey;
        p.DisplayName = r.DisplayName;
        p.Active = r.Active;
        p.Country = r.Country;
        p.CountryName = r.CountryName;
        p.RegionCode = r.RegionCode;
        p.Language = r.Language;
        p.PhonePrefix = r.PhonePrefix;
        p.Categories = r.Categories;
        p.MessageTemplate = r.MessageTemplate;
        p.OpenerTemplate = r.OpenerTemplate ?? string.Empty;
        p.CheckoutUrl = r.CheckoutUrl;
        p.PriceDisplay = r.PriceDisplay;
        p.AiSalesPlaybook = r.AiSalesPlaybook ?? string.Empty;
        p.DailyLimit = r.DailyLimit;
        p.TriggerHours = r.TriggerHours;
        p.SendHourStart = Math.Clamp(r.SendHourStart, 0, 24);
        p.SendHourEnd = Math.Clamp(r.SendHourEnd, 0, 24);
        p.RequiresAssistedSale = r.RequiresAssistedSale;
        p.AutoPilot = r.AutoPilot;
        p.AutoReengage = r.AutoReengage;
        p.GooglePlacesDailyLeadCap = r.GooglePlacesDailyLeadCap;
        p.ReplyTemplates = (r.ReplyTemplates ?? new())
            .Select(s => s?.Trim() ?? string.Empty)
            .Where(s => s.Length > 0)
            .ToList();
        p.MessageSteps = MapSteps(r.MessageSteps);
        p.CategoryCadences = (r.CategoryCadences ?? new())
            .Where(c => !string.IsNullOrWhiteSpace(c.Category))
            .GroupBy(c => c.Category.Trim())
            .Select(g => new CategoryCadence
            {
                Category = g.Key,
                Steps = MapSteps(g.Last().Steps)  // si hay duplicados, último gana
            })
            // Tirar overrides vacíos: si la categoría no tiene ningún step,
            // que caiga al default. No vale la pena guardar el override.
            .Where(c => c.Steps.Count > 0)
            .ToList();
        return p;
    }

    private static List<Core.Domain.Entities.MessageStep> MapSteps(List<MessageStepDto>? src) =>
        (src ?? new())
            .Where(s => !string.IsNullOrWhiteSpace(s.Text) || s.MediaAssetId is not null || (s.MediaAssetIds is { Count: > 0 }))
            .Select((s, i) => new Core.Domain.Entities.MessageStep
            {
                Text = (s.Text ?? string.Empty).Trim(),
                DelaySeconds = i == 0 ? 0 : Math.Max(0, s.DelaySeconds),
                MediaAssetId = s.MediaAssetId,
                MediaAssetIds = (s.MediaAssetIds ?? new()).Where(g => g != Guid.Empty).Distinct().ToList()
            })
            .ToList();

    private static List<MessageStepDto> StepsToDto(List<Core.Domain.Entities.MessageStep> src) =>
        src.Select(s => new MessageStepDto(s.Text, s.DelaySeconds, s.MediaAssetId, s.MediaAssetIds)).ToList();

    private static ProductDto ToDto(Product p) => new(
        p.Id, p.ProductKey, p.DisplayName, p.Active, p.Country, p.CountryName, p.RegionCode, p.Language,
        p.PhonePrefix, p.Categories, p.MessageTemplate, p.OpenerTemplate ?? string.Empty,
        p.CheckoutUrl, p.PriceDisplay,
        p.DailyLimit, p.TriggerHours, p.SendHourStart, p.SendHourEnd,
        p.RequiresAssistedSale, p.GooglePlacesDailyLeadCap,
        p.ReplyTemplates,
        StepsToDto(p.MessageSteps),
        p.CategoryCadences.Select(c => new CategoryCadenceDto(c.Category, StepsToDto(c.Steps))).ToList(),
        p.AiSalesPlaybook ?? string.Empty,
        p.AutoPilot,
        p.AutoReengage);
}
