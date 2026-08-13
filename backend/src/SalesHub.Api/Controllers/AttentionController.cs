using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SalesHub.Core.Abstractions;
using SalesHub.Core.Domain.Enums;
using SalesHub.Infrastructure.Persistence;

namespace SalesHub.Api.Controllers;

/// <summary>
/// Métricas de ATENCIÓN de los chats: cuánto tarda alguien en contestarle a un lead y
/// qué chats están esperando ahora mismo. Es el tablero para controlar que la persona
/// que maneja WhatsApp no deje a nadie colgado más de <c>Sla:ThresholdMinutes</c>.
/// </summary>
[ApiController]
[Route("api/attention")]
[Authorize]
public class AttentionController : ControllerBase
{
    private readonly ApplicationDbContext _db;
    private readonly IResponseTimeService _rt;
    private readonly IConfiguration _config;

    public AttentionController(ApplicationDbContext db, IResponseTimeService rt, IConfiguration config)
    {
        _db = db; _rt = rt; _config = config;
    }

    /// <summary>Orígenes que cuentan como "vino de un anuncio".</summary>
    private static readonly LeadSource[] AdSources = { LeadSource.MetaLeadAd, LeadSource.WhatsAppAd };

    private int SlaMinutes => _config.GetValue<int?>("Sla:ThresholdMinutes") ?? 10;

    public record SlaStats(
        int Turns, int Answered, int Unanswered,
        double? MedianMin, double? P90Min, double? AvgMin,
        double PctWithinSla, double PctAnsweredWithinSla);

    public record DailyPoint(string Date, int Turns, int Unanswered, double? MedianMin, double PctWithinSla);
    public record GroupRow(string Key, string Label, SlaStats Stats);
    public record HourRow(int Hour, int Turns, double? MedianMin, double PctWithinSla);
    public record AdProductRow(string ProductKey, int NewConversations, int Engaged, int Turns, double? AvgMin, double? MedianMin, double PctWithinSla);
    public record WaitingRow(
        Guid LeadId, string LeadName, string Phone, string ProductKey, string Source,
        Guid? SellerId, string SellerName, DateTimeOffset WaitingSince, int MinutesWaiting,
        int PendingMessages, string LastText, bool BotMuted, bool Breached);

    [HttpGet("summary")]
    public async Task<IActionResult> Summary([FromQuery] int days = 30, CancellationToken ct = default)
    {
        if (!CurrentUser.IsAdmin(User)) return Forbid();
        days = Math.Clamp(days, 1, 180);

        var sla = SlaMinutes;
        var now = DateTimeOffset.UtcNow;
        var arTz = SafeArTz();
        var nowAr = TimeZoneInfo.ConvertTime(now, arTz);
        var todayStart = new DateTimeOffset(nowAr.Year, nowAr.Month, nowAr.Day, 0, 0, 0, nowAr.Offset);
        var since = now.AddDays(-days);

        var turns = await _rt.GetTurnsAsync(since, null, ct);
        var waiting = await _rt.GetWaitingAsync(null, 0, 500, ct);

        var sellers = await _db.Sellers.AsNoTracking()
            .Select(s => new { s.Id, s.DisplayName }).ToDictionaryAsync(s => s.Id, s => s.DisplayName, ct);

        // ── Serie diaria (en hora AR, que es como se lee el día laboral) ──
        var daily = turns
            .GroupBy(t => TimeZoneInfo.ConvertTime(t.InAt, arTz).Date)
            .OrderBy(g => g.Key)
            .Select(g => new DailyPoint(
                g.Key.ToString("yyyy-MM-dd"),
                g.Count(),
                g.Count(t => t.Minutes is null),
                Percentile(g.Select(t => t.Minutes).Where(m => m.HasValue).Select(m => m!.Value), 0.5),
                Pct(g.Count(t => t.Minutes <= sla), g.Count())))
            .ToList();

        // ── Conversaciones nuevas de anuncios, por app ──
        var adLeads = await _db.Leads.AsNoTracking()
            .Where(l => l.CreatedAt >= since && AdSources.Contains(l.Source))
            .Select(l => new { l.Id, l.ProductKey })
            .ToListAsync(ct);

        var adTurnsByLead = turns.Where(t => AdSources.Contains((LeadSource)t.Source))
            .GroupBy(t => t.LeadId).ToDictionary(g => g.Key, g => g.ToList());

        var adRows = adLeads
            .GroupBy(l => string.IsNullOrEmpty(l.ProductKey) ? "(sin app)" : l.ProductKey)
            .Select(g =>
            {
                var ids = g.Select(x => x.Id).ToHashSet();
                var t = ids.Where(adTurnsByLead.ContainsKey).SelectMany(id => adTurnsByLead[id]).ToList();
                var mins = t.Where(x => x.Minutes.HasValue).Select(x => x.Minutes!.Value).ToList();
                return new AdProductRow(
                    ProductKey: g.Key,
                    NewConversations: g.Count(),
                    Engaged: ids.Count(adTurnsByLead.ContainsKey),
                    Turns: t.Count,
                    AvgMin: mins.Count > 0 ? Math.Round(mins.Average(), 1) : null,
                    MedianMin: Percentile(mins, 0.5),
                    PctWithinSla: Pct(t.Count(x => x.Minutes <= sla), t.Count));
            })
            .OrderByDescending(r => r.NewConversations)
            .ToList();

        var breached = waiting.Count(w => w.MinutesWaiting > sla);

        return Ok(new
        {
            slaMinutes = sla,
            windowDays = days,
            generatedAt = now,
            overall = Stats(turns, sla),
            today = Stats(turns.Where(t => t.InAt >= todayStart).ToList(), sla),
            last7d = Stats(turns.Where(t => t.InAt >= now.AddDays(-7)).ToList(), sla),
            daily,
            byProduct = turns
                .GroupBy(t => string.IsNullOrEmpty(t.ProductKey) ? "(sin app)" : t.ProductKey)
                .Select(g => new GroupRow(g.Key, g.Key, Stats(g.ToList(), sla)))
                .OrderByDescending(r => r.Stats.Turns).ToList(),
            bySeller = turns
                .GroupBy(t => t.SellerId)
                .Select(g => new GroupRow(
                    g.Key?.ToString() ?? "",
                    g.Key is { } id && sellers.TryGetValue(id, out var n) ? n : "(sin asignar)",
                    Stats(g.ToList(), sla)))
                .OrderByDescending(r => r.Stats.Turns).ToList(),
            byMode = turns
                .GroupBy(t => t.BotMuted)
                .Select(g => new GroupRow(g.Key ? "humano" : "bot", g.Key ? "Chat en manos humanas" : "Bot activo", Stats(g.ToList(), sla)))
                .ToList(),
            byHour = turns
                .GroupBy(t => TimeZoneInfo.ConvertTime(t.InAt, arTz).Hour)
                .OrderBy(g => g.Key)
                .Select(g => new HourRow(
                    g.Key, g.Count(),
                    Percentile(g.Where(t => t.Minutes.HasValue).Select(t => t.Minutes!.Value), 0.5),
                    Pct(g.Count(t => t.Minutes <= sla), g.Count())))
                .ToList(),
            ads = new
            {
                newConversations = adRows.Sum(r => r.NewConversations),
                byProduct = adRows,
            },
            waitingNow = new
            {
                total = waiting.Count,
                breached,
                oldestMinutes = waiting.Count > 0 ? (int)waiting.Max(w => w.MinutesWaiting) : 0,
            },
        });
    }

    /// <summary>Cola en vivo: quién está esperando respuesta, del que más espera al que menos.</summary>
    [HttpGet("waiting")]
    public async Task<IActionResult> Waiting(
        [FromQuery] int limit = 100, [FromQuery] int maxAgeHours = 0, CancellationToken ct = default)
    {
        var isAdmin = CurrentUser.IsAdmin(User);
        var sellerId = isAdmin ? (Guid?)null : CurrentUser.Id(User);
        var rows = await _rt.GetWaitingAsync(sellerId, maxAgeHours, limit, ct);
        return Ok(await ToRowsAsync(rows, ct));
    }

    /// <summary>Lo que ve el que atiende: su SLA de hoy y de la semana + su cola.</summary>
    [HttpGet("me")]
    public async Task<IActionResult> Me(CancellationToken ct = default)
    {
        var id = CurrentUser.Id(User);
        var sla = SlaMinutes;
        var now = DateTimeOffset.UtcNow;
        var arTz = SafeArTz();
        var nowAr = TimeZoneInfo.ConvertTime(now, arTz);
        var todayStart = new DateTimeOffset(nowAr.Year, nowAr.Month, nowAr.Day, 0, 0, 0, nowAr.Offset);

        var turns = await _rt.GetTurnsAsync(now.AddDays(-7), id, ct);
        var waiting = await _rt.GetWaitingAsync(id, 0, 100, ct);

        return Ok(new
        {
            slaMinutes = sla,
            today = Stats(turns.Where(t => t.InAt >= todayStart).ToList(), sla),
            last7d = Stats(turns, sla),
            waiting = await ToRowsAsync(waiting, ct),
            waitingBreached = waiting.Count(w => w.MinutesWaiting > sla),
        });
    }

    private async Task<List<WaitingRow>> ToRowsAsync(IReadOnlyList<WaitingChat> rows, CancellationToken ct)
    {
        var sla = SlaMinutes;
        var ids = rows.Where(r => r.SellerId.HasValue).Select(r => r.SellerId!.Value).Distinct().ToList();
        var names = await _db.Sellers.AsNoTracking()
            .Where(s => ids.Contains(s.Id))
            .ToDictionaryAsync(s => s.Id, s => s.DisplayName, ct);

        return rows.Select(r => new WaitingRow(
            r.LeadId, r.LeadName, r.Phone,
            string.IsNullOrEmpty(r.ProductKey) ? "(sin app)" : r.ProductKey,
            ((LeadSource)r.Source).ToString(),
            r.SellerId,
            r.SellerId is { } sid && names.TryGetValue(sid, out var n) ? n : "(sin asignar)",
            r.WaitingSince,
            (int)r.MinutesWaiting,
            r.PendingMessages,
            r.LastText,
            r.BotMuted,
            r.MinutesWaiting > sla)).ToList();
    }

    // ── helpers ──

    private static SlaStats Stats(IReadOnlyCollection<ResponseTurn> turns, int sla)
    {
        var mins = turns.Where(t => t.Minutes.HasValue).Select(t => t.Minutes!.Value).ToList();
        var answered = mins.Count;
        var withinSla = turns.Count(t => t.Minutes <= sla);
        return new SlaStats(
            Turns: turns.Count,
            Answered: answered,
            Unanswered: turns.Count - answered,
            MedianMin: Percentile(mins, 0.5),
            P90Min: Percentile(mins, 0.9),
            AvgMin: answered > 0 ? Math.Round(mins.Average(), 1) : null,
            // La métrica honesta: un chat que nunca se contestó también incumple el SLA.
            PctWithinSla: Pct(withinSla, turns.Count),
            PctAnsweredWithinSla: Pct(withinSla, answered));
    }

    private static double Pct(int num, int den) => den > 0 ? Math.Round(100.0 * num / den, 1) : 0;

    /// <summary>Percentil con interpolación lineal (mismo criterio que PERCENTILE_CONT de Postgres).</summary>
    private static double? Percentile(IEnumerable<double> values, double p)
    {
        var sorted = values.OrderBy(v => v).ToList();
        if (sorted.Count == 0) return null;
        if (sorted.Count == 1) return Math.Round(sorted[0], 1);
        var pos = p * (sorted.Count - 1);
        var lo = (int)Math.Floor(pos);
        var hi = (int)Math.Ceiling(pos);
        var val = lo == hi ? sorted[lo] : sorted[lo] + (sorted[hi] - sorted[lo]) * (pos - lo);
        return Math.Round(val, 1);
    }

    private static TimeZoneInfo SafeArTz()
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById("America/Argentina/Buenos_Aires"); }
        catch { return TimeZoneInfo.CreateCustomTimeZone("AR", TimeSpan.FromHours(-3), "AR", "AR"); }
    }
}
