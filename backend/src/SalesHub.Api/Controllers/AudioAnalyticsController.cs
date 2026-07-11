using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SalesHub.Api.Dtos;
using SalesHub.Core.Domain.Enums;
using SalesHub.Infrastructure.Persistence;
using SalesHub.Infrastructure.Services;

namespace SalesHub.Api.Controllers;

[ApiController]
[Route("api/audio-analytics")]
[Authorize]
public class AudioAnalyticsController : ControllerBase
{
    private readonly ApplicationDbContext _db;
    public AudioAnalyticsController(ApplicationDbContext db) { _db = db; }

    public record StrategyMetrics(
        int Leads,
        int Sent,
        int Replied,
        int Interested,
        int DemoScheduled,
        int Closed);

    public record StrategyDto(
        string Key,
        string Label,
        bool IsDefault,
        List<MessageStepDto> Steps,
        StrategyMetrics Metrics);

    public record AudioStatRow(
        Guid MediaAssetId,
        string FileName,
        string MimeType,
        int Sent,
        int Replied,
        int Interested,
        int DemoScheduled,
        int Closed);

    private record LeadRow(
        Guid Id,
        string? SearchCategory,
        LeadSource Source,
        LeadStatus Status,
        bool Replied,
        bool Sent);

    public record AudioAnalyticsDto(
        string ProductKey,
        string ProductDisplayName,
        List<StrategyDto> Strategies,
        List<AudioStatRow> Audios);

    /// <summary>
    /// Devuelve, para un producto, la lista de cadencias (default + cada
    /// override por categoría) con sus métricas agregadas a nivel lead, más
    /// el detalle de performance por archivo de audio. La idea es ver de un
    /// vistazo qué COMBO (texto+audio+texto…) está convirtiendo mejor, no
    /// sólo qué audio individual rinde más.
    /// </summary>
    [HttpGet("{productKey}")]
    public async Task<ActionResult<AudioAnalyticsDto>> Get(string productKey, CancellationToken ct)
    {
        if (!CurrentUser.IsAdmin(User)) return Forbid();

        var product = await _db.Products.AsNoTracking()
            .FirstOrDefaultAsync(p => p.ProductKey == productKey, ct);
        if (product is null) return NotFound();

        // Categorías que tienen override propio. Los leads cuya SearchCategory
        // matchea una de éstas usan la cadencia override; el resto cae al default.
        var overrideCats = product.CategoryCadences
            .Select(c => c.Category)
            .Where(c => !string.IsNullOrWhiteSpace(c))
            .ToList();

        // Traemos los leads del producto una sola vez y bucketeamos en memoria.
        // Volumen acotado por producto, evita N queries por categoría.
        var leads = await _db.Leads.AsNoTracking()
            .Where(l => l.ProductKey == productKey)
            .Select(l => new LeadRow(
                l.Id,
                l.SearchCategory,
                l.Source,
                l.Status,
                l.FirstReplyAt != null,
                l.SentAt != null))
            .ToListAsync(ct);

        // Para "sent" aceptamos lead.SentAt no nulo OR algún outbox row Sent.
        // El SentAt del lead suele alcanzar pero los productos con cadencia de
        // varios pasos a veces lo setean recién al primer paso, así que cubrimos
        // ambos para no subestimar.
        var sentLeadIds = await _db.Outbox.AsNoTracking()
            .Where(o => o.Status == OutboxStatus.Sent
                     && o.Lead != null
                     && o.Lead.ProductKey == productKey)
            .Select(o => o.LeadId)
            .Distinct()
            .ToListAsync(ct);
        var sentSet = sentLeadIds.ToHashSet();

        StrategyMetrics MetricsFor(IEnumerable<LeadRow> bucket)
        {
            var list = bucket.ToList();
            int leadsCount = list.Count;
            int sent = list.Count(l => l.Sent || sentSet.Contains(l.Id));
            int replied = list.Count(l => l.Replied);
            int interested = list.Count(l =>
                l.Status == LeadStatus.Interested
                || l.Status == LeadStatus.DemoScheduled
                || l.Status == LeadStatus.Closed);
            int demos = list.Count(l =>
                l.Status == LeadStatus.DemoScheduled
                || l.Status == LeadStatus.Closed);
            int closed = list.Count(l => l.Status == LeadStatus.Closed);
            return new StrategyMetrics(leadsCount, sent, replied, interested, demos, closed);
        }

        var strategies = new List<StrategyDto>();

        // Orígenes con cadencia propia. Misma precedencia que ResolveStepsForLead:
        // origen > categoría > default — un lead de MetaLeadAd con override de
        // origen NO cuenta en el bucket de su categoría ni en el default.
        var overrideSources = product.SourceCadences
            .Select(c => c.Source)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        bool UsesSourceCadence(LeadRow l) => overrideSources.Contains(l.Source.ToString());

        // Default = leads sin override de origen NI de categoría.
        var defaultLeads = leads.Where(l =>
            !UsesSourceCadence(l)
            && (string.IsNullOrWhiteSpace(l.SearchCategory) || !overrideCats.Contains(l.SearchCategory!)));
        strategies.Add(new StrategyDto(
            Key: "__default__",
            Label: "Default",
            IsDefault: true,
            Steps: product.MessageSteps
                .Select(s => new MessageStepDto(s.Text, s.DelaySeconds, s.MediaAssetId, s.MediaAssetIds))
                .ToList(),
            Metrics: MetricsFor(defaultLeads)));

        foreach (var ov in product.SourceCadences)
        {
            var bucket = leads.Where(l => string.Equals(l.Source.ToString(), ov.Source, StringComparison.OrdinalIgnoreCase));
            strategies.Add(new StrategyDto(
                Key: OutboxEnqueueHelper.SourceCadencePrefix + ov.Source,
                Label: $"Origen: {ov.Source}",
                IsDefault: false,
                Steps: ov.Steps
                    .Select(s => new MessageStepDto(s.Text, s.DelaySeconds, s.MediaAssetId, s.MediaAssetIds))
                    .ToList(),
                Metrics: MetricsFor(bucket)));
        }

        foreach (var ov in product.CategoryCadences)
        {
            var bucket = leads.Where(l => !UsesSourceCadence(l) && l.SearchCategory == ov.Category);
            strategies.Add(new StrategyDto(
                Key: ov.Category,
                Label: ov.Category,
                IsDefault: false,
                Steps: ov.Steps
                    .Select(s => new MessageStepDto(s.Text, s.DelaySeconds, s.MediaAssetId, s.MediaAssetIds))
                    .ToList(),
                Metrics: MetricsFor(bucket)));
        }

        // Audio stats por archivo (mismo cálculo que el endpoint legacy de
        // ProductsController, lo tenemos acá para que la página nueva pegue
        // a un solo endpoint).
        var assets = await _db.MediaAssets.AsNoTracking()
            .Where(m => m.ProductKey == productKey && m.MimeType.StartsWith("audio/"))
            .Select(m => new { m.Id, m.FileName, m.MimeType })
            .ToListAsync(ct);

        var audios = new List<AudioStatRow>();
        if (assets.Count > 0)
        {
            var assetIds = assets.Select(a => a.Id).ToList();
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

            var grouped = sentRows.GroupBy(r => r.AssetId)
                .ToDictionary(g => g.Key, g => g.ToList());

            audios = assets.Select(a =>
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

        return new AudioAnalyticsDto(
            ProductKey: product.ProductKey,
            ProductDisplayName: product.DisplayName,
            Strategies: strategies,
            Audios: audios);
    }
}
