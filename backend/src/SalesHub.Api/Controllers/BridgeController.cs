using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SalesHub.Core.Domain.Entities;
using SalesHub.Core.Domain.Enums;
using SalesHub.Infrastructure.Persistence;

namespace SalesHub.Api.Controllers;

/// <summary>
/// Endpoints para la app Android Bridge. La app consulta mensajes pendientes
/// y confirma entregas. Autenticación vía header X-Bridge-Key.
/// </summary>
[ApiController]
[Route("api/bridge")]
public class BridgeController : ControllerBase
{
    private readonly ApplicationDbContext _db;
    private readonly IConfiguration _cfg;

    public BridgeController(ApplicationDbContext db, IConfiguration cfg)
    {
        _db = db;
        _cfg = cfg;
    }

    /// <summary>
    /// Devuelve el próximo mensaje de texto pendiente de envío (WhatsApp, Scheduled).
    /// Solo devuelve si hay un seller con SendingEnabled y WhatsApp conectado.
    /// </summary>
    [HttpGet("pending")]
    public async Task<ActionResult<BridgePendingResponse>> GetPending([FromQuery] Guid? deviceId = null)
    {
        if (!IsAuthorized()) return Unauthorized();

        var now = DateTimeOffset.UtcNow;

        // Ruteo por device: cada celu envía SOLO la cola de su seller asignado.
        // Fail-closed: una app vieja sin deviceId no recibe nada — evita que un
        // celu mande mensajes de otro seller por la línea equivocada.
        if (deviceId is null)
            return Ok(new BridgePendingResponse { Pending = false, Message = "App desactualizada: falta deviceId" });

        var device = await _db.Devices.FindAsync(deviceId.Value);
        if (device?.SellerId is null)
            return Ok(new BridgePendingResponse { Pending = false, Message = "Device sin seller asignado" });

        var sellerOk = await _db.Sellers
            .AnyAsync(s => s.Id == device.SellerId && s.IsActive && s.SendingEnabled);
        if (!sellerOk)
            return Ok(new BridgePendingResponse { Pending = false, Message = "Seller inactivo o con envíos deshabilitados" });

        var sellerId = device.SellerId.Value;

        // Pacing anti-ban POR LÍNEA (política post-ban 2026-07-30): techo diario
        // + gap mínimo entre envíos del mismo seller.
        var dailyCap = _cfg.GetValue<int?>("Bridge:DailyCap") ?? 15;
        var minGapMinutes = _cfg.GetValue<int?>("Bridge:MinGapMinutes") ?? 10;
        var todayStart = new DateTimeOffset(now.Year, now.Month, now.Day, 0, 0, 0, TimeSpan.Zero);

        var sentToday = await _db.Outbox
            .CountAsync(o => o.Status == OutboxStatus.Sent
                          && o.SentAt >= todayStart
                          && o.Channel == MessageChannel.WhatsApp
                          && o.SellerId == sellerId
                          && o.StepIndex != null
                          && o.Lead.Source == LeadSource.MetaLeadAd);
        if (sentToday >= dailyCap)
            return Ok(new BridgePendingResponse { Pending = false, Message = $"Daily cap reached ({sentToday}/{dailyCap})" });

        var lastSentAt = await _db.Outbox
            .Where(o => o.Status == OutboxStatus.Sent
                     && o.SentAt != null
                     && o.Channel == MessageChannel.WhatsApp
                     && o.SellerId == sellerId
                     && o.StepIndex != null
                     && o.Lead.Source == LeadSource.MetaLeadAd)
            .MaxAsync(o => o.SentAt);
        if (lastSentAt != null && (now - lastSentAt.Value).TotalMinutes < minGapMinutes)
            return Ok(new BridgePendingResponse { Pending = false, Message = "Waiting min gap" });

        // Próximo mensaje Scheduled del seller del device, prioridad más alta
        // primero. Solo mensajes cuyo horario ya llegó.
        var next = await _db.Outbox
            .Include(o => o.Lead)
            .Where(o => o.Status == OutboxStatus.Scheduled
                     && o.ScheduledAt <= now
                     && o.Channel == MessageChannel.WhatsApp
                     && o.SellerId == sellerId
                     && o.Lead.Source == LeadSource.MetaLeadAd  // solo Meta Lead Ads
                     && o.StepIndex != null       // solo mensajes de cadencia (no chat)
                     && o.MediaAssetId == null    // solo texto (MVP)
                     && !string.IsNullOrWhiteSpace(o.Message))
            .OrderByDescending(o => o.Priority)
            .ThenBy(o => o.ScheduledAt)
            .FirstOrDefaultAsync();

        if (next is null)
            return Ok(new BridgePendingResponse { Pending = false, Message = "Queue empty" });

        // Lockear para que el OutboxSender no lo tome
        next.Status = OutboxStatus.Sending;
        next.LockedAt = now;
        next.Attempts++;
        await _db.SaveChangesAsync();

        return Ok(new BridgePendingResponse
        {
            Pending = true,
            OutboxId = next.Id,
            Phone = next.WhatsappPhone,
            Text = next.Message
        });
    }

    /// <summary>
    /// La app Android confirma que el mensaje fue entregado.
    /// </summary>
    [HttpPost("{id:guid}/delivered")]
    public async Task<ActionResult> MarkDelivered(Guid id)
    {
        if (!IsAuthorized()) return Unauthorized();

        var item = await _db.Outbox.FindAsync(id);
        if (item is null) return NotFound();

        item.Status = OutboxStatus.Sent;
        item.SentAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync();

        return Ok(new { ok = true });
    }

    /// <summary>
    /// La app Android reporta que el envío falló. Vuelve a Scheduled para reintento
    /// (hasta 3 attempts, después Failed).
    /// </summary>
    [HttpPost("{id:guid}/failed")]
    public async Task<ActionResult> MarkFailed(Guid id, [FromBody] BridgeFailBody? body)
    {
        if (!IsAuthorized()) return Unauthorized();

        var item = await _db.Outbox.FindAsync(id);
        if (item is null) return NotFound();

        if (item.Attempts >= 3)
            item.Status = OutboxStatus.Failed;
        else
            item.Status = OutboxStatus.Scheduled;

        item.Error = body?.Error;
        item.LockedAt = null;
        await _db.SaveChangesAsync();

        return Ok(new { ok = true });
    }

    /// <summary>
    /// El relay reporta un mensaje entrante de WhatsApp (lead respondió).
    /// </summary>
    [HttpPost("incoming")]
    public async Task<ActionResult> ReportIncoming([FromBody] BridgeIncomingBody body)
    {
        if (!IsAuthorized()) return Unauthorized();
        if (string.IsNullOrWhiteSpace(body.Phone) || string.IsNullOrWhiteSpace(body.Text))
            return BadRequest("phone and text required");

        var lead = await _db.Leads
            .FirstOrDefaultAsync(l => l.WhatsappPhone == body.Phone);

        if (lead is null)
            return Ok(new { ok = true, matched = false });

        // Registrar mensaje entrante
        var msg = new ConversationMessage
        {
            Id = Guid.NewGuid(),
            LeadId = lead.Id,
            Direction = MessageDirection.Inbound,
            Status = MessageDeliveryStatus.Received,
            Text = body.Text,
            Timestamp = DateTimeOffset.UtcNow,
            IsRead = false
        };
        _db.ConversationMessages.Add(msg);

        // Actualizar estado del lead
        if (lead.Status == LeadStatus.Sent)
            lead.Status = LeadStatus.Replied;

        await _db.SaveChangesAsync();

        return Ok(new { ok = true, matched = true, leadId = lead.Id });
    }

    /// <summary>
    /// Estadísticas de la cola para el dashboard del dispositivo.
    /// </summary>
    [HttpGet("stats")]
    public async Task<ActionResult<BridgeStatsResponse>> GetStats()
    {
        var now = DateTimeOffset.UtcNow;
        var todayStart = new DateTimeOffset(now.Year, now.Month, now.Day, 0, 0, 0, TimeSpan.Zero);

        var queueSize = await _db.Outbox
            .CountAsync(o => o.Status == OutboxStatus.Scheduled
                          && o.Channel == MessageChannel.WhatsApp
                          && o.StepIndex != null
                          && o.Lead.Source == LeadSource.MetaLeadAd);

        var sentToday = await _db.Outbox
            .CountAsync(o => o.Status == OutboxStatus.Sent
                          && o.SentAt >= todayStart
                          && o.Channel == MessageChannel.WhatsApp
                          && o.Lead.Source == LeadSource.MetaLeadAd);

        return Ok(new BridgeStatsResponse
        {
            QueueSize = queueSize,
            SentToday = sentToday
        });
    }

    private bool IsAuthorized()
    {
        var expected = _cfg.GetValue<string>("Bridge:ApiKey");
        if (string.IsNullOrWhiteSpace(expected)) return true; // dev only

        var provided = Request.Headers["X-Bridge-Key"].FirstOrDefault();
        return string.Equals(expected, provided, StringComparison.Ordinal);
    }
}

public class BridgePendingResponse
{
    public bool Pending { get; set; }
    public Guid? OutboxId { get; set; }
    public string? Phone { get; set; }
    public string? Text { get; set; }
    public string? Message { get; set; }
}

public class BridgeStatsResponse
{
    public int QueueSize { get; set; }
    public int SentToday { get; set; }
}

public class BridgeIncomingBody
{
    public string? Phone { get; set; }
    public string? Text { get; set; }
}

public class BridgeFailBody
{
    public string? Error { get; set; }
}
