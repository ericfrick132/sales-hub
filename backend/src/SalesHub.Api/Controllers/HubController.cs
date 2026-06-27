using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SalesHub.Core.Abstractions;
using SalesHub.Core.Domain.Entities;
using SalesHub.Core.Domain.Enums;
using SalesHub.Infrastructure.Persistence;

namespace SalesHub.Api.Controllers;

/// <summary>
/// Punto de entrada del "big-CRM": los productos propios (GymHero/TurnosPro) y los anuncios
/// PUSHEAN leads y DELEGAN el envío de WhatsApp acá, en vez de hablarle a Evolution directo.
/// Auth por header X-Api-Key (config Hub:ApiKey), no JWT — por eso AllowAnonymous + chequeo manual.
/// </summary>
[ApiController]
[Route("api/hub")]
[AllowAnonymous]
public class HubController : ControllerBase
{
    private readonly ILeadIngestService _ingest;
    private readonly ApplicationDbContext _db;
    private readonly IConfiguration _config;
    private readonly ILogger<HubController> _log;

    public HubController(ILeadIngestService ingest, ApplicationDbContext db, IConfiguration config, ILogger<HubController> log)
    {
        _ingest = ingest; _db = db; _config = config; _log = log;
    }

    /// <summary>
    /// Ingesta un lead B2B (de un anuncio, OTP/setup, demo o reengage). Idempotente por
    /// (producto, teléfono): un segundo push del mismo teléfono devuelve Duplicate sin recrear.
    /// </summary>
    [HttpPost("leads")]
    public async Task<IActionResult> IngestLead([FromBody] HubLeadRequest req, CancellationToken ct)
    {
        if (!ValidApiKey()) return Unauthorized();
        if (string.IsNullOrWhiteSpace(req.ProductKey))
            return BadRequest(new { error = "productKey requerido" });

        var source = MapSource(req.LeadType);
        var result = await _ingest.IngestAsync(new LeadIngestRequest(
            req.ProductKey, source, req.ExternalId, req.Name, req.BusinessName, req.Phone, req.Email), ct);

        if (result.Outcome == LeadIngestOutcome.NoProduct)
            return UnprocessableEntity(new { error = $"producto '{req.ProductKey}' no existe en SalesHub" });

        _log.LogInformation("Hub ingest {Pk}/{Type}: {Outcome}", req.ProductKey, req.LeadType ?? "reengage", result.Outcome);
        return Ok(new { outcome = result.Outcome.ToString(), leadId = result.LeadId });
    }

    /// <summary>
    /// El producto delega en SalesHub el envío de un WhatsApp a un dueño de negocio. Se encola
    /// en el outbox del vendedor asignado (mismo runner humanizado / anti-ban / hilos). El lead
    /// tiene que existir y estar asignado (pushealo antes con /hub/leads).
    /// </summary>
    [HttpPost("send")]
    public async Task<IActionResult> Send([FromBody] HubSendRequest req, CancellationToken ct)
    {
        if (!ValidApiKey()) return Unauthorized();
        if (string.IsNullOrWhiteSpace(req.ProductKey) || string.IsNullOrWhiteSpace(req.Text))
            return BadRequest(new { error = "productKey y text requeridos" });

        var phone = CleanPhone(req.Phone);
        var lead = await _db.Leads.Include(l => l.Seller).ThenInclude(s => s!.EvolutionInstance)
            .FirstOrDefaultAsync(l => l.ProductKey == req.ProductKey
                && ((req.ExternalId != null && l.ExternalId == req.ExternalId)
                    || (req.ExternalId == null && phone != null && l.WhatsappPhone == phone)), ct);

        if (lead is null)
            return UnprocessableEntity(new { error = "lead no encontrado; pushealo antes con /hub/leads" });
        if (lead.SellerId is null || lead.Seller?.EvolutionInstance is null || lead.WhatsappPhone is null)
            return UnprocessableEntity(new { error = "lead sin vendedor o instancia de WhatsApp asignada" });

        var outbox = new MessageOutbox
        {
            Id = Guid.NewGuid(),
            LeadId = lead.Id,
            SellerId = lead.SellerId.Value,
            Channel = MessageChannel.WhatsApp,
            EvolutionInstance = lead.Seller.EvolutionInstance.InstanceName,
            WhatsappPhone = lead.WhatsappPhone,
            Message = req.Text,
            ScheduledAt = DateTimeOffset.UtcNow,
            Status = OutboxStatus.Scheduled
        };
        _db.Outbox.Add(outbox);
        await _db.SaveChangesAsync(ct);

        _log.LogInformation("Hub send {Pk}: encolado outbox {Id} para lead {Lead}", req.ProductKey, outbox.Id, lead.Id);
        return Ok(new { outboxId = outbox.Id, leadId = lead.Id });
    }

    /// <summary>
    /// Contrato común: la app baja TODAS sus secuencias de follow-up ACTIVAS (config central,
    /// agnóstica). Devuelve una lista de { trigger, steps[] } — la app elige cuáles ejecutar
    /// según sus propios disparadores. Solo lectura.
    /// </summary>
    [HttpGet("followup-config")]
    public async Task<IActionResult> GetFollowupConfig([FromQuery] string productKey, CancellationToken ct)
    {
        if (!ValidApiKey()) return Unauthorized();
        if (string.IsNullOrWhiteSpace(productKey)) return BadRequest(new { error = "productKey requerido" });

        var sequences = await _db.FollowupSequences.AsNoTracking()
            .Where(s => s.ProductKey == productKey && s.Enabled)
            .OrderBy(s => s.Trigger)
            .ToListAsync(ct);

        return Ok(new
        {
            sequences = sequences.Select(s => new
            {
                trigger = s.Trigger,
                steps = s.Steps.OrderBy(x => x.Order).ToList(),
                // BACKLOG (abandonos viejos): mensajes opcionales; null = la app usa su literal.
                backlogColdMessage = s.BacklogColdMessage,
                backlogWarmMessage = s.BacklogWarmMessage,
                backlogColdAfterDays = s.BacklogColdAfterDays,
            }),
        });
    }

    /// <summary>
    /// Contrato común: la app REPORTA un evento de su follow-up (triggered/step_sent/converted/
    /// gave_up) para los reportes centralizados por app. NO dispara ningún envío — es telemetría.
    /// </summary>
    [HttpPost("followup-events")]
    public async Task<IActionResult> IngestFollowupEvent([FromBody] HubFollowupEventRequest req, CancellationToken ct)
    {
        if (!ValidApiKey()) return Unauthorized();
        if (string.IsNullOrWhiteSpace(req.ProductKey) || string.IsNullOrWhiteSpace(req.EventType))
            return BadRequest(new { error = "productKey y eventType requeridos" });

        _db.FollowupEvents.Add(new FollowupEvent
        {
            ProductKey = req.ProductKey,
            Trigger = string.IsNullOrWhiteSpace(req.Trigger) ? null : req.Trigger.Trim(),
            ExternalRef = req.ExternalRef,
            EventType = req.EventType.Trim().ToLowerInvariant(),
            StepIndex = req.StepIndex ?? -1,
            Channel = string.IsNullOrWhiteSpace(req.Channel) ? null : req.Channel.Trim().ToLowerInvariant(),
            Detail = req.Detail,
        });
        await _db.SaveChangesAsync(ct);
        return Ok(new { ok = true });
    }

    private static LeadSource MapSource(string? leadType) => (leadType ?? string.Empty).ToLowerInvariant() switch
    {
        "ad" or "ads" or "anuncio" => LeadSource.WhatsAppAd,
        "otp" or "setup" or "onboarding" => LeadSource.ProductOnboarding,
        "demo" or "signup" => LeadSource.DemoSignup,
        _ => LeadSource.ProductReengage,
    };

    private static string? CleanPhone(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var digits = new string(raw.Where(char.IsDigit).ToArray());
        return digits.Length >= 8 ? digits : null;
    }

    private bool ValidApiKey()
    {
        var expected = _config["Hub:ApiKey"];
        if (string.IsNullOrEmpty(expected)) { _log.LogWarning("Hub:ApiKey no configurado — rechazando el push"); return false; }
        if (!Request.Headers.TryGetValue("X-Api-Key", out var provided)) return false;
        var a = Encoding.UTF8.GetBytes(provided.ToString());
        var b = Encoding.UTF8.GetBytes(expected);
        return a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b);
    }
}

public record HubLeadRequest(
    string ProductKey,
    string? ExternalId,
    string? Name,
    string? BusinessName,
    string? Phone,
    string? Email,
    string? LeadType);

public record HubSendRequest(
    string ProductKey,
    string? ExternalId,
    string? Phone,
    string Text);

public record HubFollowupEventRequest(
    string ProductKey,
    string? Trigger,
    string? ExternalRef,
    string EventType,
    int? StepIndex,
    string? Channel,
    string? Detail);
