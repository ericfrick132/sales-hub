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
    /// <summary>Motivos que reporta el APK cuando el número no puede recibir WhatsApp.</summary>
    public const string NoWhatsAppError = "no_whatsapp";
    public const string InvalidNumberError = "invalid_number";
    /// <summary>Una mano humana alteró el texto mientras el celu tipeaba: NO se envió.</summary>
    public const string InputAlteredError = "input_alterado";
    /// <summary>El APK detectó input humano real (getevent) y frenó sin enviar.</summary>
    public const string HumanOnPhoneError = "humano_en_el_celu";
    /// <summary>Se apretó enviar pero no se pudo confirmar: reintentar duplicaría.</summary>
    public const string SendNotConfirmedError = "send_not_confirmed";

    private static readonly System.Text.RegularExpressions.Regex NotAMessage = new(
        @"^(\d+ (mensajes|messages)( nuevos| new)?|mensaje|message|escribiendo\.\.\.|en l[ií]nea|online|\d{1,2}:\d{2}( ?[ap]\.? ?m\.?)?|hoy|ayer|today|yesterday)$",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

    private readonly ApplicationDbContext _db;
    private readonly IConfiguration _cfg;
    private readonly SalesHub.Infrastructure.Services.BridgeDirectSendService _testSends;
    private readonly SalesHub.Infrastructure.Services.GroqWhisperClient _whisper;
    private readonly SalesHub.Infrastructure.Services.ConversationService _conversations;
    private readonly ILogger<BridgeController> _log;

    public BridgeController(ApplicationDbContext db, IConfiguration cfg,
        SalesHub.Infrastructure.Services.BridgeDirectSendService testSends,
        SalesHub.Infrastructure.Services.GroqWhisperClient whisper,
        SalesHub.Infrastructure.Services.ConversationService conversations,
        ILogger<BridgeController> log)
    {
        _db = db;
        _cfg = cfg;
        _testSends = testSends;
        _whisper = whisper;
        _conversations = conversations;
        _log = log;
    }

    /// <summary>
    /// Devuelve el próximo mensaje de texto pendiente de envío (WhatsApp, Scheduled).
    /// Solo devuelve si hay un seller con SendingEnabled y WhatsApp conectado.
    /// </summary>
    [HttpGet("pending")]
    public async Task<ActionResult<BridgePendingResponse>> GetPending([FromQuery] Guid? deviceId = null, [FromQuery] bool peek = false, [FromQuery] string? v = null)
    {
        if (!IsAuthorized()) return Unauthorized();

        var now = DateTimeOffset.UtcNow;

        // Ruteo por device: cada celu envía SOLO la cola de su seller asignado.
        // Fail-closed: una app vieja sin deviceId no recibe nada — evita que un
        // celu mande mensajes de otro seller por la línea equivocada.
        if (deviceId is null)
            return Ok(new BridgePendingResponse { Pending = false, Message = "App desactualizada: falta deviceId" });

        var device = await _db.Devices.FindAsync(deviceId.Value);
        if (device is null)
            return Ok(new BridgePendingResponse { Pending = false, Message = "Device desconocido" });

        // El celu reporta su versión en cada poll: sin esto no hay forma de saber qué corre
        // cada teléfono (pasó dos veces tener que adivinar si un fix ya había llegado).
        if (!string.IsNullOrWhiteSpace(v) && device.AppVersion != v)
        {
            device.AppVersion = v;
            await _db.SaveChangesAsync();
        }

        // Pedido manual de "levantar chats": viaja en la misma respuesta del poll.
        var sweep = _testSends.ConsumeSweep(device.Id);

        // "Enviar YA" de prueba: se sirve ANTES que la cola real y saltea todos los
        // gates (seller, caps, gap, dup) — es un humano probando el fierro.
        var test = peek ? null : _testSends.TryTakePending(device.Id);
        if (test is not null)
        {
            return Ok(new BridgePendingResponse
            {
                Pending = true,
                OutboxId = test.TestId,
                Phone = test.Phone,
                Text = test.Text,
                Fast = test.Fast
            });
        }

        if (device.SellerId is null)
            return Ok(new BridgePendingResponse { Pending = false, Sweep = sweep, Message = "Device sin seller asignado" });

        var sellerOk = await _db.Sellers
            .AnyAsync(s => s.Id == device.SellerId && s.IsActive && s.SendingEnabled);
        if (!sellerOk)
            return Ok(new BridgePendingResponse { Pending = false, Sweep = sweep, Message = "Seller inactivo o con envíos deshabilitados" });

        var sellerId = device.SellerId.Value;

        // Pacing anti-ban POR LÍNEA (política post-ban 2026-07-30). El riesgo de ban está
        // en ABRIR conversaciones nuevas, no en terminar una empezada: el techo diario
        // cuenta CONVERSACIONES (leads distintos tocados hoy) y el gap largo se le aplica
        // solo al primer mensaje de un lead. Los pasos siguientes de una charla ya abierta
        // salen con un gap corto y no consumen cupo — si no, el lead se queda colgado con
        // el saludo mientras la línea atiende a otros.
        // Números de vendedor con hambre (2026-08-04): la línea es un celu real con la app
        // nativa, no una sesión de Baileys, así que el techo puede parecerse al de una
        // persona que se pasa el día escribiendo. El jitter es lo que lo hace humano: sin
        // variación, un mensaje cada exactamente N minutos es un patrón de bot.
        var dailyCap = _cfg.GetValue<int?>("Bridge:DailyCap") ?? 40;
        var minGapSeconds = _cfg.GetValue<int?>("Bridge:NewChatGapSeconds")
                            ?? (_cfg.GetValue<int?>("Bridge:MinGapMinutes") * 60) ?? 210;
        var continuationGapSeconds = _cfg.GetValue<int?>("Bridge:ContinuationGapSeconds") ?? 45;
        var todayStart = new DateTimeOffset(now.Year, now.Month, now.Day, 0, 0, 0, TimeSpan.Zero);

        // Qué manda el bridge: cadencia de Meta Lead Ads + "enviar ahora" manual
        // (Priority >= BridgeManualPriority). NUNCA el backlog frío histórico
        // (miles de filas Scheduled priority<=70) — drenarlas solas quema la línea.
        var sentQuery = _db.Outbox
            .Where(o => o.Status == OutboxStatus.Sent
                     && o.Channel == MessageChannel.WhatsApp
                     && o.SellerId == sellerId
                     && o.StepIndex != null
                     && (o.Lead.Source == LeadSource.MetaLeadAd
                         || o.Priority >= MessageOutbox.BridgeManualPriority));

        var leadsToday = await sentQuery
            .Where(o => o.SentAt >= todayStart)
            .Select(o => o.LeadId)
            .Distinct()
            .ToListAsync();
        var capReached = leadsToday.Count >= dailyCap;

        var lastSentAt = await sentQuery.MaxAsync(o => o.SentAt);

        // Próximos mensajes Scheduled del seller del device. Traemos varios para poder
        // saltar (y marcar) los duplicados textuales sin devolver cola vacía, y para
        // poder elegir una continuación por sobre una conversación nueva.
        var pool = await _db.Outbox
            .Include(o => o.Lead)
            .Where(o => o.Status == OutboxStatus.Scheduled
                     && o.ScheduledAt <= now
                     && o.Channel == MessageChannel.WhatsApp
                     && o.SellerId == sellerId
                     && (o.Lead.Source == LeadSource.MetaLeadAd
                         || o.Priority >= MessageOutbox.BridgeManualPriority)
                     && o.StepIndex != null       // solo mensajes de cadencia (no chat)
                     && o.MediaAssetId == null    // solo texto (MVP)
                     && !string.IsNullOrWhiteSpace(o.Message))
            .OrderByDescending(o => o.Priority)
            .ThenBy(o => o.ScheduledAt)
            .Take(30)
            .ToListAsync();

        // Un lead ya contactado alguna vez = la conversación está abierta; lo que queda
        // de su cadencia es continuación.
        var poolLeadIds = pool.Select(o => o.LeadId).Distinct().ToList();
        var alreadyContacted = await sentQuery
            .Where(o => poolLeadIds.Contains(o.LeadId))
            .Select(o => o.LeadId)
            .Distinct()
            .ToListAsync();

        bool IsContinuation(MessageOutbox o) => alreadyContacted.Contains(o.LeadId);

        // Con el cupo del día lleno no se abren conversaciones nuevas, pero las que ya
        // están abiertas se terminan igual.
        var allowed = capReached ? pool.Where(IsContinuation).ToList() : pool;
        if (allowed.Count == 0)
        {
            return Ok(new BridgePendingResponse
            {
                Pending = false,
                Sweep = sweep,
                Message = capReached
                    ? $"Daily cap reached ({leadsToday.Count}/{dailyCap})"
                    : "Queue empty"
            });
        }

        // Continuaciones primero: terminar la charla empezada vale más que abrir otra.
        var candidates = allowed
            .OrderByDescending(IsContinuation)
            .ThenByDescending(o => o.Priority)
            .ThenBy(o => o.ScheduledAt)
            .Take(5)
            .ToList();

        // El gap depende de qué vamos a mandar: corto si es continuación, largo si abre
        // una conversación nueva.
        if (lastSentAt != null)
        {
            var elapsed = now - lastSentAt.Value;
            var nextIsContinuation = IsContinuation(candidates[0]);
            // ±35% de variación sobre el gap: se evalúa en cada poll, así el momento real
            // del envío cae en cualquier punto de la ventana en vez de en el minuto exacto.
            var baseGap = nextIsContinuation ? continuationGapSeconds : minGapSeconds;
            var jittered = baseGap * (0.65 + Random.Shared.NextDouble() * 0.7);
            var required = TimeSpan.FromSeconds(jittered);
            if (elapsed < required)
                return Ok(new BridgePendingResponse { Pending = false, Sweep = sweep, Message = "Waiting min gap" });
        }

        // Anti-duplicado textual: si el mismo texto ya le salió a este lead hace poco
        // (por Evolution, por el bridge o por una fila zombie), no lo repetimos —
        // mismo criterio que OutboxSender.
        MessageOutbox? next = null;
        var dupWindow = DateTimeOffset.UtcNow.AddDays(-7);
        foreach (var cand in candidates)
        {
            var dup = await _db.ConversationMessages.AnyAsync(m =>
                m.LeadId == cand.LeadId
                && m.Direction == MessageDirection.Outbound
                && m.Status != MessageDeliveryStatus.Failed
                && m.Timestamp >= dupWindow
                && m.Text == cand.Message);
            if (dup)
            {
                cand.Status = OutboxStatus.Skipped;
                cand.Error = "Duplicado: mismo texto ya enviado al lead en los últimos 7 días";
                continue;
            }
            next = cand;
            break;
        }

        if (next is null)
        {
            await _db.SaveChangesAsync(); // persistir los Skipped, si hubo
            return Ok(new BridgePendingResponse { Pending = false, Sweep = sweep, Message = "Queue empty" });
        }

        // peek: el celu solo quiere saber si hay algo que salga AHORA (respeta caps y gap)
        // para decidir si puede ponerse a leer chats. No lockeamos ni devolvemos el texto:
        // consumir la fila acá la dejaría tomada y sin enviar.
        if (peek)
        {
            await _db.SaveChangesAsync();   // persistir los Skipped del dup-guard, si hubo
            return Ok(new BridgePendingResponse { Pending = true, Message = "peek" });
        }

        // Lockear para que el OutboxSender no lo tome. El marcador en Error hace que,
        // si el celu muere sin ack, el reclaim NO la re-programe (pudo haber enviado)
        // — va a Failed ambiguo en vez de duplicar.
        next.Status = OutboxStatus.Sending;
        next.LockedAt = now;
        next.Attempts++;
        next.Error = MessageOutbox.BridgePulledError;
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

        // Ack de un "enviar YA" de prueba: vive en memoria, no en el outbox.
        if (_testSends.TryComplete(id, ok: true, error: null))
            return Ok(new { ok = true, test = true });

        var item = await _db.Outbox.FindAsync(id);
        if (item is null) return NotFound();

        item.Status = OutboxStatus.Sent;
        item.SentAt = DateTimeOffset.UtcNow;
        if (item.Error == MessageOutbox.BridgePulledError) item.Error = null;

        // Avanzar el lead como hace OutboxSender tras un envío — sin esto el lead queda
        // en Assigned/Queued para siempre y los sweeps lo ven como "nunca contactado".
        // Solo hacia adelante: no pisar Replied/Interested/Closed.
        var deliveredLead = await _db.Leads.FindAsync(item.LeadId);
        if (deliveredLead is not null && deliveredLead.Status is LeadStatus.New or LeadStatus.Assigned or LeadStatus.Queued)
        {
            deliveredLead.Status = LeadStatus.Sent;
            deliveredLead.SentAt ??= DateTimeOffset.UtcNow;
        }

        // Registrar el outbound en la conversación: el celu manda por SU WhatsApp (sin
        // Evolution) así que no hay eco de webhook que lo persista — sin esto el envío
        // no aparece en /conversaciones y el guard anti-duplicado no lo ve.
        _db.ConversationMessages.Add(new ConversationMessage
        {
            Id = Guid.NewGuid(),
            LeadId = item.LeadId,
            SellerId = item.SellerId,
            Direction = MessageDirection.Outbound,
            Status = MessageDeliveryStatus.Sent,
            Text = item.Message,
            EvolutionInstance = item.EvolutionInstance,
            Timestamp = DateTimeOffset.UtcNow,
            IsRead = true
        });
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

        // Fallo de un "enviar YA" de prueba: vive en memoria, no en el outbox.
        if (_testSends.TryComplete(id, ok: false, error: body?.Error ?? "fallo sin detalle"))
            return Ok(new { ok = true, test = true });

        var item = await _db.Outbox.FindAsync(id);
        if (item is null) return NotFound();

        // El número no tiene WhatsApp: reintentar es imposible por definición. Marcamos
        // el lead (estado terminal) y cancelamos lo que le quedaba encolado, así deja de
        // consumir cupo de la línea y sale de los sweeps de contacto.
        if (body?.Error is NoWhatsAppError or InvalidNumberError)
        {
            var motivo = body.Error == InvalidNumberError
                ? "Número inválido para WhatsApp"
                : "El número no tiene WhatsApp";
            item.Status = OutboxStatus.Skipped;
            item.Error = motivo;
            item.LockedAt = null;

            var lead = await _db.Leads.FindAsync(item.LeadId);
            if (lead is not null) lead.Status = LeadStatus.NoWhatsApp;

            var rest = await _db.Outbox
                .Where(o => o.LeadId == item.LeadId
                         && o.Id != item.Id
                         && (o.Status == OutboxStatus.Scheduled || o.Status == OutboxStatus.Sending))
                .ToListAsync();
            foreach (var r in rest)
            {
                r.Status = OutboxStatus.Cancelled;
                r.Error = motivo;
            }

            await _db.SaveChangesAsync();
            return Ok(new { ok = true, noWhatsApp = true, cancelled = rest.Count });
        }

        // Alguien agarró el teléfono mientras el celu trabajaba: el APK lo detecta en el
        // input real (getevent solo ve la mano humana, no lo que inyecta el bridge) y frena
        // en el acto. NO se envió nada, así que esto no es un fallo: es una tarea cancelada
        // que vuelve a la cola. El borrador queda en WhatsApp y el próximo intento lo retoma.
        if (body?.Error != null && (body.Error.StartsWith(HumanOnPhoneError, StringComparison.Ordinal)
                                    || body.Error.StartsWith(InputAlteredError, StringComparison.Ordinal)))
        {
            item.Status = item.Attempts >= 8 ? OutboxStatus.Failed : OutboxStatus.Scheduled;
            item.ScheduledAt = DateTimeOffset.UtcNow.AddMinutes(Random.Shared.Next(3, 8));
            item.Error = "Cancelado: alguien estaba usando el teléfono. Vuelve a la cola";
            item.LockedAt = null;
            await _db.SaveChangesAsync();
            return Ok(new { ok = true, requeued = true, retryAt = item.ScheduledAt });
        }

        // Ambiguo: tipeamos y apretamos enviar, pero no pudimos confirmar que saliera.
        // Reintentar puede duplicarle el mensaje al lead, así que no reintentamos.
        if (body?.Error == SendNotConfirmedError)
        {
            item.Status = OutboxStatus.Failed;
            item.Error = "No se pudo confirmar el envío (no se reintenta para no duplicar)";
            item.LockedAt = null;
            await _db.SaveChangesAsync();
            return Ok(new { ok = true, ambiguous = true });
        }

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
    /// El celu reporta un mensaje entrante de WhatsApp (leído de la notificación).
    /// Entra por el MISMO motor que usaba el webhook de Evolution, así el lead pasa a
    /// Replied, la charla aparece en /conversaciones y el agente decide si responde.
    /// </summary>
    [HttpPost("incoming")]
    public async Task<ActionResult> ReportIncoming([FromBody] BridgeIncomingBody body, CancellationToken ct)
    {
        if (!IsAuthorized()) return Unauthorized();

        // El celu saca el texto de dumpsys/uiautomator, que lo escriben ESCAPADO: los emojis
        // y saltos de línea llegan como &#128522; / &#10;. Guardarlo así le mete ruido al
        // agente. Se decodifica también acá (no solo en el APK) porque un celu con una
        // versión vieja sigue mandando el texto crudo.
        var text = System.Net.WebUtility.HtmlDecode(body.Text ?? "")
            .Replace("\u200E", "").Replace("\u200F", "")
            .Trim();
        if (text.Length == 0) return BadRequest(new { error = "Falta el texto" });

        // Restos de la UI de WhatsApp que un APK viejo puede reportar como si fueran
        // mensajes del lead: el placeholder del cajón, una hora suelta, el separador de
        // no leídos. No son contenido y ensucian la conversación que lee el agente.
        if (NotAMessage.IsMatch(text))
            return Ok(new { ok = true, matched = false, reason = "no es un mensaje" });

        // El remitente sale del título de la notificación: si el número NO está agendado
        // WhatsApp muestra el número (lo que queremos). Si es un contacto guardado o un
        // grupo, no hay dígitos para matchear y lo ignoramos en vez de inventar un lead.
        var sender = body.Sender ?? body.Phone ?? "";
        var digits = new string(sender.Where(char.IsDigit).ToArray());
        if (digits.Length < 8)
            return Ok(new { ok = true, matched = false, reason = "remitente sin número (contacto agendado o grupo)" });

        var device = await _db.Devices.Include(d => d.Seller).ThenInclude(s => s!.EvolutionInstance)
            .FirstOrDefaultAsync(d => d.Id == body.DeviceId, ct);
        var instanceName = device?.Seller?.EvolutionInstance?.InstanceName;
        if (instanceName is null)
            return Ok(new { ok = true, matched = false, reason = "device sin seller/línea" });

        // El barrido de chats vuelve a leer las mismas burbujas cada vez que corre: si ya
        // tenemos ese texto de ese número, no lo registramos de nuevo (ni despertamos al
        // agente por algo viejo). El webhook traía un id de mensaje para esto; acá no hay.
        var suffix = digits.Length >= 8 ? digits[^8..] : digits;
        var dupWindow = DateTimeOffset.UtcNow.AddDays(-30);
        var already = await _db.ConversationMessages
            .AnyAsync(m => m.Direction == MessageDirection.Inbound
                        && m.Text == text
                        && m.Timestamp >= dupWindow
                        && m.Lead!.WhatsappPhone != null
                        && m.Lead.WhatsappPhone.EndsWith(suffix), ct);
        if (already)
            return Ok(new { ok = true, matched = true, duplicate = true });

        var handled = await _conversations.HandleIncomingAsync(new SalesHub.Infrastructure.Services.ConversationService.IncomingMessage(
            InstanceName: instanceName,
            FromJid: $"{digits}@s.whatsapp.net",
            FromPhone: digits,
            MessageId: null,
            Text: text,
            Timestamp: DateTimeOffset.UtcNow,
            RawJson: "{}"), ct);

        _log.LogInformation("Entrante por bridge: device={Device} de={Phone} manejado={Handled}",
            device!.Name, digits, handled);
        return Ok(new { ok = true, matched = handled });
    }

    /// <summary>
    /// El celu subió una nota de voz que le llegó por WhatsApp: la transcribimos y
    /// encolamos el texto de vuelta al número autorizado (el de /transcripcion). Reemplaza
    /// al relay por Evolution, que ya no se usa.
    /// </summary>
    [HttpPost("transcribe")]
    public async Task<ActionResult> Transcribe([FromBody] BridgeTranscribeBody body, CancellationToken ct)
    {
        if (!IsAuthorized()) return Unauthorized();
        if (string.IsNullOrWhiteSpace(body.AudioBase64))
            return BadRequest(new { error = "Falta audioBase64" });

        var device = await _db.Devices.FindAsync(new object[] { body.DeviceId }, ct);
        if (device is null) return NotFound(new { error = "Device desconocido" });

        // Mismo interruptor que la página /transcripcion.
        var settings = await _db.TranscriptionSettings.AsNoTracking().FirstOrDefaultAsync(ct);
        if (settings is not null && !settings.Enabled)
            return Ok(new { ok = false, error = "Transcripción apagada" });

        if (!_whisper.IsConfigured)
            return Ok(new { ok = false, error = "Groq sin API key" });

        byte[] audio;
        try { audio = Convert.FromBase64String(body.AudioBase64.Trim()); }
        catch (FormatException) { return BadRequest(new { error = "audioBase64 inválido" }); }
        if (audio.Length == 0) return BadRequest(new { error = "Audio vacío" });

        // A quién le contestamos: el número autorizado cargado en /transcripcion. El celu
        // no puede saber quién mandó el audio (WhatsApp no lo expone en el archivo), así
        // que la respuesta siempre va al dueño configurado.
        var owner = await _db.TranscriptionPhones.AsNoTracking()
            .OrderBy(p => p.CreatedAt)
            .Select(p => p.Phone)
            .FirstOrDefaultAsync(ct);
        var ownerDigits = new string((owner ?? "").Where(char.IsDigit).ToArray());
        if (ownerDigits.Length < 8)
            return Ok(new { ok = false, error = "No hay número autorizado cargado en /transcripcion" });

        var transcript = await _whisper.TranscribeAsync(audio, body.FileName ?? "voice.opus", ct);
        var reply = string.IsNullOrWhiteSpace(transcript)
            ? "No pude transcribir ese audio. Probá reenviarlo de nuevo."
            : transcript!;

        _testSends.Queue(device.Id, ownerDigits, reply, SalesHub.Infrastructure.Services.BridgeDirectSendService.KindTranscription);
        _log.LogInformation("Transcripción por bridge: device={Device} bytes={Bytes} ok={Ok} chars={Chars}",
            device.Name, audio.Length, !string.IsNullOrWhiteSpace(transcript), reply.Length);

        return Ok(new { ok = true, transcribed = !string.IsNullOrWhiteSpace(transcript), chars = reply.Length });
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
                          && (o.Lead.Source == LeadSource.MetaLeadAd
                              || o.Priority >= MessageOutbox.BridgeManualPriority));

        var sentToday = await _db.Outbox
            .CountAsync(o => o.Status == OutboxStatus.Sent
                          && o.SentAt >= todayStart
                          && o.Channel == MessageChannel.WhatsApp
                          && (o.Lead.Source == LeadSource.MetaLeadAd
                              || o.Priority >= MessageOutbox.BridgeManualPriority));

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
    /// <summary>Tipear sin ritmo humano (respuestas al propio dueño, no outreach a leads).</summary>
    public bool Fast { get; set; }
    /// <summary>Pedido de recorrer los chats y reportar lo que respondieron los leads.</summary>
    public bool Sweep { get; set; }
}

public class BridgeTranscribeBody
{
    public Guid DeviceId { get; set; }
    public string? FileName { get; set; }
    public string? AudioBase64 { get; set; }
}

public class BridgeStatsResponse
{
    public int QueueSize { get; set; }
    public int SentToday { get; set; }
}

public class BridgeIncomingBody
{
    public Guid DeviceId { get; set; }
    /// <summary>Título de la notificación de WhatsApp: número si no está agendado.</summary>
    public string? Sender { get; set; }
    public string? Phone { get; set; }
    public string? Text { get; set; }
}

public class BridgeFailBody
{
    public string? Error { get; set; }
}
