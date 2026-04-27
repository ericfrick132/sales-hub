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

    [HttpGet("me")]
    public async Task<ActionResult<SellerDashboard>> Me(CancellationToken ct)
    {
        var id = CurrentUser.Id(User);
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
        var todaySent = await _db.Outbox.CountAsync(o => o.SellerId == s.Id && o.Status == OutboxStatus.Sent
            && o.SentAt >= dayStart && o.SentAt < dayEnd, ct);
        return new SellerMetricRow(
            s.Id, s.DisplayName, assigned, sent, replied, closed,
            sent == 0 ? 0 : Math.Round((double)replied / sent, 3),
            sent == 0 ? 0 : Math.Round((double)closed / sent, 3),
            _scheduler.ComputeTodayCap(s, today), todaySent,
            s.EvolutionInstance?.Status.ToString() ?? "no_instance", s.SendingEnabled);
    }
}
