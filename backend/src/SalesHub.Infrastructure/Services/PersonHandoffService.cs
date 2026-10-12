using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SalesHub.Core.Abstractions;
using SalesHub.Core.Domain.Entities;
using SalesHub.Core.Domain.Enums;
using SalesHub.Infrastructure.Persistence;

namespace SalesHub.Infrastructure.Services;

/// <summary>
/// Pase de un lead a una persona (Mateo) que sigue la charla en la MISMA línea: el bot se calla en
/// esa charla, el lead queda etiquetado y con nota, y a la persona le llega un aviso corto a su WhatsApp
/// personal desde esa línea (la que acaba de recibir el mensaje, así que está viva). Las respuestas
/// salen siempre por la línea donde vive la charla, no por la del dueño del lead.
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

        // El aviso sale por la línea donde vive la charla (la de la app), que es la que está conectada.
        var line = await _lineSender.ResolveForLeadAsync(lead, ct);
        var sent = false;
        if (!string.IsNullOrWhiteSpace(person?.WhatsappPhone) && line is not null)
        {
            try { sent = await _lineSender.SendToLeadAsync(line, person.WhatsappPhone!, summary, ct); }
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

    /// <summary>
    /// El aviso que le llega a la persona (sin mandar nada; lo usa también el simulador). Corto a
    /// propósito: el chat se lee rápido en el WhatsApp Web de la línea, no hace falta resumirlo;
    /// solo tiene que saber QUÉ chat y en QUÉ línea abrirlo.
    /// </summary>
    public async Task<string> BuildSummaryAsync(Lead lead, string reason, string? latestInbound, string? context, CancellationToken ct)
    {
        var app = await _db.Products.AsNoTracking().Where(p => p.ProductKey == lead.ProductKey)
            .Select(p => p.DisplayName).FirstOrDefaultAsync(ct) ?? lead.ProductKey;
        var line = await _lineSender.ResolveForLeadAsync(lead, ct);
        var linePhone = line?.InstanceName is null ? null : await _db.EvolutionInstances.AsNoTracking()
            .Where(i => i.InstanceName == line.InstanceName).Select(i => i.ConnectedPhoneNumber).FirstOrDefaultAsync(ct);
        var phone = string.IsNullOrWhiteSpace(lead.WhatsappPhone) ? "" : $" ({lead.WhatsappPhone})";
        return $"{app}: {lead.Name}{phone} necesita atencion ({reason}).\n" +
               $"abrilo en la linea de {app}" + (string.IsNullOrWhiteSpace(linePhone) ? "" : $" (+{linePhone})") +
               ", el bot ya no le contesta.";
    }
}
