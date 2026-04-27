using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SalesHub.Core.Domain.Entities;
using SalesHub.Infrastructure.Persistence;
using SalesHub.Infrastructure.Services;

namespace SalesHub.Api.Controllers;

[ApiController]
[Route("api/conversations")]
[Authorize]
public class ConversationsController : ControllerBase
{
    private readonly ApplicationDbContext _db;
    private readonly ConversationService _conv;

    public ConversationsController(ApplicationDbContext db, ConversationService conv)
    {
        _db = db; _conv = conv;
    }

    public record ConversationListItem(
        Guid LeadId, string LeadName, string? City, string ProductKey, string Status,
        string? LastMessageText, MessageDirection? LastDirection,
        DateTimeOffset? LastTimestamp, int UnreadCount);

    public record ConversationMessageDto(
        Guid Id, MessageDirection Direction, string Text, DateTimeOffset Timestamp,
        MessageDeliveryStatus Status, bool IsRead);

    public record ConversationThreadDto(
        Guid LeadId, string LeadName, string? WhatsappPhone, string? RenderedInitialMessage,
        string ProductKey, string Status, IReadOnlyList<ConversationMessageDto> Messages);

    public record SendReplyRequest(string Text);

    [HttpGet]
    public async Task<ActionResult<IEnumerable<ConversationListItem>>> List(CancellationToken ct)
    {
        var sellerId = CurrentUser.Id(User);
        var isAdmin = CurrentUser.IsAdmin(User);

        // All leads assigned to me (or all if admin) — plus a summary of their latest msg.
        var leadQ = _db.Leads.AsNoTracking();
        if (!isAdmin) leadQ = leadQ.Where(l => l.SellerId == sellerId);

        var items = await (from l in leadQ
                           let latest = _db.ConversationMessages.Where(m => m.LeadId == l.Id)
                                          .OrderByDescending(m => m.Timestamp).FirstOrDefault()
                           let unread = _db.ConversationMessages.Count(m => m.LeadId == l.Id
                                          && m.Direction == MessageDirection.Inbound && !m.IsRead)
                           where latest != null // only leads with at least 1 message
                           orderby unread descending, latest.Timestamp descending
                           select new ConversationListItem(
                               l.Id, l.Name, l.City, l.ProductKey, l.Status.ToString(),
                               latest.Text, latest.Direction, latest.Timestamp, unread))
                       .Take(200).ToListAsync(ct);
        return items;
    }

    [HttpGet("{leadId:guid}")]
    public async Task<ActionResult<ConversationThreadDto>> Thread(Guid leadId, CancellationToken ct)
    {
        var sellerId = CurrentUser.Id(User);
        var isAdmin = CurrentUser.IsAdmin(User);
        var lead = await _db.Leads.AsNoTracking().FirstOrDefaultAsync(l => l.Id == leadId, ct);
        if (lead is null) return NotFound();
        if (!isAdmin && lead.SellerId != sellerId) return Forbid();

        var messages = await _db.ConversationMessages.AsNoTracking()
            .Where(m => m.LeadId == leadId)
            .OrderBy(m => m.Timestamp)
            .Select(m => new ConversationMessageDto(m.Id, m.Direction, m.Text, m.Timestamp, m.Status, m.IsRead))
            .ToListAsync(ct);

        // Mark inbound as read on view.
        if (!isAdmin) await _conv.MarkReadAsync(sellerId, leadId, ct);

        return new ConversationThreadDto(lead.Id, lead.Name, lead.WhatsappPhone, lead.RenderedMessage,
            lead.ProductKey, lead.Status.ToString(), messages);
    }

    [HttpPost("{leadId:guid}/reply")]
    public async Task<IActionResult> Reply(Guid leadId, [FromBody] SendReplyRequest req, CancellationToken ct)
    {
        var sellerId = CurrentUser.Id(User);
        if (string.IsNullOrWhiteSpace(req.Text)) return BadRequest(new { error = "Texto vacío" });
        try
        {
            var msg = await _conv.SendReplyAsync(sellerId, leadId, req.Text, ct);
            if (msg is null) return NotFound();
            return Ok(new ConversationMessageDto(msg.Id, msg.Direction, msg.Text, msg.Timestamp, msg.Status, msg.IsRead));
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpGet("unread-count")]
    public async Task<IActionResult> UnreadCount(CancellationToken ct)
    {
        var sellerId = CurrentUser.Id(User);
        var count = await _db.ConversationMessages
            .CountAsync(m => m.SellerId == sellerId
                          && m.Direction == MessageDirection.Inbound && !m.IsRead, ct);
        return Ok(new { count });
    }
}
