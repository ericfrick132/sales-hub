using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SalesHub.Core.Domain;
using SalesHub.Core.Domain.Entities;
using SalesHub.Core.Domain.Enums;
using SalesHub.Infrastructure.Persistence;

namespace SalesHub.Api.Controllers;

/// <summary>
/// Qué se manda y a quién. Más fino que los flags de runner (que apagan un motor entero):
/// acá se prende/apaga, POR ORIGEN de lead, cada tipo de mensaje automático — mensajes
/// nuevos (primer contacto), seguimiento (cadencia + re-enganches) y respuestas del bot.
///
/// Los gates son de ENVÍO: lo que ya está encolado queda esperando y sale solo cuando el
/// switch se vuelve a prender. Nada de esto toca los mensajes que manda un humano a mano.
/// </summary>
[ApiController]
[Route("api/messaging-policy")]
[Authorize(Roles = "Admin")]
public class MessagingPolicyController : ControllerBase
{
    private readonly ApplicationDbContext _db;
    public MessagingPolicyController(ApplicationDbContext db) { _db = db; }

    /// <summary>Tipos de mensaje que se pueden prender/apagar (la "columna" de la matriz).</summary>
    public const string KindOutreach = "outreach";
    public const string KindFollowup = "followup";
    public const string KindReply = "reply";

    public record GroupDto(
        string Key, string Label, string Hint,
        bool AllowOutreach, bool AllowFollowup, bool AllowReply,
        int QueuedOutreach, int QueuedFollowup, int Leads);

    public record SetRequest(bool Enabled);

    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken ct)
    {
        var rows = (await _db.MessagingPolicies.AsNoTracking().ToListAsync(ct))
            .ToDictionary(r => r.SourceGroup, StringComparer.OrdinalIgnoreCase);

        // Cuántos mensajes hay esperando en la cola por origen, separando primer contacto de
        // seguimiento — así se ve de una qué está frenando cada switch.
        var queued = await _db.Outbox
            .Where(o => o.Status == OutboxStatus.Scheduled && o.Lead != null)
            .Select(o => new
            {
                o.Lead!.Source,
                Followup = _db.Outbox.Any(x => x.LeadId == o.LeadId && x.Status == OutboxStatus.Sent)
            })
            .GroupBy(x => new { x.Source, x.Followup })
            .Select(g => new { g.Key.Source, g.Key.Followup, Count = g.Count() })
            .ToListAsync(ct);

        var leadCounts = await _db.Leads
            .GroupBy(l => l.Source)
            .Select(g => new { Source = g.Key, Count = g.Count() })
            .ToListAsync(ct);

        var result = MessagingSourceGroups.All.Select(g =>
        {
            rows.TryGetValue(g.Key, out var p);
            var inGroup = MessagingSourceGroups.SourcesOf(g.Key).ToHashSet();
            return new GroupDto(
                g.Key, g.Label, g.Hint,
                p?.AllowOutreach ?? true,
                p?.AllowFollowup ?? true,
                p?.AllowReply ?? true,
                queued.Where(q => !q.Followup && inGroup.Contains(q.Source)).Sum(q => q.Count),
                queued.Where(q => q.Followup && inGroup.Contains(q.Source)).Sum(q => q.Count),
                leadCounts.Where(l => inGroup.Contains(l.Source)).Sum(l => l.Count));
        });

        return Ok(result);
    }

    /// <summary>
    /// Prende/apaga UN tipo de mensaje para UN origen. Un campo por request: así el mismo
    /// endpoint sirve para la matriz de la web y para el menú del bot maestro.
    /// </summary>
    [HttpPost("{group}/{kind}")]
    public async Task<IActionResult> Set(string group, string kind, [FromBody] SetRequest req, CancellationToken ct)
    {
        if (!MessagingSourceGroups.IsKnown(group)) return BadRequest(new { error = "Origen desconocido" });
        kind = (kind ?? string.Empty).ToLowerInvariant();
        if (kind is not (KindOutreach or KindFollowup or KindReply))
            return BadRequest(new { error = "Tipo desconocido (outreach | followup | reply)" });

        var row = await _db.MessagingPolicies.FirstOrDefaultAsync(p => p.SourceGroup == group, ct);
        if (row is null)
        {
            // Primera vez que se toca este origen: la fila arranca con todo permitido (el
            // default histórico) y sólo cambia el campo pedido.
            row = new MessagingPolicy { SourceGroup = group };
            _db.MessagingPolicies.Add(row);
        }

        switch (kind)
        {
            case KindOutreach: row.AllowOutreach = req.Enabled; break;
            case KindFollowup: row.AllowFollowup = req.Enabled; break;
            case KindReply: row.AllowReply = req.Enabled; break;
        }
        row.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);

        return Ok(new
        {
            group = row.SourceGroup,
            allowOutreach = row.AllowOutreach,
            allowFollowup = row.AllowFollowup,
            allowReply = row.AllowReply
        });
    }
}
