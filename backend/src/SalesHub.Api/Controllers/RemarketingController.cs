using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SalesHub.Api.Auth;
using SalesHub.Core.Domain.Entities;
using SalesHub.Core.Domain.Enums;
using SalesHub.Infrastructure.Persistence;
using SalesHub.Infrastructure.Services;

namespace SalesHub.Api.Controllers;

/// <summary>
/// Campaña de remarketing (/remarketing): config, medidor del cupo diario por línea, métrica
/// de respuesta por etapa y vista previa de los próximos mensajes antes de prenderla.
/// </summary>
[ApiController]
[Route("api/remarketing")]
[Authorize]
public class RemarketingController : ControllerBase
{
    private readonly ApplicationDbContext _db;
    private readonly RemarketingService _svc;
    private readonly ClaudeClient _claude;

    public RemarketingController(ApplicationDbContext db, RemarketingService svc, ClaudeClient claude)
    {
        _db = db; _svc = svc; _claude = claude;
    }

    public record SettingsRequest(bool Enabled, int PerLinePerDay, int MinIdleDays, int? MaxIdleDays,
        List<string>? SendWindows, List<int>? SendWeekdays, List<Guid>? SenderSellerIds, List<string>? ProductKeys,
        bool PersonalizeWithAi, Guid? HandoffSellerId);

    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken ct)
    {
        if (!CurrentUser.IsAdmin(User)) return Forbid();
        var s = await _svc.GetSettingsAsync(ct);
        var dayStart = RemarketingService.DayStartAr(DateTimeOffset.UtcNow);

        var sellers = await _db.Sellers.AsNoTracking().Include(x => x.EvolutionInstance)
            .Where(x => x.IsActive || s.SenderSellerIds.Contains(x.Id))
            .OrderBy(x => x.DisplayName)
            .ToListAsync(ct);

        var lines = new List<object>();
        foreach (var seller in sellers)
        {
            var (ok, reason, deviceCap) = await _svc.LineStatusAsync(seller, ct);
            var used = await _svc.UsedTodayAsync(seller.Id, ct);
            var sentToday = await _db.Outbox.CountAsync(o => o.SellerId == seller.Id
                && o.CadenceCategory == MessageOutbox.RemarketingCategory
                && o.Status == OutboxStatus.Sent && o.SentAt >= dayStart, ct);
            var pending = await _db.Outbox.CountAsync(o => o.SellerId == seller.Id
                && o.CadenceCategory == MessageOutbox.RemarketingCategory
                && o.Status == OutboxStatus.Scheduled, ct);
            lines.Add(new
            {
                sellerId = seller.Id,
                name = seller.DisplayName,
                selected = s.SenderSellerIds.Contains(seller.Id),
                canSend = ok,
                reason,
                deviceCap,
                sellerDailyCap = seller.DailyCap,
                usedToday = used,
                sentToday,
                pending,
            });
        }

        var attempts = await _db.RemarketingAttempts.AsNoTracking()
            .Select(a => new
            {
                a.Stage,
                a.RepliedAt,
                Sent = _db.Outbox.Any(o => o.Id == a.OutboxId && o.Status == OutboxStatus.Sent),
            })
            .ToListAsync(ct);
        var excluded = attempts.Count(a => a.Stage == RemarketingService.ExcludedStage);
        var real = attempts.Where(a => a.Stage != RemarketingService.ExcludedStage).ToList();
        var byStage = real.GroupBy(a => a.Stage).Select(g => new
        {
            stage = g.Key,
            label = RemarketingService.StageOf(g.Key).Label,
            enqueued = g.Count(),
            sent = g.Count(x => x.Sent),
            replied = g.Count(x => x.RepliedAt != null),
        }).OrderByDescending(x => x.sent).ToList();

        var products = await _db.Products.AsNoTracking().OrderBy(p => p.DisplayName)
            .Select(p => new { p.ProductKey, p.DisplayName }).ToListAsync(ct);

        return Ok(new
        {
            settings = new
            {
                s.Enabled, s.PerLinePerDay, s.MinIdleDays, s.MaxIdleDays, s.SendWindows, s.SendWeekdays, s.HandoffSellerId,
                s.SenderSellerIds, s.ProductKeys, s.PersonalizeWithAi, s.UpdatedAt,
            },
            lines,
            pool = await _svc.CountPoolAsync(s, ct),
            totals = new
            {
                enqueued = real.Count,
                sent = real.Count(a => a.Sent),
                replied = real.Count(a => a.RepliedAt != null),
                excluded,
            },
            aiPaused = s.PersonalizeWithAi && (!_claude.IsConfigured || _claude.IsPaused),
            byStage,
            products,
            stages = RemarketingService.Stages.Select(x => new { x.Key, x.Label }),
        });
    }

    [HttpPut]
    public async Task<IActionResult> Save([FromBody] SettingsRequest req, CancellationToken ct)
    {
        if (!CurrentUser.IsAdmin(User)) return Forbid();
        if (req.PerLinePerDay is < 0 or > 50) return BadRequest(new { error = "El tope va de 0 a 50 por línea por día." });
        if (req.MinIdleDays is < 1 or > 365) return BadRequest(new { error = "Los días sin actividad van de 1 a 365." });
        if (req.MaxIdleDays is not null && req.MaxIdleDays <= req.MinIdleDays)
            return BadRequest(new { error = "La antigüedad máxima tiene que ser mayor que la mínima." });
        var windows = RemarketingService.ParseWindows(req.SendWindows);
        if (windows.Count == 0 || windows.Count != (req.SendWindows ?? new()).Count(w => !string.IsNullOrWhiteSpace(w)))
            return BadRequest(new { error = "Las franjas tienen que ser como 9-12 (hora Argentina, desde < hasta, sin superponerse)." });
        var days = (req.SendWeekdays ?? new()).Where(d => d is >= 1 and <= 7).Distinct().OrderBy(d => d).ToList();
        if (days.Count == 0) return BadRequest(new { error = "Elegí al menos un día." });
        if (req.HandoffSellerId is not null && !await _db.Sellers.AnyAsync(x => x.Id == req.HandoffSellerId && x.IsActive, ct))
            return BadRequest(new { error = "La persona a la que se pasa el lead tiene que ser un vendedor activo." });

        var s = await _svc.GetSettingsAsync(ct);
        var newSellers = (req.SenderSellerIds ?? new()).Distinct().ToList();
        var removed = s.SenderSellerIds.Except(newSellers).ToList();

        s.Enabled = req.Enabled;
        s.PerLinePerDay = req.PerLinePerDay;
        s.MinIdleDays = req.MinIdleDays;
        s.MaxIdleDays = req.MaxIdleDays;
        s.SendWindows = windows.Select(w => $"{w.Start}-{w.End}").ToList();
        s.SendWeekdays = days;
        s.HandoffSellerId = req.HandoffSellerId;
        s.SenderSellerIds = newSellers;
        s.ProductKeys = (req.ProductKeys ?? new()).Where(p => !string.IsNullOrWhiteSpace(p)).Distinct().ToList();
        s.PersonalizeWithAi = req.PersonalizeWithAi;
        s.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);

        // Apagar (o sacar una línea) devuelve a la cola a los que todavía no salieron.
        var cancelled = !s.Enabled
            ? await _svc.CancelPendingAsync(null, ct)
            : removed.Count > 0 ? await _svc.CancelPendingAsync(removed, ct) : 0;
        return Ok(new { ok = true, cancelled });
    }

    /// <summary>
    /// Los próximos de la cola con el mensaje que les saldría. Sin <paramref name="ai"/> muestra
    /// el opener fijo de la etapa (gratis); con ai=true lo escribe Claude como saldría de verdad.
    /// </summary>
    [HttpGet("preview")]
    public async Task<IActionResult> Preview([FromQuery] int take = 15, [FromQuery] bool ai = false,
        [FromQuery] Guid? sellerId = null, CancellationToken ct = default)
    {
        if (!CurrentUser.IsAdmin(User)) return Forbid();
        var s = await _svc.GetSettingsAsync(ct);
        take = Math.Clamp(take, 1, ai ? 10 : 50);

        // Con el nombre de quien va a mandar; si todavía no hay líneas elegidas, con el de quien mira.
        var sid = sellerId ?? (s.SenderSellerIds.Count > 0 ? s.SenderSellerIds[0] : CurrentUser.Id(User));
        var seller = await _db.Sellers.AsNoTracking().FirstOrDefaultAsync(x => x.Id == sid, ct)
                     ?? new Seller { DisplayName = "" };
        var products = await _db.Products.AsNoTracking().ToDictionaryAsync(p => p.ProductKey, p => p.DisplayName, ct);

        var candidates = await _svc.RankAsync(s, take, ct);
        var items = new List<object>();
        foreach (var c in candidates)
        {
            var r = await _svc.ComposeAsync(c, seller, products.GetValueOrDefault(c.ProductKey, c.ProductKey), ai, ct);
            items.Add(new
            {
                c.LeadId, c.Name, c.ProductKey, c.Stage,
                stageLabel = RemarketingService.StageOf(c.Stage).Label,
                c.Score, c.Owed, c.InboundCount, c.IdleDays, c.LastInbound,
                message = r.Text, ai = r.Ai, excluded = r.Excluded, reason = r.Reason,
            });
        }
        return Ok(new { seller = seller.DisplayName, items });
    }
}
