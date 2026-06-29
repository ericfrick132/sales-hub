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

    // ─────────────────────────────────────────────────────────────────────────────
    // TRANSPORTE GESTIONADO POR LA APP (refactor "apps mandan, sales-hub orquesta").
    // Para productos con AppManagedTransport=true: sales-hub NO manda por Evolution; deja
    // los mensajes en el outbox y la app los baja acá, los manda por SU Evolution y ackea.
    // El inbound que recibe la app lo reenvía a /hub/inbound → el cerebro lo procesa.
    // ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// La app baja los mensajes que tiene que MANDAR ella (por su propia Evolution). Sólo
    /// productos AppManagedTransport. Devuelve filas "due" (ya paceadas por ScheduledAt)
    /// respetando el cap diario y la ventana horaria del producto, y las RECLAMA (LockedAt)
    /// para no servirlas dos veces. La app debe ackear cada una con /hub/outbound/ack.
    /// </summary>
    [HttpGet("outbound")]
    public async Task<IActionResult> GetOutbound([FromQuery] string productKey, CancellationToken ct)
    {
        if (!ValidApiKey()) return Unauthorized();
        if (string.IsNullOrWhiteSpace(productKey)) return BadRequest(new { error = "productKey requerido" });

        var product = await _db.Products.AsNoTracking().FirstOrDefaultAsync(p => p.ProductKey == productKey, ct);
        if (product is null || !product.AppManagedTransport)
            return Ok(Array.Empty<object>());

        var now = DateTimeOffset.UtcNow;
        var ar = now.ToOffset(TimeSpan.FromHours(-3)); // AR no tiene DST

        // Ventana horaria del producto (hora AR). Start>=End = sin restricción.
        if (product.SendHourStart < product.SendHourEnd
            && (ar.Hour < product.SendHourStart || ar.Hour >= product.SendHourEnd))
            return Ok(Array.Empty<object>());

        // Cap diario anti-ban: contactos NUEVOS (leads distintos) ya enviados hoy.
        var dayStart = new DateTimeOffset(ar.Date, TimeSpan.FromHours(-3));
        var cap = product.DailyLimit > 0 ? product.DailyLimit : 60;
        var contactedToday = await _db.Outbox
            .Where(o => o.Status == OutboxStatus.Sent && o.SentAt != null && o.SentAt >= dayStart
                     && o.Lead != null && o.Lead.ProductKey == productKey)
            .Select(o => o.LeadId).Distinct().CountAsync(ct);
        if (contactedToday >= cap)
            return Ok(Array.Empty<object>());

        var claimCutoff = now.AddMinutes(-5);
        var rows = await (
            from o in _db.Outbox
            where o.Status == OutboxStatus.Scheduled
               && o.Channel == MessageChannel.WhatsApp
               && o.ScheduledAt <= now
               && o.Lead != null && o.Lead.ProductKey == productKey
               && (o.LockedAt == null || o.LockedAt < claimCutoff)
            orderby o.Priority descending, o.ScheduledAt
            select o
        ).Take(20).ToListAsync(ct);

        foreach (var o in rows) o.LockedAt = now; // reclamar (re-servible tras 5 min si la app murió)
        if (rows.Count > 0) await _db.SaveChangesAsync(ct);

        return Ok(rows.Select(o => new
        {
            id = o.Id,
            leadId = o.LeadId,
            phone = o.WhatsappPhone,
            text = o.Message,
            priority = o.Priority,
        }));
    }

    /// <summary>La app confirma el resultado del envío de un mensaje bajado de /hub/outbound.</summary>
    [HttpPost("outbound/ack")]
    public async Task<IActionResult> AckOutbound([FromBody] HubOutboundAck req, CancellationToken ct)
    {
        if (!ValidApiKey()) return Unauthorized();
        var o = await _db.Outbox.FirstOrDefaultAsync(x => x.Id == req.Id, ct);
        if (o is null) return Ok(new { ok = false, error = "outbox no encontrado" });

        if (string.Equals(req.Status, "sent", StringComparison.OrdinalIgnoreCase))
        {
            o.Status = OutboxStatus.Sent;
            o.SentAt = DateTimeOffset.UtcNow;
            o.LockedAt = null;
            o.Error = null;
        }
        else
        {
            o.Attempts++;
            o.Error = req.Error;
            if (o.Attempts >= 3) o.Status = OutboxStatus.Failed;
            else o.LockedAt = null; // liberar para que se re-sirva
        }
        await _db.SaveChangesAsync(ct);
        _log.LogInformation("Hub ack outbox {Id}: {Status} (attempts {N})", o.Id, o.Status, o.Attempts);
        return Ok(new { ok = true, status = o.Status.ToString() });
    }

    /// <summary>
    /// La app reenvía un WhatsApp INBOUND que recibió por su Evolution. Lo registramos en la
    /// conversación del lead (match por producto + últimos 8 dígitos del teléfono) y marcamos
    /// Replied; el cerebro (ConversationAgentService) lo levanta solo en su tick.
    /// </summary>
    [HttpPost("inbound")]
    public async Task<IActionResult> Inbound([FromBody] HubInboundRequest req, CancellationToken ct)
    {
        if (!ValidApiKey()) return Unauthorized();
        if (string.IsNullOrWhiteSpace(req.ProductKey) || string.IsNullOrWhiteSpace(req.Phone) || string.IsNullOrWhiteSpace(req.Text))
            return BadRequest(new { error = "productKey, phone y text requeridos" });

        var digits = new string(req.Phone.Where(char.IsDigit).ToArray());
        if (digits.Length < 8) return BadRequest(new { error = "teléfono inválido" });
        var suffix = digits[^8..];

        // Dedup por id del proveedor.
        if (!string.IsNullOrWhiteSpace(req.ProviderMessageId)
            && await _db.ConversationMessages.AnyAsync(m => m.WhatsappMessageId == req.ProviderMessageId, ct))
            return Ok(new { matched = true, dedup = true });

        var lead = await _db.Leads
            .Where(l => l.ProductKey == req.ProductKey && l.WhatsappPhone != null
                && l.WhatsappPhone
                    .Replace(" ", "").Replace("-", "").Replace("+", "")
                    .Replace("(", "").Replace(")", "").Replace(".", "")
                    .EndsWith(suffix))
            .OrderByDescending(l => l.CreatedAt)
            .FirstOrDefaultAsync(ct);
        if (lead is null)
        {
            _log.LogInformation("Hub inbound sin lead: {Pk}/{Phone}", req.ProductKey, digits);
            return Ok(new { matched = false });
        }

        var ts = req.TimestampUnix is long u ? DateTimeOffset.FromUnixTimeSeconds(u) : DateTimeOffset.UtcNow;
        _db.ConversationMessages.Add(new ConversationMessage
        {
            Id = Guid.NewGuid(),
            LeadId = lead.Id,
            SellerId = lead.SellerId,
            Direction = MessageDirection.Inbound,
            Status = MessageDeliveryStatus.Received,
            Text = req.Text,
            WhatsappMessageId = req.ProviderMessageId,
            Timestamp = ts,
            IsRead = false,
        });

        var isFirstReply = lead.FirstReplyAt is null;
        if (isFirstReply) lead.FirstReplyAt = ts;
        if (lead.Status is LeadStatus.Sent or LeadStatus.Queued or LeadStatus.Assigned)
            lead.Status = LeadStatus.Replied;
        lead.AiSuggestedReply = null;
        lead.AiSuggestedReplyAt = null;
        lead.UpdatedAt = DateTimeOffset.UtcNow;

        // Cortar el drip: si respondió, los steps de outreach pendientes ya no van.
        if (isFirstReply)
        {
            var pending = await _db.Outbox
                .Where(o => o.LeadId == lead.Id && o.Status == OutboxStatus.Scheduled)
                .ToListAsync(ct);
            foreach (var o in pending) o.Status = OutboxStatus.Cancelled;
        }

        await _db.SaveChangesAsync(ct);
        _log.LogInformation("Hub inbound: lead {Lead} ({Pk}) → Replied", lead.Id, req.ProductKey);
        return Ok(new { matched = true, leadId = lead.Id });
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

public record HubOutboundAck(
    Guid Id,
    string? Status,           // "sent" | "failed"
    string? ProviderMessageId,
    string? Error);

public record HubInboundRequest(
    string ProductKey,
    string? Phone,
    string? Text,
    string? ProviderMessageId,
    long? TimestampUnix);
