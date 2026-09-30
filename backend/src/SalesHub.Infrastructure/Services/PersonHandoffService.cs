using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SalesHub.Core.Abstractions;
using SalesHub.Core.Domain.Entities;
using SalesHub.Core.Domain.Enums;
using SalesHub.Infrastructure.Persistence;

namespace SalesHub.Infrastructure.Services;

/// <summary>
/// Pase de un lead a una persona (Mateo) que sigue la charla en la MISMA línea: el bot se calla en
/// esa charla, el lead queda etiquetado y con nota, y a la persona le llega un resumen por WhatsApp
/// desde esa línea (la que acaba de recibir el mensaje, así que está viva). No cambia el dueño del
/// lead: si lo cambiara, las respuestas saldrían por otro número.
/// Lo usan el bot de pre-calificación (onboarding) y el remarketing cuando el lead contesta.
/// No guarda: lo persiste quien llama.
/// </summary>
public class PersonHandoffService
{
    public const string Tag = "para-mateo";

    private readonly ApplicationDbContext _db;
    private readonly SellerLineSender _lineSender;
    private readonly IAdminAlerter _alerter;
    private readonly ILogger<PersonHandoffService> _log;

    public PersonHandoffService(ApplicationDbContext db, SellerLineSender lineSender, IAdminAlerter alerter,
        ILogger<PersonHandoffService> log)
    {
        _db = db; _lineSender = lineSender; _alerter = alerter; _log = log;
    }

    /// <param name="personId">A quién se pasa (vendedor con su WhatsApp cargado). null = aviso al maestro.</param>
    /// <param name="reason">Por qué se pasa ahora ("respondió 2 preguntas", "contestó al remarketing").</param>
    /// <param name="latestInbound">El mensaje que disparó el pase, si todavía no está guardado.</param>
    /// <param name="context">Algo más para el resumen (ej. qué le habíamos escrito en el remarketing).</param>
    public async Task HandoffAsync(Lead lead, Guid? personId, string reason, string? latestInbound, string? context,
        CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        lead.BotMutedAt ??= now;
        if (!lead.Tags.Contains(Tag, StringComparer.OrdinalIgnoreCase))
            lead.Tags = lead.Tags.Append(Tag).ToList();   // lista nueva: el change tracker no ve los Add in-place
        if (lead.Status is LeadStatus.New or LeadStatus.Assigned or LeadStatus.Queued or LeadStatus.Sent or LeadStatus.Replied)
            lead.Status = LeadStatus.Interested;
        lead.UpdatedAt = now;

        var person = personId is null ? null : await _db.Sellers.AsNoTracking().Where(s => s.Id == personId)
            .Select(s => new { s.DisplayName, s.WhatsappPhone }).FirstOrDefaultAsync(ct);
        _db.LeadNotes.Add(new LeadNote
        {
            Id = Guid.NewGuid(),
            LeadId = lead.Id,
            Kind = LeadNoteKind.System,
            Text = $"Pasado a {person?.DisplayName ?? "una persona"} ({reason}).",
            CreatedAt = now,
        });

        var summary = await BuildSummaryAsync(lead, reason, latestInbound, context, ct);

        var instance = lead.SellerId is null ? null : await _db.EvolutionInstances.AsNoTracking()
            .Where(i => i.SellerId == lead.SellerId).Select(i => i.InstanceName).FirstOrDefaultAsync(ct);
        var sent = false;
        if (!string.IsNullOrWhiteSpace(person?.WhatsappPhone))
        {
            try { sent = await _lineSender.SendTextAsync(lead.SellerId, instance, person.WhatsappPhone!, summary, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException) { _log.LogWarning(ex, "Pase: falló el aviso por la línea del lead"); }
        }
        if (!sent)
        {
            // Sin número cargado o sin línea: al número maestro, así no se pierde.
            _log.LogWarning("Pase: no pude avisar a {Person} por su WhatsApp, va al maestro (lead {Lead})", person?.DisplayName, lead.Id);
            await _alerter.AlertAsync(summary, ct);
        }
        _log.LogInformation("Lead {Lead} pasado a {Person} ({Reason})", lead.Id, person?.DisplayName, reason);
    }

    /// <summary>El texto del resumen que le llega a la persona (sin mandar nada; lo usa también el simulador).</summary>
    public async Task<string> BuildSummaryAsync(Lead lead, string reason, string? latestInbound, string? context, CancellationToken ct)
    {
        // Resumen: lo último que escribió el lead + el contexto del pase.
        var previous = await _db.ConversationMessages.AsNoTracking()
            .Where(m => m.LeadId == lead.Id && m.Direction == MessageDirection.Inbound)
            .OrderByDescending(m => m.Timestamp).Take(4).Select(m => m.Text).ToListAsync(ct);
        previous.Reverse();
        if (!string.IsNullOrWhiteSpace(latestInbound) && (previous.Count == 0 || previous[^1] != latestInbound))
            previous.Add(latestInbound);
        var lines = previous.Where(t => !string.IsNullOrWhiteSpace(t)).TakeLast(4)
            .Select(t => "- " + (t.Length > 160 ? t[..160] + "…" : t).Replace('\n', ' '));
        var app = await _db.Products.AsNoTracking().Where(p => p.ProductKey == lead.ProductKey)
            .Select(p => p.DisplayName).FirstOrDefaultAsync(ct) ?? lead.ProductKey;
        var summary = $"lead para vos de {app}: {lead.Name} ({lead.WhatsappPhone})\n" +
                      $"motivo: {reason}\n" +
                      (string.IsNullOrWhiteSpace(context) ? "" : context.Trim() + "\n") +
                      "lo que escribio:\n" + string.Join("\n", lines) + "\n" +
                      "seguilo en el mismo chat (el bot ya no le contesta).";
        return summary;
    }
}
