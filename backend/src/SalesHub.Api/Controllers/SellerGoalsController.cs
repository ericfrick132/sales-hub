using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SalesHub.Api.Dtos;
using SalesHub.Core.Domain.Entities;
using SalesHub.Core.Domain.Enums;
using SalesHub.Infrastructure.Persistence;

namespace SalesHub.Api.Controllers;

[ApiController]
[Route("api/seller-goals")]
[Authorize]
public class SellerGoalsController : ControllerBase
{
    private readonly ApplicationDbContext _db;

    public SellerGoalsController(ApplicationDbContext db)
    {
        _db = db;
    }

    // ---------- CRUD superadmin ----------

    [HttpGet]
    public async Task<ActionResult<IEnumerable<SellerGoalDto>>> List(CancellationToken ct)
    {
        if (!CurrentUser.IsAdmin(User)) return Forbid();
        var goals = await _db.SellerGoals
            .OrderBy(g => g.Period).ThenBy(g => g.Metric)
            .ToListAsync(ct);
        var names = await _db.Sellers.ToDictionaryAsync(s => s.Id, s => s.DisplayName, ct);
        return goals.Select(g => ToDto(g, names)).ToList();
    }

    [HttpPost]
    public async Task<ActionResult<SellerGoalDto>> Create([FromBody] CreateSellerGoalRequest req, CancellationToken ct)
    {
        if (!CurrentUser.IsAdmin(User)) return Forbid();
        if (req.Target < 0) return BadRequest(new { error = "target debe ser >= 0" });

        var goal = new SellerGoal
        {
            Id = Guid.NewGuid(),
            SellerId = req.SellerId,
            ProductKey = string.IsNullOrWhiteSpace(req.ProductKey) ? null : req.ProductKey,
            Metric = req.Metric,
            Period = req.Period,
            Target = req.Target,
            IsActive = req.IsActive
        };
        _db.SellerGoals.Add(goal);
        await _db.SaveChangesAsync(ct);

        var names = await _db.Sellers.ToDictionaryAsync(s => s.Id, s => s.DisplayName, ct);
        return ToDto(goal, names);
    }

    [HttpPut("{id:guid}")]
    public async Task<ActionResult<SellerGoalDto>> Update(Guid id, [FromBody] UpdateSellerGoalRequest req, CancellationToken ct)
    {
        if (!CurrentUser.IsAdmin(User)) return Forbid();
        var goal = await _db.SellerGoals.FirstOrDefaultAsync(g => g.Id == id, ct);
        if (goal is null) return NotFound();

        if (req.Target is { } t)
        {
            if (t < 0) return BadRequest(new { error = "target debe ser >= 0" });
            goal.Target = t;
        }
        if (req.Metric is { } m) goal.Metric = m;
        if (req.Period is { } p) goal.Period = p;
        if (req.IsActive is { } active) goal.IsActive = active;
        // El alcance (SellerId/ProductKey) define la identidad del objetivo y no se
        // edita acá para evitar ambigüedad null-vs-omitido en PATCH parciales: para
        // cambiarlo, se borra y se recrea.
        goal.UpdatedAt = DateTimeOffset.UtcNow;

        await _db.SaveChangesAsync(ct);
        var names = await _db.Sellers.ToDictionaryAsync(s => s.Id, s => s.DisplayName, ct);
        return ToDto(goal, names);
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        if (!CurrentUser.IsAdmin(User)) return Forbid();
        var goal = await _db.SellerGoals.FirstOrDefaultAsync(g => g.Id == id, ct);
        if (goal is null) return NotFound();
        _db.SellerGoals.Remove(goal);
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }

    // ---------- Vista del vendedor: objetivos + progreso real ----------

    [HttpGet("me/progress")]
    public async Task<ActionResult<IEnumerable<SellerGoalProgressDto>>> MyProgress(CancellationToken ct)
    {
        var sellerId = CurrentUser.Id(User);
        if (sellerId == Guid.Empty) return Forbid();

        var goals = await _db.SellerGoals
            .Where(g => g.IsActive && (g.SellerId == sellerId || g.SellerId == null))
            .ToListAsync(ct);

        // Si hay un objetivo específico del vendedor para un (métrica, período, app),
        // pisa al global del mismo combo (HasValue=true gana).
        var effective = goals
            .GroupBy(g => new { g.Metric, g.Period, g.ProductKey })
            .Select(grp => grp.OrderByDescending(g => g.SellerId.HasValue).First())
            .ToList();

        var result = new List<SellerGoalProgressDto>();
        foreach (var g in effective)
        {
            var (from, to) = PeriodWindow(g.Period);
            var current = await CountActualAsync(sellerId, g, from, to, ct);
            var pct = g.Target <= 0 ? 0 : (int)Math.Min(100, Math.Round(current * 100.0 / g.Target));
            result.Add(new SellerGoalProgressDto(
                g.Id, g.Metric.ToString(), g.Period.ToString(), g.ProductKey, g.Target, current, pct));
        }

        return result.OrderBy(r => r.Period).ThenBy(r => r.Metric).ToList();
    }

    private async Task<int> CountActualAsync(Guid sellerId, SellerGoal g, DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
    {
        var q = _db.Leads.Where(l => l.SellerId == sellerId);
        if (g.ProductKey is not null) q = q.Where(l => l.ProductKey == g.ProductKey);

        return g.Metric switch
        {
            SellerGoalMetric.Contactos => await q.CountAsync(l => l.SentAt >= from && l.SentAt < to, ct),
            SellerGoalMetric.Respuestas => await q.CountAsync(l => l.FirstReplyAt >= from && l.FirstReplyAt < to, ct),
            SellerGoalMetric.Demos => await q.CountAsync(l => l.DemoScheduledAt >= from && l.DemoScheduledAt < to, ct),
            SellerGoalMetric.Cierres => await q.CountAsync(
                l => l.Status == LeadStatus.Closed && l.ClosedAt >= from && l.ClosedAt < to, ct),
            _ => 0
        };
    }

    // Ventana del período en hora AR. Las comparaciones de DateTimeOffset son por
    // instante absoluto, así que comparar contra timestamps UTC de Lead es correcto.
    private static (DateTimeOffset from, DateTimeOffset to) PeriodWindow(SellerGoalPeriod period)
    {
        var nowAr = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, ArTz);
        var todayStart = new DateTimeOffset(nowAr.Year, nowAr.Month, nowAr.Day, 0, 0, 0, nowAr.Offset);
        if (period == SellerGoalPeriod.Daily)
            return (todayStart, todayStart.AddDays(1));

        // Semanal: lunes 00:00 AR de la semana actual.
        var diff = ((int)nowAr.DayOfWeek + 6) % 7; // lunes => 0
        var weekStart = todayStart.AddDays(-diff);
        return (weekStart, weekStart.AddDays(7));
    }

    private static readonly TimeZoneInfo ArTz = ResolveArTz();

    private static TimeZoneInfo ResolveArTz()
    {
        foreach (var id in new[] { "America/Argentina/Buenos_Aires", "Argentina Standard Time" })
        {
            try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
            catch { /* probar el siguiente */ }
        }
        return TimeZoneInfo.CreateCustomTimeZone("AR", TimeSpan.FromHours(-3), "AR", "AR");
    }

    private static SellerGoalDto ToDto(SellerGoal g, IReadOnlyDictionary<Guid, string> names) =>
        new(
            g.Id,
            g.SellerId,
            g.SellerId is { } sid && names.TryGetValue(sid, out var n) ? n : null,
            g.ProductKey,
            g.Metric.ToString(),
            g.Period.ToString(),
            g.Target,
            g.IsActive);
}
