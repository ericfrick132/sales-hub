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
        DateTimeOffset ScheduledAt, DateTime? SentAt, int Attempts, string? Error);

    [HttpGet("outbox")]
    public async Task<ActionResult<IEnumerable<OutboxItemDto>>> Outbox(
        [FromQuery] OutboxStatus? status, [FromQuery] int limit = 100, CancellationToken ct = default)
    {
        var id = CurrentUser.Id(User);
        var q = _db.Outbox.AsNoTracking()
            .Include(o => o.Lead).ThenInclude(l => l!.Product)
            .Where(o => o.SellerId == id);
        if (status is not null) q = q.Where(o => o.Status == status);
        q = q.OrderByDescending(o => o.SentAt ?? o.ScheduledAt).Take(Math.Min(limit, 500));
        var rows = await q.ToListAsync(ct);
        return rows.Select(o => new OutboxItemDto(
            o.Id, o.LeadId, o.Lead?.Name ?? "—", o.Lead?.ProductKey ?? "",
            o.Lead?.Product?.DisplayName, o.WhatsappPhone, o.Message,
            o.Status, o.ScheduledAt, o.SentAt, o.Attempts, o.Error)).ToList();
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
        var queued = await _db.Outbox.CountAsync(o => o.SellerId == id && o.Status == OutboxStatus.Scheduled, ct);
        var leads = active.Select(l => new LeadDto(
            l.Id, l.ProductKey, l.Product?.DisplayName, l.Source, l.Name, l.City, l.Province,
            l.WhatsappPhone, l.Website, l.InstagramHandle, l.FacebookUrl, l.Rating, l.TotalReviews,
            l.Score, l.Status, l.SellerId, seller.DisplayName, l.RenderedMessage, l.WhatsappLink,
            l.AssignedAt, l.SentAt, l.FirstReplyAt, l.Notes, l.CreatedAt)).ToList();
        return new SellerDashboard(row, leads, queued, row.TodaySent, row.TodayCap);
    }

    private async Task<SellerMetricRow> BuildRowAsync(Seller s, DateOnly today, CancellationToken ct)
    {
        var dayStart = today.ToDateTime(TimeOnly.MinValue);
        var dayEnd = today.AddDays(1).ToDateTime(TimeOnly.MinValue);
        var assigned = await _db.Leads.CountAsync(l => l.SellerId == s.Id, ct);
        var sent = await _db.Leads.CountAsync(l => l.SellerId == s.Id && l.SentAt != null, ct);
        var replied = await _db.Leads.CountAsync(l => l.SellerId == s.Id && l.FirstReplyAt != null, ct);
        var closed = await _db.Leads.CountAsync(l => l.SellerId == s.Id && l.Status == LeadStatus.Closed, ct);
        // Count both auto-sent (Outbox via Evolution) and manually-recorded sends (Lead.SentAt
        // when seller marks status=Contactado). Today the team is sending by hand from their own
        // WhatsApp, so the manual count is the load-bearing one.
        var dayStartOffset = new DateTimeOffset(dayStart, TimeSpan.Zero);
        var dayEndOffset = new DateTimeOffset(dayEnd, TimeSpan.Zero);
        var todayManual = await _db.Leads.CountAsync(l => l.SellerId == s.Id
            && l.SentAt != null && l.SentAt >= dayStartOffset && l.SentAt < dayEndOffset, ct);
        var todayAuto = await _db.Outbox.CountAsync(o => o.SellerId == s.Id && o.Status == OutboxStatus.Sent
            && o.SentAt >= dayStart && o.SentAt < dayEnd, ct);
        var todaySent = todayManual + todayAuto;
        return new SellerMetricRow(
            s.Id, s.DisplayName, assigned, sent, replied, closed,
            sent == 0 ? 0 : Math.Round((double)replied / sent, 3),
            sent == 0 ? 0 : Math.Round((double)closed / sent, 3),
            _scheduler.ComputeTodayCap(s, today), todaySent,
            s.EvolutionInstance?.Status.ToString() ?? "no_instance", s.SendingEnabled);
    }
}
