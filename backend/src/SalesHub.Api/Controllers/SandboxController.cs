using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SalesHub.Api.Auth;
using SalesHub.Core.Abstractions;
using SalesHub.Core.Domain.Entities;
using SalesHub.Core.Domain.Enums;
using SalesHub.Infrastructure.Persistence;
using SalesHub.Infrastructure.Services;

namespace SalesHub.Api.Controllers;

/// <summary>
/// Simulador del bot (/probar-bot): una persona escribe como si fuera el lead y el bot contesta con la
/// MISMA lógica que en producción (guion de la app, diccionario, pase a la persona, horarios reales de
/// Calendly). No sale nada por WhatsApp, no se avisa a nadie y la reserva en Calendly se simula.
/// El lead de prueba no tiene teléfono ni vendedor y queda con la etiqueta "sandbox": ningún worker lo
/// toma (reparto, cadencias, remarketing, agente) y el CRM no lo muestra.
/// </summary>
[ApiController]
[Route("api/sandbox")]
[Authorize]
public class SandboxController : ControllerBase
{
    public const string Tag = "sandbox";

    private readonly ApplicationDbContext _db;
    private readonly OnboardingService _onboarding;
    private readonly IntentClassifier _intents;
    private readonly PersonHandoffService _handoff;
    private readonly IMessageRenderer _renderer;

    public SandboxController(ApplicationDbContext db, OnboardingService onboarding, IntentClassifier intents,
        PersonHandoffService handoff, IMessageRenderer renderer)
    {
        _db = db; _onboarding = onboarding; _intents = intents; _handoff = handoff; _renderer = renderer;
        _onboarding.DryRunBooking = true;
        _onboarding.DryRunProvision = true;
    }

    /// <summary>
    /// Ajustes para probar sin tocar la config guardada: pase a una persona, cómo se presenta, cuántas
    /// preguntas y el evento de Calendly. Vacíos = los de la app.
    /// </summary>
    public record Overrides(bool? Handoff, Guid? HandoffSellerId, int? AfterQuestions, string? PresentAs, string? DemoEventTypeUri,
        string? HandoffMessage);

    public record StartRequest(string ProductKey, string? LeadName, Overrides? Overrides);
    public record MessageRequest(string Text, Overrides? Overrides);

    public record BotTurn(List<string> Messages, string? IntentKey, bool? Confident, string? Decision, string? Handoff,
        string? HandoffSummary, bool Finished);

    [HttpPost("start")]
    public async Task<IActionResult> Start([FromBody] StartRequest req, CancellationToken ct)
    {
        if (!CurrentUser.IsAdmin(User)) return Forbid();
        var product = await _db.Products.AsNoTracking().FirstOrDefaultAsync(p => p.ProductKey == req.ProductKey, ct);
        if (product is null) return BadRequest(new { error = "App desconocida." });
        var cfg = await ConfigAsync(req.ProductKey, req.Overrides, ct);
        if (cfg is null) return BadRequest(new { error = "La app no tiene guion de onboarding cargado." });

        var now = DateTimeOffset.UtcNow;
        var lead = new Lead
        {
            Id = Guid.NewGuid(),
            ProductKey = product.ProductKey,
            Name = string.IsNullOrWhiteSpace(req.LeadName) ? "prueba bot" : req.LeadName.Trim(),
            Source = LeadSource.WhatsAppAd,
            Status = LeadStatus.Replied,        // no New: el reparto automático no lo toma
            Tags = new List<string> { Tag },
            ManualAssignedAt = now,
            CreatedAt = now,
            UpdatedAt = now,
        };
        _db.Leads.Add(lead);
        await _db.SaveChangesAsync(ct);

        // Primer mensaje del bot: el saludo + la 1ª pregunta (como cuando entra alguien por el anuncio).
        var ob = await _onboarding.ProcessAsync(lead, string.Empty, cfg, ct);
        var turn = await ToTurnAsync(lead, product, cfg, ob, null, null, ct);
        return Ok(new { leadId = lead.Id, turn });
    }

    [HttpPost("{leadId:guid}/message")]
    public async Task<IActionResult> Message(Guid leadId, [FromBody] MessageRequest req, CancellationToken ct)
    {
        if (!CurrentUser.IsAdmin(User)) return Forbid();
        var lead = await _db.Leads.Include(l => l.Product).FirstOrDefaultAsync(l => l.Id == leadId, ct);
        if (lead is null || !lead.Tags.Contains(Tag)) return NotFound();
        var cfg = await ConfigAsync(lead.ProductKey, req.Overrides, ct);
        if (cfg is null) return BadRequest(new { error = "La app no tiene guion de onboarding cargado." });

        var text = (req.Text ?? "").Trim();
        if (text.Length == 0) return BadRequest(new { error = "Escribí algo." });
        var prev = await _db.ConversationMessages.AsNoTracking()
            .Where(m => m.LeadId == leadId && m.Direction == MessageDirection.Outbound)
            .OrderByDescending(m => m.Timestamp).Select(m => m.Text).FirstOrDefaultAsync(ct);
        var (key, confident) = (await _intents.GetMatcherAsync(ct)).ClassifyWithConfidence(text, prev);
        _db.ConversationMessages.Add(new ConversationMessage
        {
            Id = Guid.NewGuid(), LeadId = leadId, Direction = MessageDirection.Inbound,
            Status = MessageDeliveryStatus.Received, Text = text, Timestamp = DateTimeOffset.UtcNow,
            IsRead = true, IntentKey = key, IntentConfident = confident, EvolutionInstance = "sandbox",
        });
        await _db.SaveChangesAsync(ct);

        var onb = await _db.Set<LeadOnboarding>().AsNoTracking().FirstOrDefaultAsync(o => o.LeadId == leadId, ct);
        if (onb?.Step == OnboardingService.StepHumanHandoff)
            return Ok(new BotTurn(new(), key, confident, "el bot ya se lo pasó a la persona: esto lo contesta ella", null, null, true));

        var ob = await _onboarding.ProcessAsync(lead, text, cfg, ct, text);
        return Ok(await ToTurnAsync(lead, lead.Product!, cfg, ob, key, confident, ct));
    }

    [HttpDelete("{leadId:guid}")]
    public async Task<IActionResult> Reset(Guid leadId, CancellationToken ct)
    {
        if (!CurrentUser.IsAdmin(User)) return Forbid();
        // Borra este y cualquier otro lead de prueba viejo (FKs en cascada).
        var ids = await _db.Leads.Where(l => l.Id == leadId || l.Tags.Contains(Tag)).Select(l => l.Id).ToListAsync(ct);
        await _db.Leads.Where(l => ids.Contains(l.Id) && l.Tags.Contains(Tag)).ExecuteDeleteAsync(ct);
        return Ok(new { ok = true, deleted = ids.Count });
    }

    /// <summary>Config de la app con los ajustes de prueba aplicados en memoria (no se guarda).</summary>
    private async Task<OnboardingConfig?> ConfigAsync(string productKey, Overrides? o, CancellationToken ct)
    {
        var cfg = await _db.OnboardingConfigs.AsNoTracking().FirstOrDefaultAsync(c => c.ProductKey == productKey, ct);
        if (cfg is null || cfg.Questions.Count == 0) return null;
        if (o is null) return cfg;
        if (o.Handoff == true) cfg.HandoffSellerId = o.HandoffSellerId ?? cfg.HandoffSellerId ?? Guid.Empty;
        if (o.Handoff == false) cfg.HandoffSellerId = null;
        if (o.AfterQuestions is > 0) cfg.HandoffAfterQuestions = o.AfterQuestions.Value;
        if (o.PresentAs is not null) cfg.PresentAs = string.IsNullOrWhiteSpace(o.PresentAs) ? null : o.PresentAs.Trim().ToLowerInvariant();
        if (o.DemoEventTypeUri is not null) cfg.DemoEventTypeUri = string.IsNullOrWhiteSpace(o.DemoEventTypeUri) ? null : o.DemoEventTypeUri;
        if (o.HandoffMessage is not null) cfg.HandoffMessage = o.HandoffMessage;
        return cfg;
    }

    /// <summary>Arma lo que habría mandado el bot (mismo render que el envío real) y lo guarda en la charla de prueba.</summary>
    private async Task<BotTurn> ToTurnAsync(Lead lead, Product product, OnboardingConfig cfg, OnboardingResult ob,
        string? key, bool? confident, CancellationToken ct)
    {
        var parts = new List<string>();
        void AddText(string? t)
        {
            if (string.IsNullOrWhiteSpace(t)) return;
            var rendered = CopyStyle.Clean(_renderer.RenderTemplate(t, lead, product, null).Replace("{contacto}", ""));
            parts.AddRange(rendered.Split("[NUEVO_MENSAJE]", StringSplitOptions.RemoveEmptyEntries)
                .Select(p => p.Trim()).Where(p => p.Length > 0));
        }

        string decision;
        string? summary = null;
        if (ob.Handoff is not null)
        {
            AddText(ob.Reply);
            decision = "pasa a la persona";
            summary = await _handoff.BuildSummaryAsync(lead, ob.Handoff, null, null, ct);
        }
        else if (ob.OffScript)
        {
            decision = "fuera del guion: acá contesta la IA (hoy sin crédito) y después repite la pregunta pendiente";
            if (!string.IsNullOrWhiteSpace(ob.PendingQuestion)) AddText("[IA contestaria aca][NUEVO_MENSAJE]" + ob.PendingQuestion);
        }
        else
        {
            AddText(ob.Reply);
            if (!string.IsNullOrWhiteSpace(ob.PostMediaText)) AddText(ob.PostMediaText);
            decision = ob.Provisioned ? "creó la cuenta (alta)" : "sigue el guion";
        }

        var now = DateTimeOffset.UtcNow;
        foreach (var p in parts)
        {
            _db.ConversationMessages.Add(new ConversationMessage
            {
                Id = Guid.NewGuid(), LeadId = lead.Id, Direction = MessageDirection.Outbound,
                Status = MessageDeliveryStatus.Sent, Text = p, Timestamp = now = now.AddMilliseconds(10),
                IsRead = true, EvolutionInstance = "sandbox",
            });
        }
        await _db.SaveChangesAsync(ct);
        return new BotTurn(parts, key, confident, decision, ob.Handoff, summary, ob.Handoff is not null);
    }
}
