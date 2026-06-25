using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SalesHub.Api.Dtos;
using SalesHub.Core.Abstractions;
using SalesHub.Core.Domain.Entities;
using SalesHub.Core.Domain.Enums;
using SalesHub.Infrastructure.Persistence;

namespace SalesHub.Api.Controllers;

[ApiController]
[Route("api/dashboard")]
[Authorize]
public class DashboardController : ControllerBase
{
    private readonly ApplicationDbContext _db;
    private readonly ISendScheduler _scheduler;

    public DashboardController(ApplicationDbContext db, ISendScheduler scheduler)
    {
        _db = db; _scheduler = scheduler;
    }

    /// <summary>
    /// Gasto de la API de Claude (la key de IA): costo USD hoy / 7d / 30d + desglose
    /// por feature de HOY (dónde se gasta: conversación de ventas, SEO, social).
    /// </summary>
    [HttpGet("ai-cost")]
    public async Task<IActionResult> AiCost(CancellationToken ct)
    {
        if (!CurrentUser.IsAdmin(User)) return Forbid();

        TimeZoneInfo arTz;
        try { arTz = TimeZoneInfo.FindSystemTimeZoneById("America/Argentina/Buenos_Aires"); }
        catch { arTz = TimeZoneInfo.CreateCustomTimeZone("AR", TimeSpan.FromHours(-3), "AR", "AR"); }

        var now = DateTimeOffset.UtcNow;
        var nowAr = TimeZoneInfo.ConvertTime(now, arTz);
        var todayStart = new DateTimeOffset(nowAr.Year, nowAr.Month, nowAr.Day, 0, 0, 0, nowAr.Offset);
        var since7 = now.AddDays(-7);
        var since30 = now.AddDays(-30);

        var logs = await _db.AiUsageLogs.AsNoTracking()
            .Where(l => l.CreatedAt >= since30)
            .Select(l => new { l.CreatedAt, l.Feature, l.CostUsd })
            .ToListAsync(ct);

        var todayLogs = logs.Where(l => l.CreatedAt >= todayStart).ToList();
        var byFeatureToday = todayLogs
            .GroupBy(l => l.Feature)
            .Select(g => new { feature = g.Key, costUsd = g.Sum(x => x.CostUsd), calls = g.Count() })
            .OrderByDescending(x => x.costUsd)
            .ToList();

        return Ok(new
        {
            todayUsd = todayLogs.Sum(x => x.CostUsd),
            last7dUsd = logs.Where(l => l.CreatedAt >= since7).Sum(x => x.CostUsd),
            last30dUsd = logs.Sum(x => x.CostUsd),
            byFeatureToday,
        });
    }

    /// <summary>
    /// Embudo de efectividad por aplicación: leads → contactados → respondieron → demos →
    /// cerrados/perdidos + las tasas (% resp / % demo / % cierre). Filtrable por ventana
    /// (cohorte de leads que entraron en ese período, por CreatedAt).
    /// </summary>
    [HttpGet("effectiveness")]
    public async Task<IActionResult> Effectiveness([FromQuery] string period = "all", CancellationToken ct = default)
    {
        if (!CurrentUser.IsAdmin(User)) return Forbid();

        DateTimeOffset? since = null;
        switch ((period ?? "all").ToLowerInvariant())
        {
            case "today":
                TimeZoneInfo arTz;
                try { arTz = TimeZoneInfo.FindSystemTimeZoneById("America/Argentina/Buenos_Aires"); }
                catch { arTz = TimeZoneInfo.CreateCustomTimeZone("AR", TimeSpan.FromHours(-3), "AR", "AR"); }
                var nowAr = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, arTz);
                since = new DateTimeOffset(nowAr.Year, nowAr.Month, nowAr.Day, 0, 0, 0, nowAr.Offset);
                break;
            case "7d": since = DateTimeOffset.UtcNow.AddDays(-7); break;
            case "30d": since = DateTimeOffset.UtcNow.AddDays(-30); break;
        }

        var q = _db.Leads.AsNoTracking().Where(l => l.ProductKey != "");
        if (since.HasValue) q = q.Where(l => l.CreatedAt >= since.Value);

        var raw = await q
            .GroupBy(l => l.ProductKey)
            .Select(g => new
            {
                productKey = g.Key,
                leads = g.Count(),
                contactados = g.Count(l => l.SentAt != null),
                respondieron = g.Count(l => l.FirstReplyAt != null),
                demos = g.Count(l => l.DemoScheduledAt != null),
                cerrados = g.Count(l => l.Status == LeadStatus.Closed),
                perdidos = g.Count(l => l.Status == LeadStatus.Lost),
            })
            .ToListAsync(ct);

        var result = raw
            .Select(r => new
            {
                r.productKey, r.leads, r.contactados, r.respondieron, r.demos, r.cerrados, r.perdidos,
                replyRate = r.contactados > 0 ? Math.Round((double)r.respondieron / r.contactados, 4) : 0,
                demoRate = r.respondieron > 0 ? Math.Round((double)r.demos / r.respondieron, 4) : 0,
                closeRate = r.contactados > 0 ? Math.Round((double)r.cerrados / r.contactados, 4) : 0,
            })
            .OrderByDescending(x => x.leads)
            .ToList();

        return Ok(result);
    }

    [HttpGet("admin")]
    public async Task<ActionResult<GlobalMetrics>> Admin(CancellationToken ct)
    {
        if (!CurrentUser.IsAdmin(User)) return Forbid();
        var now = DateTimeOffset.UtcNow;
        var today = DateOnly.FromDateTime(now.UtcDateTime);
        var since7d = now.AddDays(-7);

        var totalLeads = await _db.Leads.CountAsync(ct);
        var leadsToday = await _db.Leads.CountAsync(l => l.CreatedAt >= today.ToDateTime(TimeOnly.MinValue), ct);
        var leadsSent7d = await _db.Leads.CountAsync(l => l.SentAt >= since7d, ct);
        var leadsReplied7d = await _db.Leads.CountAsync(l => l.FirstReplyAt >= since7d, ct);
        var leadsClosed7d = await _db.Leads.CountAsync(l => l.ClosedAt >= since7d && l.Status == LeadStatus.Closed, ct);

        var byProduct = await _db.Leads.GroupBy(l => l.ProductKey).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count, ct);
        var bySource = await _db.Leads.GroupBy(l => l.Source).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key.ToString(), x => x.Count, ct);

        var sellers = await _db.Sellers.Include(s => s.EvolutionInstance).ToListAsync(ct);
        var rows = new List<SellerMetricRow>();
        foreach (var s in sellers.Where(s => s.Role == SellerRole.Seller))
        {
            rows.Add(await BuildRowAsync(s, today, ct));
        }
        return new GlobalMetrics(totalLeads, leadsToday, leadsSent7d, leadsReplied7d, leadsClosed7d, byProduct, bySource, rows);
    }

    public record DailyActivity(string Date, int Total, Dictionary<string, int> ByProduct, Dictionary<string, int> ByStatus);
    public record SellerActivity(
        Guid SellerId, string DisplayName, string Email, string InstanceStatus, bool SendingEnabled,
        int Total, int Total7d, int TodayCount, int YesterdayCount,
        Dictionary<string, int> TopProducts, List<DailyActivity> Daily);

    [HttpGet("sellers/activity")]
    public async Task<ActionResult<IEnumerable<SellerActivity>>> SellersActivity(
        [FromQuery] int days = 14, CancellationToken ct = default)
    {
        if (!CurrentUser.IsAdmin(User)) return Forbid();
        days = Math.Clamp(days, 1, 60);

        var sellers = await _db.Sellers.AsNoTracking()
            .Include(s => s.EvolutionInstance)
            .Where(s => s.IsActive)
            .OrderBy(s => s.DisplayName)
            .ToListAsync(ct);

        var now = DateTimeOffset.UtcNow;
        var todayLocal = DateOnly.FromDateTime(now.UtcDateTime);
        var since = todayLocal.AddDays(-(days - 1)).ToDateTime(TimeOnly.MinValue);
        var sinceOffset = new DateTimeOffset(since, TimeSpan.Zero);

        // Pull all leads in window for active sellers in one shot, then group in-memory.
        // The dataset is bounded (sellers × days × leads-per-day, typically < 5k rows) so this is fine.
        var sellerIds = sellers.Select(s => s.Id).ToList();
        var leads = await _db.Leads.AsNoTracking()
            .Where(l => l.SellerId != null && sellerIds.Contains(l.SellerId!.Value)
                     && (l.AssignedAt ?? l.CreatedAt) >= sinceOffset)
            .Select(l => new
            {
                l.SellerId,
                l.ProductKey,
                l.Status,
                Stamp = l.AssignedAt ?? l.CreatedAt
            })
            .ToListAsync(ct);

        var rows = new List<SellerActivity>();
        foreach (var s in sellers)
        {
            var mine = leads.Where(l => l.SellerId == s.Id).ToList();

            var byDay = new List<DailyActivity>();
            for (var i = 0; i < days; i++)
            {
                var d = todayLocal.AddDays(-i);
                var dStart = new DateTimeOffset(d.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
                var dEnd = dStart.AddDays(1);
                var dayLeads = mine.Where(l => l.Stamp >= dStart && l.Stamp < dEnd).ToList();
                var byProduct = dayLeads.GroupBy(l => l.ProductKey).ToDictionary(g => g.Key, g => g.Count());
                var byStatus = dayLeads.GroupBy(l => l.Status.ToString()).ToDictionary(g => g.Key, g => g.Count());
                byDay.Add(new DailyActivity(d.ToString("yyyy-MM-dd"), dayLeads.Count, byProduct, byStatus));
            }

            var todayCount = byDay.FirstOrDefault(d => d.Date == todayLocal.ToString("yyyy-MM-dd"))?.Total ?? 0;
            var yesterdayCount = byDay.FirstOrDefault(d => d.Date == todayLocal.AddDays(-1).ToString("yyyy-MM-dd"))?.Total ?? 0;
            var total7d = byDay.Take(7).Sum(d => d.Total);
            var topProducts = mine.GroupBy(l => l.ProductKey)
                .Select(g => new { Key = g.Key, Count = g.Count() })
                .OrderByDescending(x => x.Count).Take(3)
                .ToDictionary(x => x.Key, x => x.Count);

            rows.Add(new SellerActivity(
                s.Id, s.DisplayName, s.Email,
                s.EvolutionInstance?.Status.ToString() ?? "no_instance",
                s.SendingEnabled,
                mine.Count, total7d, todayCount, yesterdayCount,
                topProducts, byDay));
        }
        return rows;
    }

    public record OutboxItemDto(
        Guid Id, Guid LeadId, string LeadName, string ProductKey, string? ProductName,
        string WhatsappPhone, string Message, OutboxStatus Status,
        DateTimeOffset ScheduledAt, DateTimeOffset? SentAt, int Attempts, string? Error,
        string? SearchCategory, bool HasMedia, string? MediaMimeType, string? City,
        /// <summary>
        /// Hora estimada real de envío considerando la config del seller (cap diario, ventana
        /// horaria, warmup, skip-day, jitter entre mensajes). Null si el envío está pausado
        /// (SendingEnabled=false o instancia no conectada) — en ese caso no se puede proyectar.
        /// </summary>
        DateTimeOffset? ProjectedAt);

    [HttpGet("outbox")]
    public async Task<ActionResult<IEnumerable<OutboxItemDto>>> Outbox(
        [FromQuery] OutboxStatus? status, [FromQuery] int limit = 100,
        [FromQuery] string? order = null, CancellationToken ct = default)
    {
        var id = CurrentUser.Id(User);
        // Misma whitelist del seller que aplica el OutboxSender al enviar. Sin esto, el
        // dashboard mostraba rows que en realidad nunca van a salir (porque el sender los
        // skipea), confundiendo al seller. Aplica sólo a rows Scheduled — los Sent/Failed
        // históricos se ven igual independiente de la whitelist actual.
        var sellerVerticals = await _db.Sellers.AsNoTracking()
            .Where(s => s.Id == id)
            .Select(s => s.VerticalsWhitelist)
            .FirstOrDefaultAsync(ct);
        var hasWhitelist = sellerVerticals is { Count: > 0 };

        var q = _db.Outbox.AsNoTracking()
            .Include(o => o.Lead).ThenInclude(l => l!.Product)
            .Include(o => o.MediaAsset)
            .Where(o => o.SellerId == id);
        if (status is not null) q = q.Where(o => o.Status == status);
        if (hasWhitelist)
        {
            q = q.Where(o => o.Status != OutboxStatus.Scheduled
                          || (o.Lead != null && sellerVerticals!.Contains(o.Lead.ProductKey)));
        }
        // order=upcoming → próximos primero (asc por ScheduledAt). Default = desc por última actividad.
        q = order == "upcoming"
            ? q.OrderBy(o => o.ScheduledAt)
            : q.OrderByDescending(o => o.SentAt ?? o.ScheduledAt);
        q = q.Take(Math.Min(limit, 500));
        var rows = await q.ToListAsync(ct);

        // Proyección de hora real de envío sólo para upcoming/Scheduled — para el resto
        // (Sent, Failed, Cancelled) la hora real es SentAt o no aplica.
        Dictionary<Guid, DateTimeOffset> projections = new();
        if (order == "upcoming" && rows.Count > 0 && rows.Any(o => o.Status == OutboxStatus.Scheduled))
        {
            var seller = await _db.Sellers.AsNoTracking()
                .Include(s => s.EvolutionInstance)
                .FirstOrDefaultAsync(s => s.Id == id, ct);
            if (seller is not null)
            {
                projections = await ProjectDispatchesAsync(seller, rows, ct);
            }
        }

        return rows.Select(o => new OutboxItemDto(
            o.Id, o.LeadId, o.Lead?.Name ?? "—", o.Lead?.ProductKey ?? "",
            o.Lead?.Product?.DisplayName, o.WhatsappPhone, o.Message,
            o.Status, o.ScheduledAt, o.SentAt, o.Attempts, o.Error,
            o.Lead?.SearchCategory, o.MediaAssetId is not null,
            o.MediaAsset?.MimeType, o.Lead?.City,
            projections.TryGetValue(o.Id, out var p) ? p : (DateTimeOffset?)null)).ToList();
    }

    /// <summary>
    /// Simula el OutboxSender hacia adelante para estimar a qué hora real va a salir cada
    /// item Scheduled. Considera:
    ///   - ventana horaria del seller (ActiveHoursStart/End en su timezone)
    ///   - cap diario con warmup ramp (ISendScheduler.ComputeTodayCap)
    ///   - skip-day determinístico (ISendScheduler.IsSkipDay)
    ///   - jitter entre mensajes (promedio de DelayMin/Max)
    ///   - ScheduledAt como piso (el item nunca sale antes de su scheduled)
    /// No considera (deliberadamente, para mantener la proyección simple):
    ///   - burst pauses
    ///   - ventana horaria por producto (asume seller-wide)
    ///   - typing/recording overhead (segundos, negligible vs. jitter de minutos)
    ///   - reintentos
    /// Devuelve {} si el seller no puede mandar (SendingEnabled=false o instancia
    /// no conectada) — el frontend muestra "envío pausado".
    /// </summary>
    private async Task<Dictionary<Guid, DateTimeOffset>> ProjectDispatchesAsync(
        Seller seller, List<MessageOutbox> items, CancellationToken ct)
    {
        var result = new Dictionary<Guid, DateTimeOffset>();
        if (!seller.SendingEnabled) return result;
        if (seller.EvolutionInstance is null || seller.EvolutionInstance.Status != InstanceStatus.Connected)
            return result;

        var tz = SafeTz(seller.Timezone);
        var nowUtc = DateTimeOffset.UtcNow;
        var nowLocal = TimeZoneInfo.ConvertTime(nowUtc, tz).DateTime;

        // Promedio del jitter entre mensajes — la mejor estimación lineal sin RNG.
        var avgDelaySec = Math.Max(1, (seller.DelayMinSeconds + seller.DelayMaxSeconds) / 2);

        var day = DateOnly.FromDateTime(nowLocal);
        var sentToday = await _db.Outbox.CountAsync(o =>
            o.SellerId == seller.Id && o.Status == OutboxStatus.Sent
            && o.SentAt != null
            && o.SentAt.Value >= day.ToDateTime(TimeOnly.MinValue)
            && o.SentAt.Value < day.AddDays(1).ToDateTime(TimeOnly.MinValue), ct);

        // Avanzar el día si hoy es skip-day o ya pasamos la ventana / cap.
        DateTime dayStart = day.ToDateTime(new TimeOnly(seller.ActiveHoursStart, 0));
        DateTime dayEnd = day.ToDateTime(new TimeOnly(seller.ActiveHoursEnd, 0));
        int cap = _scheduler.ComputeTodayCap(seller, day);
        DateTime cursor = nowLocal < dayStart ? dayStart : nowLocal;

        void RollToNextValidDay()
        {
            do
            {
                day = day.AddDays(1);
            } while (_scheduler.IsSkipDay(seller, day));
            dayStart = day.ToDateTime(new TimeOnly(seller.ActiveHoursStart, 0));
            dayEnd = day.ToDateTime(new TimeOnly(seller.ActiveHoursEnd, 0));
            cap = _scheduler.ComputeTodayCap(seller, day);
            sentToday = 0;
            cursor = dayStart;
        }

        if (_scheduler.IsSkipDay(seller, day) || cursor >= dayEnd || sentToday >= cap)
            RollToNextValidDay();

        foreach (var o in items)
        {
            if (o.Status != OutboxStatus.Scheduled) continue;

            // Piso: el item no puede salir antes de su ScheduledAt (en hora local del seller).
            var schedLocal = TimeZoneInfo.ConvertTime(o.ScheduledAt, tz).DateTime;
            if (schedLocal > cursor) cursor = schedLocal;

            // Si caímos fuera de ventana o sobrepasamos cap, rolleamos a próximo día válido,
            // pero respetando que el ScheduledAt del item podría estar todavía adelante.
            while (sentToday >= cap || cursor >= dayEnd)
            {
                RollToNextValidDay();
                if (schedLocal > cursor) cursor = schedLocal;
            }

            if (cursor < dayStart) cursor = dayStart;

            result[o.Id] = TimeZoneInfo.ConvertTimeToUtc(
                DateTime.SpecifyKind(cursor, DateTimeKind.Unspecified), tz);
            sentToday++;
            cursor = cursor.AddSeconds(avgDelaySec);
        }
        return result;
    }

    private static TimeZoneInfo SafeTz(string id)
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
        catch { return TimeZoneInfo.Utc; }
    }

    [HttpGet("seller/{id:guid}")]
    public async Task<ActionResult<SellerDashboard>> ForSeller(Guid id, CancellationToken ct)
    {
        if (!CurrentUser.IsAdmin(User) && CurrentUser.Id(User) != id) return Forbid();
        return await BuildSellerDashboardAsync(id, ct);
    }

    [HttpGet("me")]
    public async Task<ActionResult<SellerDashboard>> Me(CancellationToken ct)
    {
        var id = CurrentUser.Id(User);
        return await BuildSellerDashboardAsync(id, ct);
    }

    private async Task<ActionResult<SellerDashboard>> BuildSellerDashboardAsync(Guid id, CancellationToken ct)
    {
        var seller = await _db.Sellers.Include(s => s.EvolutionInstance).FirstOrDefaultAsync(s => s.Id == id, ct);
        if (seller is null) return NotFound();
        var today = DateOnly.FromDateTime(DateTimeOffset.UtcNow.UtcDateTime);
        var row = await BuildRowAsync(seller, today, ct);
        var todayStart = today.ToDateTime(TimeOnly.MinValue);
        var active = await _db.Leads.AsNoTracking()
            .Include(l => l.Product)
            .Where(l => l.SellerId == id && l.Status != LeadStatus.Closed && l.Status != LeadStatus.Lost)
            .OrderByDescending(l => l.AssignedAt)
            .Take(200)
            .ToListAsync(ct);
        // "En cola" cuenta LEADS en estado Queued, no filas de outbox (varias por
        // lead). Así Asignados + En cola + Enviados particionan limpio. El conteo
        // de mensajes de outbox sigue disponible en /dashboard/outbox.
        // Respetamos la whitelist del seller: si el producto del lead no está en
        // la whitelist el sender lo skipea, así que no debe contar como "En cola".
        var queuedQ = _db.Leads.Where(l => l.SellerId == id && l.Status == LeadStatus.Queued);
        if (seller.VerticalsWhitelist is { Count: > 0 })
        {
            var wl = seller.VerticalsWhitelist;
            queuedQ = queuedQ.Where(l => wl.Contains(l.ProductKey));
        }
        var queued = await queuedQ.CountAsync(ct);
        var leads = active.Select(l => new LeadDto(
            l.Id, l.ProductKey, l.Product?.DisplayName, l.Source, l.Name, l.City, l.Province,
            l.WhatsappPhone, l.Website, l.InstagramHandle, l.FacebookUrl, l.Rating, l.TotalReviews,
            l.Score, l.Status, l.SellerId, seller.DisplayName, l.RenderedMessage, l.WhatsappLink,
            l.AssignedAt, l.SentAt, l.FirstReplyAt, l.Notes, l.CreatedAt,
            l.SearchCategory, l.SearchQuery)).ToList();
        return new SellerDashboard(row, leads, queued, row.TodaySent, row.TodayCap);
    }

    private async Task<SellerMetricRow> BuildRowAsync(Seller s, DateOnly today, CancellationToken ct)
    {
        var dayStart = today.ToDateTime(TimeOnly.MinValue);
        var dayEnd = today.AddDays(1).ToDateTime(TimeOnly.MinValue);
        // "Asignados" = leads actualmente asignados sin encolar/enviar. Sin el filtro
        // de estado contaba todo (incluido Sent/Closed/Lost) y no reconciliaba.
        var assigned = await _db.Leads.CountAsync(l => l.SellerId == s.Id && l.Status == LeadStatus.Assigned, ct);
        var sent = await _db.Leads.CountAsync(l => l.SellerId == s.Id && l.SentAt != null, ct);
        var replied = await _db.Leads.CountAsync(l => l.SellerId == s.Id && l.FirstReplyAt != null, ct);
        var closed = await _db.Leads.CountAsync(l => l.SellerId == s.Id && l.Status == LeadStatus.Closed, ct);
        // "Enviados hoy" = leads distintos contactados hoy. Lead.SentAt lo setean
        // tanto el envío manual como el OutboxSender, así que basta contar leads
        // (antes se sumaba además las filas de outbox → doble conteo).
        var dayStartOffset = new DateTimeOffset(dayStart, TimeSpan.Zero);
        var dayEndOffset = new DateTimeOffset(dayEnd, TimeSpan.Zero);
        var todaySent = await _db.Leads.CountAsync(l => l.SellerId == s.Id
            && l.SentAt != null && l.SentAt >= dayStartOffset && l.SentAt < dayEndOffset, ct);
        return new SellerMetricRow(
            s.Id, s.DisplayName, assigned, sent, replied, closed,
            sent == 0 ? 0 : Math.Round((double)replied / sent, 3),
            sent == 0 ? 0 : Math.Round((double)closed / sent, 3),
            _scheduler.ComputeTodayCap(s, today), todaySent,
            s.EvolutionInstance?.Status.ToString() ?? "no_instance", s.SendingEnabled);
    }
}
