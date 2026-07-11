using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SalesHub.Core.Abstractions;
using SalesHub.Core.Domain.Entities;
using SalesHub.Core.Domain.Enums;
using SalesHub.Infrastructure.Persistence;

namespace SalesHub.Infrastructure.Services;

/// <summary>
/// Picks at most one scheduled outbox item per seller per tick, enforces humanization,
/// sends via Evolution, updates Lead + SellerDailyStats.
/// </summary>
public class OutboxSender
{
    /// <summary>
    /// Techo absoluto de mensajes enviados por vendedor por día, sin importar
    /// cuántas verticales/productos tenga asignados. El cap de CONTACTOS limita
    /// prospectos nuevos (la señal anti-ban principal), pero el volumen total
    /// (contactos × pasos de cadencia) tampoco debe pasar este límite conservador
    /// — WhatsApp marca números que superan ~150 msg/día.
    /// </summary>
    public const int MaxMessagesPerSellerPerDay = 150;

    private readonly ApplicationDbContext _db;
    private readonly IEvolutionClient _evo;
    private readonly ISendScheduler _scheduler;
    private readonly IMessageRenderer _renderer;
    private readonly Microsoft.Extensions.Configuration.IConfiguration _config;
    private readonly ILogger<OutboxSender> _log;

    public OutboxSender(
        ApplicationDbContext db,
        IEvolutionClient evo,
        ISendScheduler scheduler,
        IMessageRenderer renderer,
        Microsoft.Extensions.Configuration.IConfiguration config,
        ILogger<OutboxSender> log)
    {
        _db = db; _evo = evo; _scheduler = scheduler; _renderer = renderer; _config = config; _log = log;
    }

    public async Task<int> TickAsync(CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;

        // Recuperar filas zombie: si el proceso se reinició (deploy/OOM) mientras un
        // mensaje estaba en Sending, quedó trabado para siempre — el query de
        // candidatos sólo toma Scheduled. Reclamamos los Sending viejos: vuelven a
        // Scheduled (o Failed si ya agotaron intentos).
        var staleCutoff = now.AddMinutes(-10);
        var stale = await _db.Outbox
            .Where(o => o.Status == OutboxStatus.Sending && o.Channel == MessageChannel.WhatsApp
                     && o.LockedAt != null && o.LockedAt < staleCutoff)
            .ToListAsync(ct);
        if (stale.Count > 0)
        {
            foreach (var s in stale)
            {
                s.Status = s.Attempts >= 3 ? OutboxStatus.Failed : OutboxStatus.Scheduled;
                s.LockedAt = null;
            }
            await _db.SaveChangesAsync(ct);
            _log.LogWarning("Reclaimed {N} stale Sending outbox rows", stale.Count);
        }

        // Cualquier usuario con SendingEnabled + WhatsApp conectado envía, sea Seller o Admin.
        // El Admin que conecta su WhatsApp y prende el switch debe poder mandar como un vendedor más.
        var sellers = await _db.Sellers
            .Include(s => s.EvolutionInstance)
            .Where(s => s.IsActive && s.SendingEnabled)
            .ToListAsync(ct);

        var sent = 0;
        foreach (var seller in sellers)
        {
            if (seller.EvolutionInstance is null || seller.EvolutionInstance.Status != InstanceStatus.Connected) continue;

            var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, SafeTz(seller.Timezone)).DateTime);
            if (_scheduler.IsSkipDay(seller, today)) continue;

            var cap = _scheduler.ComputeTodayCap(seller, today);
            // El cap es de CONTACTOS NUEVOS por día (lógica anti-ban), no de mensajes:
            // contamos leads distintos contactados hoy, así los pasos 2..N de la
            // cadencia (follow-ups al mismo número) no consumen cupo.
            var dayStart = today.ToDateTime(TimeOnly.MinValue);
            var dayEnd = today.AddDays(1).ToDateTime(TimeOnly.MinValue);
            var contactedToday = await _db.Outbox
                .Where(o => o.SellerId == seller.Id
                         && o.Channel == MessageChannel.WhatsApp
                         && o.Status == OutboxStatus.Sent
                         && o.SentAt != null
                         && o.SentAt.Value >= dayStart
                         && o.SentAt.Value < dayEnd)
                .Select(o => o.LeadId)
                .Distinct()
                .CountAsync(ct);
            // Cap alcanzado = no abrir contactos NUEVOS. Pero los follow-ups a leads ya
            // contactados siguen (no suman contactos): cortarlos dejaba leads con solo el
            // primer mensaje hasta el día siguiente. El techo de MENSAJES de abajo sigue
            // siendo el límite duro del día.
            var capReached = contactedToday >= cap;

            // Tope absoluto de volumen: aunque queden contactos nuevos por hacer,
            // el total de mensajes del día (incluyendo follow-ups de la cadencia)
            // no debe pasar el techo anti-ban.
            var messagesToday = await _db.Outbox
                .CountAsync(o => o.SellerId == seller.Id
                              && o.Channel == MessageChannel.WhatsApp
                              && o.Status == OutboxStatus.Sent
                              && o.SentAt != null
                              && o.SentAt.Value >= dayStart
                              && o.SentAt.Value < dayEnd, ct);
            if (messagesToday >= MaxMessagesPerSellerPerDay) continue;

            // Enforce active hours window.
            var local = TimeZoneInfo.ConvertTime(now, SafeTz(seller.Timezone)).DateTime;
            if (local.Hour < seller.ActiveHoursStart || local.Hour >= seller.ActiveHoursEnd) continue;

            // Gap mínimo por LÍNEA con jitter: el tick corre cada ~30s, y sin esto un backlog
            // vencido se drena en ráfaga sostenida de 2/min (patrón de bot, riesgo de ban).
            // Outbox:LineGapSeconds (default 90) ≈ techo de ~40 envíos/hora por línea; 0 = off.
            var gapSeconds = Microsoft.Extensions.Configuration.ConfigurationBinder.GetValue(_config, "Outbox:LineGapSeconds", 90);
            if (gapSeconds > 0)
            {
                var jittered = TimeSpan.FromSeconds(Random.Shared.Next(gapSeconds * 2 / 3, gapSeconds * 4 / 3 + 1));
                var lastLineSend = await _db.Outbox
                    .Where(o => o.EvolutionInstance == seller.EvolutionInstance!.InstanceName
                             && o.Status == OutboxStatus.Sent && o.SentAt != null)
                    .MaxAsync(o => (DateTimeOffset?)o.SentAt, ct);
                if (lastLineSend is not null && now - lastLineSend < jittered) continue;
            }

            // Trae los próximos N candidatos y filtra por ventana de envío del PRODUCTO
            // del lead (además de la ventana del seller que ya chequeamos arriba). Si un
            // producto está fuera de su ventana, lo saltea y prueba el siguiente.
            //
            // Orden: PRIORIZAR leads "en progreso" (con algún step ya Sent) sobre leads
            // frescos. Sin esto, con muchos leads encolados a la vez todos los step 0
            // (con ScheduledAt anterior por construcción) se mandan antes que cualquier
            // step 1 — la cadencia queda rota: cada lead recibe sólo el primer mensaje
            // hasta que todos los demás leads también recibieron el suyo. Con esta
            // prioridad, una vez que un lead arrancó, el sender termina su cadencia
            // antes de pasar al siguiente.
            //
            // Whitelist: si el seller tiene VerticalsWhitelist explícita, sólo aceptamos
            // rows cuyo producto esté en ella. Los rows fuera de whitelist NO se cancelan
            // — quedan Scheduled esperando a que (a) la whitelist se amplíe, o (b) el
            // lead se reasigne a un seller que sí tenga ese producto. Whitelist vacía/null
            // = sin restricción (misma convención que LeadAssigner).
            var hasWhitelist = seller.VerticalsWhitelist is { Count: > 0 };
            var whitelist = hasWhitelist ? seller.VerticalsWhitelist : null;
            var candidates = await (
                from o in _db.Outbox
                where o.SellerId == seller.Id
                   && o.Status == OutboxStatus.Scheduled
                   && o.Channel == MessageChannel.WhatsApp
                   && o.ScheduledAt <= now
                   && (!hasWhitelist
                       || (o.Lead != null && whitelist!.Contains(o.Lead.ProductKey)))
                   // Con el cap de contactos nuevos agotado, sólo son elegibles los leads
                   // que YA recibieron algo (follow-up, no contacto nuevo).
                   && (!capReached || _db.Outbox.Any(x => x.LeadId == o.LeadId && x.Status == OutboxStatus.Sent))
                   // Transporte gestionado por la app: estas filas las sirve GET /hub/outbound
                   // (las manda la app por su propia Evolution); sales-hub NO las manda por Evolution.
                   && !(o.Lead != null && _db.Products.Any(p => p.ProductKey == o.Lead.ProductKey && p.AppManagedTransport))
                let leadInProgress = _db.Outbox.Any(x =>
                    x.LeadId == o.LeadId && x.Status == OutboxStatus.Sent)
                // Prioridad primero (re-enganche caliente salta la fila), después "en progreso", después FIFO.
                orderby o.Priority descending, leadInProgress descending, o.ScheduledAt
                select o
            ).Take(20).ToListAsync(ct);
            MessageOutbox? next = null;
            foreach (var cand in candidates)
            {
                var lead = await _db.Leads.AsNoTracking()
                    .Include(l => l.Product)
                    .FirstOrDefaultAsync(l => l.Id == cand.LeadId, ct);
                var product = lead?.Product;
                if (product is null) { next = cand; break; }
                // Start>=End → sin restricción a nivel producto.
                if (product.SendHourStart >= product.SendHourEnd) { next = cand; break; }
                if (local.Hour >= product.SendHourStart && local.Hour < product.SendHourEnd) { next = cand; break; }
                // Fuera de ventana del producto: probamos el siguiente candidato del mismo seller.
            }
            if (next is null) continue;

            next.Status = OutboxStatus.Sending;
            next.LockedAt = now;
            next.Attempts++;
            await _db.SaveChangesAsync(ct);

            // Una vez que invocamos un envío real contra Evolution, un fallo es
            // ambiguo (pudo haber entregado igual). Reintentar duplicaría → si
            // sendAttempted, vamos a Failed sin reintento.
            var sendAttempted = false;
            ConversationMessage? convMsg = null;
            try
            {
                // ─── Re-render at send time ──────────────────────────────────
                // Para filas nuevas (StepIndex != null), no confiamos en el snapshot Message/
                // MediaAssetId persistido en el enqueue. Re-resolvemos desde la cadencia ACTUAL
                // del producto (incluye overrides por categoría) y renderizamos los placeholders
                // fresh. Si el admin editó la cadencia, sale lo nuevo. Si la cadencia se acortó
                // y este step ya no existe, cancelamos la fila.
                //
                // Filas legacy (StepIndex == null, encoladas antes de este cambio) caen al
                // path antiguo y se mandan con el snapshot tal cual.
                if (next.StepIndex is not null)
                {
                    var leadCtx = await _db.Leads.AsNoTracking()
                        .Include(l => l.Product)
                        .FirstOrDefaultAsync(l => l.Id == next.LeadId, ct);
                    if (leadCtx?.Product is null)
                    {
                        next.Status = OutboxStatus.Cancelled;
                        next.Error = "Producto o lead inexistente";
                        await _db.SaveChangesAsync(ct);
                        continue;
                    }
                    var (steps, cadenceCategory) = OutboxEnqueueHelper.ResolveStepsForLead(leadCtx, leadCtx.Product);
                    if (next.StepIndex.Value >= steps.Count)
                    {
                        next.Status = OutboxStatus.Cancelled;
                        next.Error = "Step removido de la cadencia";
                        await _db.SaveChangesAsync(ct);
                        continue;
                    }
                    var step = steps[next.StepIndex.Value];
                    var stepHasMedia = step.MediaAssetId is not null || (step.MediaAssetIds is { Count: > 0 });
                    if (string.IsNullOrWhiteSpace(step.Text) && !stepHasMedia)
                    {
                        next.Status = OutboxStatus.Cancelled;
                        next.Error = "Step vacío en la cadencia actual";
                        await _db.SaveChangesAsync(ct);
                        continue;
                    }

                    next.Message = string.IsNullOrWhiteSpace(step.Text)
                        ? string.Empty
                        : _renderer.RenderTemplate(step.Text, leadCtx, leadCtx.Product, seller);

                    Guid? mediaAssetId = step.MediaAssetId;
                    if (step.MediaAssetIds is { Count: > 0 })
                    {
                        if (step.MediaAssetIds.Count == 1)
                        {
                            mediaAssetId = step.MediaAssetIds[0];
                        }
                        else
                        {
                            var idx = OutboxEnqueueHelper.NextRotationIndex(
                                _db, leadCtx.Product.Id, cadenceCategory,
                                next.StepIndex.Value, step.MediaAssetIds.Count);
                            mediaAssetId = step.MediaAssetIds[idx];
                        }
                    }
                    next.MediaAssetId = mediaAssetId;
                    next.CadenceCategory = cadenceCategory;
                    // Persistimos el snapshot actualizado AHORA, antes de mandar — si crashea
                    // a mitad del send, queda Sending con el contenido correcto para reintento.
                    await _db.SaveChangesAsync(ct);
                }

                if (seller.ReadIncomingFirst)
                {
                    await _evo.MarkAllChatsReadAsync(seller.EvolutionInstance.InstanceName, ct);
                }

                var jid = $"{next.WhatsappPhone}@s.whatsapp.net";
                MediaAsset? asset = null;
                var isAudio = false;
                if (next.MediaAssetId is not null)
                {
                    asset = await _db.MediaAssets.AsNoTracking()
                        .FirstOrDefaultAsync(m => m.Id == next.MediaAssetId, ct);
                    if (asset is null)
                        throw new InvalidOperationException($"MediaAsset {next.MediaAssetId} no existe");
                    isAudio = asset.MimeType.StartsWith("audio/", StringComparison.OrdinalIgnoreCase);
                }

                // Registrar el outbound ANTES de mandar: el eco fromMe del webhook llega
                // en ~1s y HandleOwnMessageAsync lo matchea contra estos registros — sin
                // commit previo lo toma como mensaje manual y mutea el bot para ese lead.
                convMsg = new ConversationMessage
                {
                    Id = Guid.NewGuid(),
                    LeadId = next.LeadId,
                    SellerId = next.SellerId,
                    Direction = MessageDirection.Outbound,
                    Status = MessageDeliveryStatus.Sent,
                    Text = next.Message,
                    EvolutionInstance = next.EvolutionInstance,
                    Timestamp = DateTimeOffset.UtcNow,
                    IsRead = true
                };
                _db.ConversationMessages.Add(convMsg);
                await _db.SaveChangesAsync(ct);

                // Para texto/imagen/PDF: pre-typing humanizado como "escribiendo…".
                // Para audio: skip — el "grabando audio…" lo mostramos justo
                // antes de enviar, con la duración exacta del audio.
                if (!isAudio)
                {
                    var typing = Random.Shared.Next(seller.PreSendTypingMinSeconds, seller.PreSendTypingMaxSeconds + 1);
                    await _evo.SetPresenceTypingAsync(seller.EvolutionInstance.InstanceName, jid, typing, ct);
                    await Task.Delay(TimeSpan.FromSeconds(typing), ct);
                }

                bool ok;
                if (asset is not null)
                {
                    if (isAudio)
                    {
                        // Audio → se manda como nota de voz (PTT), no como adjunto. WhatsApp
                        // no soporta caption en notas de voz; si el step trae texto, lo
                        // mandamos como mensaje separado ANTES del audio (orden natural:
                        // primero contexto, después la nota de voz).
                        if (!string.IsNullOrWhiteSpace(next.Message))
                        {
                            sendAttempted = true;
                            var pre = await _evo.SendTextAsync(seller.EvolutionInstance.InstanceName, next.WhatsappPhone, next.Message, ct);
                            if (!pre) throw new InvalidOperationException("Evolution rechazó el texto previo al audio");
                        }
                        // Convertimos a OGG/Opus + duración real, mostramos
                        // "grabando audio…" por exactamente esa duración, y recién
                        // después enviamos.
                        var prep = await _evo.PrepareVoiceNoteAsync(asset.Content, ct);
                        var dur = Math.Max(1, prep.DurationSeconds);
                        var rem = dur;
                        while (rem > 0)
                        {
                            var chunk = Math.Min(25, rem);
                            await _evo.SetPresenceRecordingAsync(seller.EvolutionInstance.InstanceName, jid, chunk, ct);
                            await Task.Delay(chunk * 1000, ct);
                            rem -= chunk;
                        }
                        sendAttempted = true;
                        ok = await _evo.SendPreparedVoiceNoteAsync(
                            seller.EvolutionInstance.InstanceName, next.WhatsappPhone,
                            prep.OggBytes, ct);
                    }
                    else
                    {
                        var caption = string.IsNullOrWhiteSpace(next.Message) ? null : next.Message;
                        sendAttempted = true;
                        ok = await _evo.SendMediaAsync(
                            seller.EvolutionInstance.InstanceName, next.WhatsappPhone,
                            asset.Content, asset.MimeType, asset.FileName, caption, ct);
                    }
                }
                else
                {
                    sendAttempted = true;
                    ok = await _evo.SendTextAsync(seller.EvolutionInstance.InstanceName, next.WhatsappPhone, next.Message, ct);
                }
                if (!ok) throw new InvalidOperationException("Evolution rejected the send");

                next.Status = OutboxStatus.Sent;
                next.SentAt = DateTimeOffset.UtcNow;

                var lead = await _db.Leads.FirstOrDefaultAsync(l => l.Id == next.LeadId, ct);
                if (lead is not null)
                {
                    lead.Status = LeadStatus.Sent;
                    lead.SentAt = DateTimeOffset.UtcNow;
                }

                var statsKey = new { SellerId = seller.Id, Date = today };
                var stats = await _db.DailyStats.FindAsync(new object[] { statsKey.SellerId, statsKey.Date }, ct);
                if (stats is null)
                {
                    stats = new SellerDailyStats { SellerId = seller.Id, Date = today, PlannedCap = cap };
                    _db.DailyStats.Add(stats);
                }
                stats.MessagesSent++;
                await _db.SaveChangesAsync(ct);
                sent++;

                _log.LogInformation("Sent to {Phone} from seller {Seller} ({Instance})", next.WhatsappPhone, seller.DisplayName, seller.EvolutionInstance.InstanceName);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Send failed for outbox {Id}", next.Id);
                next.Error = ex.Message;
                next.LockedAt = null;
                // El registro pre-insertado queda como Failed (si se reintenta, el
                // reintento crea el suyo propio).
                if (convMsg is not null) convMsg.Status = MessageDeliveryStatus.Failed;
                if (sendAttempted)
                {
                    // Ya invocamos el envío real contra Evolution: no sabemos si
                    // entregó. Reintentar duplicaría el mensaje → Failed sin reintento.
                    next.Status = OutboxStatus.Failed;
                }
                else
                {
                    // Falló en un paso previo al envío (presencia, prep de audio):
                    // todavía no se mandó nada, es seguro reintentar.
                    next.Status = next.Attempts >= 3 ? OutboxStatus.Failed : OutboxStatus.Scheduled;
                    if (next.Status == OutboxStatus.Scheduled)
                        next.ScheduledAt = DateTimeOffset.UtcNow.AddMinutes(Random.Shared.Next(5, 16));
                }

                // Si quedó Failed permanente y es parte de una cadencia, cancelamos
                // los steps siguientes del mismo lead: sin el step previo (ej. texto
                // intro) los que vienen después saldrían sin contexto — el lead
                // recibiría "[audio]" sin el mensaje que lo introducía.
                if (next.Status == OutboxStatus.Failed && next.StepIndex is not null)
                {
                    var laterSteps = await _db.Outbox
                        .Where(o => o.LeadId == next.LeadId
                                 && o.Status == OutboxStatus.Scheduled
                                 && o.StepIndex != null
                                 && o.StepIndex > next.StepIndex.Value)
                        .ToListAsync(ct);
                    foreach (var ls in laterSteps)
                    {
                        ls.Status = OutboxStatus.Cancelled;
                        ls.Error = $"Cancelled: step {next.StepIndex.Value} failed";
                    }
                }
                await _db.SaveChangesAsync(ct);
            }
        }
        return sent;
    }

    private static TimeZoneInfo SafeTz(string id)
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
        catch { return TimeZoneInfo.Utc; }
    }
}
