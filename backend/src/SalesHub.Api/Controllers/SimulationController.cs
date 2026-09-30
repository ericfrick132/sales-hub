using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SalesHub.Api.Auth;
using SalesHub.Core.Domain.Entities;
using SalesHub.Infrastructure.Persistence;
using SalesHub.Infrastructure.Services;

namespace SalesHub.Api.Controllers;

/// <summary>
/// /simulacion: conversaciones reales con lo que habría hecho el bot en cada mensaje del lead
/// (diccionario, sin IA, nada se manda) y la opinión de una persona sobre cada decisión.
/// </summary>
[ApiController]
[Route("api/simulation")]
[Authorize]
public class SimulationController : ControllerBase
{
    private static readonly HashSet<string> Actions = new() { IntentReplyPlanner.Reply, IntentReplyPlanner.NoReply, IntentReplyPlanner.ToHuman };
    private readonly ApplicationDbContext _db;

    public SimulationController(ApplicationDbContext db) => _db = db;

    /// <summary>
    /// Conversaciones con mensajes del lead, las más recientes primero.
    /// <paramref name="filter"/>: "todas", "bot" (el bot habría actuado en algún mensaje),
    /// "pendientes" (con decisión del bot sin opinión todavía), "con_feedback".
    /// </summary>
    [HttpGet("conversations")]
    public async Task<IActionResult> Conversations([FromQuery] string? product = null, [FromQuery] string filter = "bot",
        [FromQuery] string? q = null, [FromQuery] int skip = 0, [FromQuery] int take = 40, CancellationToken ct = default)
    {
        if (!CurrentUser.IsAdmin(User)) return Forbid();
        var leads = _db.Leads.AsNoTracking()
            .Where(l => !l.Tags.Contains(SandboxController.Tag)
                     && _db.ConversationMessages.Any(m => m.LeadId == l.Id && m.Direction == MessageDirection.Inbound));
        if (!string.IsNullOrWhiteSpace(product)) leads = leads.Where(l => l.ProductKey == product);
        if (!string.IsNullOrWhiteSpace(q)) { var t = q.Trim().ToLower(); leads = leads.Where(l => l.Name.ToLower().Contains(t)); }
        leads = filter switch
        {
            "bot" => leads.Where(l => _db.ConversationMessages.Any(m => m.LeadId == l.Id && m.Direction == MessageDirection.Inbound
                        && (m.IntentAction == IntentReplyPlanner.Reply || m.IntentAction == IntentReplyPlanner.NoReply))),
            "pendientes" => leads.Where(l => _db.ConversationMessages.Any(m => m.LeadId == l.Id && m.Direction == MessageDirection.Inbound
                        && (m.IntentAction == IntentReplyPlanner.Reply || m.IntentAction == IntentReplyPlanner.NoReply)
                        && !_db.IntentFeedbacks.Any(f => f.MessageId == m.Id))),
            "con_feedback" => leads.Where(l => _db.IntentFeedbacks.Any(f => f.LeadId == l.Id)),
            _ => leads,
        };

        var total = await leads.CountAsync(ct);
        var rows = await leads
            .Select(l => new
            {
                l.Id, l.Name, l.ProductKey,
                seller = l.Seller != null ? l.Seller.DisplayName : null,
                inbound = _db.ConversationMessages.Count(m => m.LeadId == l.Id && m.Direction == MessageDirection.Inbound),
                botActs = _db.ConversationMessages.Count(m => m.LeadId == l.Id && m.Direction == MessageDirection.Inbound
                    && (m.IntentAction == IntentReplyPlanner.Reply || m.IntentAction == IntentReplyPlanner.NoReply)),
                feedback = _db.IntentFeedbacks.Count(f => f.LeadId == l.Id),
                lastAt = _db.ConversationMessages.Where(m => m.LeadId == l.Id).Max(m => (DateTimeOffset?)m.Timestamp),
            })
            .OrderByDescending(x => x.lastAt)
            .Skip(Math.Max(0, skip)).Take(Math.Clamp(take, 1, 100))
            .ToListAsync(ct);
        return Ok(new { total, rows });
    }

    /// <summary>El chat completo de un lead con la decisión del bot y la opinión en cada mensaje del lead.</summary>
    [HttpGet("conversations/{leadId:guid}")]
    public async Task<IActionResult> Conversation(Guid leadId, CancellationToken ct)
    {
        if (!CurrentUser.IsAdmin(User)) return Forbid();
        var lead = await _db.Leads.AsNoTracking().Where(l => l.Id == leadId)
            .Select(l => new { l.Id, l.Name, l.ProductKey, seller = l.Seller != null ? l.Seller.DisplayName : null, l.WhatsappPhone })
            .FirstOrDefaultAsync(ct);
        if (lead is null) return NotFound();
        var feedback = await _db.IntentFeedbacks.AsNoTracking().Where(f => f.LeadId == leadId).ToDictionaryAsync(f => f.MessageId, ct);
        var msgs = await _db.ConversationMessages.AsNoTracking()
            .Where(m => m.LeadId == leadId)
            .OrderBy(m => m.Timestamp)
            .Select(m => new
            {
                m.Id, inbound = m.Direction == MessageDirection.Inbound, m.Text, m.Timestamp,
                m.IntentKey, m.IntentConfident, m.IntentAction, m.IntentSimulatedReply, m.IntentWhy,
            })
            .ToListAsync(ct);
        return Ok(new
        {
            lead,
            messages = msgs.Select(m => new
            {
                m.Id, m.inbound, m.Text, m.Timestamp, m.IntentKey, m.IntentConfident, m.IntentAction,
                m.IntentSimulatedReply, m.IntentWhy,
                feedback = feedback.TryGetValue(m.Id, out var f)
                    ? new { f.Verdict, f.CorrectKey, f.CorrectAction, f.BetterReply, f.Note, f.UpdatedAt }
                    : null,
            }),
        });
    }

    public record FeedbackRequest(string Verdict, string? CorrectKey, string? CorrectAction, string? BetterReply, string? Note);

    /// <summary>Guarda (o reemplaza) la opinión sobre la decisión del bot en un mensaje del lead.</summary>
    [HttpPut("feedback/{messageId:guid}")]
    public async Task<IActionResult> SaveFeedback(Guid messageId, [FromBody] FeedbackRequest req, CancellationToken ct)
    {
        if (!CurrentUser.IsAdmin(User)) return Forbid();
        if (req.Verdict is not ("bien" or "mal")) return BadRequest(new { error = "La opinión es 'bien' o 'mal'." });
        if (req.CorrectAction is not null && !Actions.Contains(req.CorrectAction)) return BadRequest(new { error = "Acción desconocida." });
        var msg = await _db.ConversationMessages.AsNoTracking()
            .Where(m => m.Id == messageId && m.Direction == MessageDirection.Inbound)
            .Select(m => new { m.Id, m.LeadId, m.IntentKey, m.IntentAction, m.IntentSimulatedReply })
            .FirstOrDefaultAsync(ct);
        if (msg is null) return NotFound();

        var f = await _db.IntentFeedbacks.FirstOrDefaultAsync(x => x.MessageId == messageId, ct);
        if (f is null)
        {
            f = new IntentFeedback { Id = Guid.NewGuid(), MessageId = messageId, LeadId = msg.LeadId, CreatedBySellerId = CurrentUser.Id(User) };
            _db.IntentFeedbacks.Add(f);
        }
        f.IntentKey = msg.IntentKey;
        f.IntentAction = msg.IntentAction;
        f.SimulatedReply = msg.IntentSimulatedReply;
        f.Verdict = req.Verdict;
        f.CorrectKey = req.Verdict == "mal" && !string.IsNullOrWhiteSpace(req.CorrectKey) ? req.CorrectKey.Trim() : null;
        f.CorrectAction = req.Verdict == "mal" ? req.CorrectAction : null;
        f.BetterReply = string.IsNullOrWhiteSpace(req.BetterReply) ? null : CopyStyle.Clean(req.BetterReply.Trim());
        f.Note = string.IsNullOrWhiteSpace(req.Note) ? null : req.Note.Trim();
        f.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);
        return Ok(new { ok = true });
    }

    [HttpDelete("feedback/{messageId:guid}")]
    public async Task<IActionResult> DeleteFeedback(Guid messageId, CancellationToken ct)
    {
        if (!CurrentUser.IsAdmin(User)) return Forbid();
        await _db.IntentFeedbacks.Where(f => f.MessageId == messageId).ExecuteDeleteAsync(ct);
        return Ok(new { ok = true });
    }

    /// <summary>Resumen de las opiniones: por tipo y acción del bot, cuántas bien / mal, y las respuestas sugeridas.</summary>
    [HttpGet("feedback/summary")]
    public async Task<IActionResult> FeedbackSummary(CancellationToken ct)
    {
        if (!CurrentUser.IsAdmin(User)) return Forbid();
        var all = await _db.IntentFeedbacks.AsNoTracking().OrderByDescending(f => f.UpdatedAt).ToListAsync(ct);
        return Ok(new
        {
            total = all.Count,
            good = all.Count(f => f.Verdict == "bien"),
            bad = all.Count(f => f.Verdict == "mal"),
            byType = all.GroupBy(f => new { f.IntentKey, f.IntentAction }).Select(g => new
            {
                g.Key.IntentKey, g.Key.IntentAction,
                good = g.Count(f => f.Verdict == "bien"),
                bad = g.Count(f => f.Verdict == "mal"),
            }).OrderByDescending(x => x.good + x.bad),
            betterReplies = all.Where(f => f.BetterReply != null).Take(100)
                .Select(f => new { f.IntentKey, f.BetterReply, f.SimulatedReply, f.UpdatedAt }),
        });
    }
}
