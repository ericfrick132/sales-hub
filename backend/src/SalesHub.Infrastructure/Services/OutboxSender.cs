using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SalesHub.Core.Abstractions;
using SalesHub.Core.Domain.Entities;
using SalesHub.Core.Domain.Enums;
using SalesHub.Infrastructure.Options;
using SalesHub.Infrastructure.Persistence;
using SalesHub.Infrastructure.Services.Social;

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

    /// <summary>
    /// Piso de LeadSource que marca a un lead como CALIENTE (formulario/app: ya nos
    /// dejó sus datos o nos conoce). Todo lo que está por debajo es tráfico FRÍO
    /// (scrapeado) — el que causa bans. Misma convención que OutboxEnqueueHelper.
    /// </summary>
    public const int HotSourceFloor = 400;

    private readonly ApplicationDbContext _db;
    private readonly IEvolutionClient _evo;
    private readonly ISendScheduler _scheduler;
    private readonly IMessageRenderer _renderer;
    private readonly ElevenLabsClient _tts;
    private readonly VoiceNoteOptions _voiceOpts;
    private readonly Microsoft.Extensions.Configuration.IConfiguration _config;
    private readonly ILogger<OutboxSender> _log;

    public OutboxSender(
        ApplicationDbContext db,
        IEvolutionClient evo,
        ISendScheduler scheduler,
        IMessageRenderer renderer,
        ElevenLabsClient tts,
        IOptions<VoiceNoteOptions> voiceOpts,
        Microsoft.Extensions.Configuration.IConfiguration config,
        ILogger<OutboxSender> log)
    {
        _db = db; _evo = evo; _scheduler = scheduler; _renderer = renderer;
        _tts = tts; _voiceOpts = voiceOpts.Value; _config = config; _log = log;
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
                // Fila pulleada por el bridge Android sin ack: el celu pudo haber ENVIADO y
                // morir antes de confirmar. Reintentar duplicaría (mismo criterio que
                // sendAttempted) → Failed ambiguo, no vuelve a Scheduled.
                if (s.Error == MessageOutbox.BridgePulledError)
                    s.Status = OutboxStatus.Failed;
                else
                    s.Status = s.Attempts >= 3 ? OutboxStatus.Failed : OutboxStatus.Scheduled;
                s.LockedAt = null;
            }
            await _db.SaveChangesAsync(ct);
            _log.LogWarning("Reclaimed {N} stale Sending outbox rows", stale.Count);
        }

        // Qué se puede mandar hoy y a quién (por origen del lead). Se lee por tick: apagar
        // un switch corta los envíos en el tick siguiente. Lo bloqueado queda Scheduled y
        // sale solo cuando se vuelve a prender.
        var policy = await _db.LoadMessagingPolicyAsync(ct);
        var outreachSources = policy.OutreachSources;
        var followupSources = policy.FollowupSources;

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

            var local = TimeZoneInfo.ConvertTime(now, SafeTz(seller.Timezone)).DateTime;
            var today = DateOnly.FromDateTime(local);
            var isSkipDay = _scheduler.IsSkipDay(seller, today);

            // ─── Fast lane para leads calientes ─────────────────────────────
            // Un lead que llenó un formulario (Meta Lead Ads y demás source>=400,
            // Priority>=70 por convención de OutboxEnqueueHelper) espera respuesta
            // YA: contactado <1h cierra 59%, >3 días cierra 2% (data propia). Por
            // eso las filas calientes no esperan skip-day ni el horario del seller
            // (sí un clamp 08-22 local para no escribir de madrugada), no compiten
            // por el cap de contactos nuevos del día, y van antes que cualquier
            // cadencia fría. El techo duro de mensajes/día sigue vigente.
            var fastLanePriority = Microsoft.Extensions.Configuration.ConfigurationBinder.GetValue(_config, "Outbox:FastLanePriority", 70);
            var fastLaneStart = Microsoft.Extensions.Configuration.ConfigurationBinder.GetValue(_config, "Outbox:FastLaneHourStart", 8);
            var fastLaneEnd = Microsoft.Extensions.Configuration.ConfigurationBinder.GetValue(_config, "Outbox:FastLaneHourEnd", 22);
            var insideFastLaneHours = local.Hour >= fastLaneStart && local.Hour < fastLaneEnd;
            var insideSellerHours = local.Hour >= seller.ActiveHoursStart && local.Hour < seller.ActiveHoursEnd;
            // Gate normal cerrado (skip-day u horario) → solo pueden salir filas calientes.
            var hotOnly = isSkipDay || !insideSellerHours;
            if (hotOnly && !insideFastLaneHours) continue;

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

            // ─── Techo de tráfico FRÍO por día (post-ban 2026-07-30) ────────
            // WhatsApp banea por mensajes a gente que no te tiene agendado y NO
            // responde. Este techo cuenta MENSAJES del día a leads fríos
            // (Source < 400) que nunca nos escribieron — cadencia, follow-ups y
            // re-enganche incluidos. No cuenta: leads calientes (formulario/app)
            // ni leads que ya respondieron alguna vez (charla real, riesgo bajo).
            // Alcanzado el techo, lo frío queda Scheduled para otro día; solo
            // siguen los calientes, los que ya hablaron, y las burbujas restantes
            // de conversaciones frías abiertas HOY (no partimos saludo/pitch).
            var coldCap = Microsoft.Extensions.Configuration.ConfigurationBinder.GetValue(_config, "Outbox:ColdDailyMessageCap", 15);
            var coldCapReached = false;
            if (coldCap > 0)
            {
                var coldMessagesToday = await _db.Outbox
                    .CountAsync(o => o.SellerId == seller.Id
                                  && o.Channel == MessageChannel.WhatsApp
                                  && o.Status == OutboxStatus.Sent
                                  && o.SentAt != null
                                  && o.SentAt.Value >= dayStart
                                  && o.SentAt.Value < dayEnd
                                  && o.Lead != null && (int)o.Lead.Source < HotSourceFloor
                                  && !_db.ConversationMessages.Any(m => m.LeadId == o.LeadId
                                        && m.Direction == MessageDirection.Inbound), ct);
                coldCapReached = coldMessagesToday >= coldCap;
            }

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

            // Espaciado entre CONTACTOS NUEVOS (post-ban 2026-07-30): abrir muchas
            // conversaciones nuevas seguidas es la señal de spam más fuerte, aunque cada
            // lead sea caliente (Meta forms en ráfaga). Entre el PRIMER mensaje a un lead
            // y el primer mensaje al siguiente exigimos un gap con jitter — las burbujas
            // y follow-ups de leads ya contactados fluyen al ritmo del line-gap normal.
            // Outbox:NewContactGapSeconds (default 600 ≈ máx 6 aperturas/hora); 0 = off.
            var newContactGapSeconds = Microsoft.Extensions.Configuration.ConfigurationBinder.GetValue(_config, "Outbox:NewContactGapSeconds", 600);
            DateTimeOffset? lastFirstContactAt = null;
            TimeSpan newContactGap = TimeSpan.Zero;
            if (newContactGapSeconds > 0)
            {
                newContactGap = TimeSpan.FromSeconds(Random.Shared.Next(newContactGapSeconds * 2 / 3, newContactGapSeconds * 4 / 3 + 1));
                // Último "primer mensaje de la historia de un lead" enviado por esta línea.
                lastFirstContactAt = await _db.Outbox
                    .Where(o => o.EvolutionInstance == seller.EvolutionInstance!.InstanceName
                             && o.Status == OutboxStatus.Sent && o.SentAt != null
                             && !_db.Outbox.Any(x => x.LeadId == o.LeadId
                                   && x.Status == OutboxStatus.Sent
                                   && x.SentAt != null && x.SentAt < o.SentAt))
                    .MaxAsync(o => o.SentAt, ct);
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
                   // Gate normal cerrado (fuera de horario del seller / skip-day):
                   // solo pasan las filas calientes del fast lane.
                   && (!hotOnly || o.Priority >= fastLanePriority)
                   // Con el cap de contactos nuevos agotado, sólo son elegibles los leads
                   // que YA recibieron algo (follow-up, no contacto nuevo) — salvo los
                   // calientes: un lead de formulario no puede quedar para mañana porque
                   // el scraping frío ya consumió el cupo del día.
                   && (!capReached || o.Priority >= fastLanePriority
                       || _db.Outbox.Any(x => x.LeadId == o.LeadId && x.Status == OutboxStatus.Sent))
                   // Techo de tráfico frío alcanzado: solo calientes por ORIGEN (no por
                   // Priority — el re-enganche hereda score alto y se colaría), leads que
                   // ya nos escribieron, o el resto de una conversación fría abierta HOY.
                   && (!coldCapReached
                       || (o.Lead != null && (int)o.Lead.Source >= HotSourceFloor)
                       || _db.ConversationMessages.Any(m => m.LeadId == o.LeadId
                             && m.Direction == MessageDirection.Inbound)
                       || _db.Outbox.Any(x => x.LeadId == o.LeadId && x.Status == OutboxStatus.Sent
                             && x.SentAt != null && x.SentAt.Value >= dayStart))
                   // Política de mensajería por origen: un lead que nunca recibió nada es un
                   // MENSAJE NUEVO; si ya recibió algo, es SEGUIMIENTO. Cada uno se prende/apaga
                   // por separado y por origen (ej. cortar todo lo nuevo y seguir con Meta Lead Ads).
                   && (o.Lead == null
                       || (_db.Outbox.Any(x => x.LeadId == o.LeadId && x.Status == OutboxStatus.Sent)
                           && followupSources.Contains(o.Lead.Source))
                       || (!_db.Outbox.Any(x => x.LeadId == o.LeadId && x.Status == OutboxStatus.Sent)
                           && outreachSources.Contains(o.Lead.Source)))
                   // Transporte gestionado por la app: estas filas las sirve GET /hub/outbound
                   // (las manda la app por su propia Evolution); sales-hub NO las manda por Evolution.
                   && !(o.Lead != null && _db.Products.Any(p => p.ProductKey == o.Lead.ProductKey && p.AppManagedTransport))
                let leadInProgress = _db.Outbox.Any(x =>
                    x.LeadId == o.LeadId && x.Status == OutboxStatus.Sent)
                let hot = o.Priority >= fastLanePriority
                // FAST LANE primero: un lead caliente recién llegado le gana incluso a las
                // cadencias frías en progreso — lo peor que le pasa al frío es demorar su
                // próximo step un line-gap (~90s), mientras que el caliente enfriándose
                // pierde la venta. Dentro de cada grupo, EN PROGRESO primero: un lead con
                // la cadencia empezada se TERMINA antes de atender a otro — nadie se cuela
                // entre el saludo y el pitch (caso real: "buenos días! soy Eric" y el resto
                // trabado 30 min). Después prioridad, después FIFO.
                orderby hot descending, leadInProgress descending, o.Priority descending, o.ScheduledAt
                select o
            ).Take(20).ToListAsync(ct);
            MessageOutbox? next = null;
            foreach (var cand in candidates)
            {
                // Gap entre contactos nuevos: si este candidato sería el PRIMER mensaje
                // a su lead y todavía no pasó el gap desde la última apertura de la
                // línea, lo salteamos (calientes incluidos — 5 forms juntos salen
                // espaciados, no en ráfaga). Las continuaciones siguen elegibles.
                if (lastFirstContactAt is not null && now - lastFirstContactAt < newContactGap)
                {
                    var isNewContact = !await _db.Outbox.AnyAsync(x =>
                        x.LeadId == cand.LeadId && x.Status == OutboxStatus.Sent, ct);
                    if (isNewContact) continue;
                }
                // Fast lane: los calientes tampoco esperan la ventana horaria del
                // producto — el que llenó el formulario a la noche espera respuesta
                // a la noche (el clamp 08-22 ya se aplicó arriba).
                if (cand.Priority >= fastLanePriority) { next = cand; break; }
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
            string? voiceGreeting = null; // saludo de voz personalizado (audio 1 del cold-open), si el step lo define
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
                    // Saludo de voz personalizado (audio 1 del cold-open): render con el nombre del lead.
                    voiceGreeting = string.IsNullOrWhiteSpace(step.VoiceGreeting)
                        ? null
                        : _renderer.RenderTemplate(step.VoiceGreeting, leadCtx, leadCtx.Product, seller);
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

                // ─── Anti-duplicado textual ──────────────────────────────────
                // El MISMO texto ya le salió a este lead hace poco — lo haya mandado este
                // sender, el bridge, o una fila zombie re-despachada tras un crash o una
                // reconexión (caso real 2026-08-03: intro de onboarding del 30-jul re-enviado
                // ENTERO 4 días después al reconectar la línea). El guion repetido textual es
                // olor a bot y riesgo de ban. Skipped, no Failed: el mensaje ya está dicho.
                // Se excluyen los intentos Failed (retry legítimo de la misma fila).
                if (next.MediaAssetId is null && !string.IsNullOrWhiteSpace(next.Message))
                {
                    var dupWindow = DateTimeOffset.UtcNow.AddDays(-7);
                    var dup = await _db.ConversationMessages.AnyAsync(m =>
                        m.LeadId == next.LeadId
                        && m.Direction == MessageDirection.Outbound
                        && m.Status != MessageDeliveryStatus.Failed
                        && m.Timestamp >= dupWindow
                        && m.Text == next.Message, ct);
                    if (dup)
                    {
                        next.Status = OutboxStatus.Skipped;
                        next.Error = "Duplicado: mismo texto ya enviado al lead en los últimos 7 días";
                        await _db.SaveChangesAsync(ct);
                        continue;
                    }
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
                        // AUDIO 1 del cold-open: saludo personalizado con la voz clonada
                        // ("hola {name}, ¿cómo andás?"), generado por lead y mandado ANTES del
                        // pitch grabado. Best-effort: si falla la síntesis/envío, sigue el pitch.
                        if (!string.IsNullOrWhiteSpace(voiceGreeting) && _tts.IsConfigured)
                        {
                            try
                            {
                                var greetMp3 = await _tts.SynthesizeAsync(voiceGreeting!, _voiceOpts.VoiceId,
                                    new ElevenLabsClient.TtsVoiceSettings(_voiceOpts.Stability, _voiceOpts.SimilarityBoost, _voiceOpts.Style, _voiceOpts.Speed), ct);
                                if (greetMp3 is not null)
                                {
                                    var gprep = await _evo.PrepareVoiceNoteAsync(greetMp3, ct);
                                    var grem = Math.Max(1, gprep.DurationSeconds);
                                    while (grem > 0)
                                    {
                                        var chunk = Math.Min(25, grem);
                                        await _evo.SetPresenceRecordingAsync(seller.EvolutionInstance.InstanceName, jid, chunk, ct);
                                        await Task.Delay(chunk * 1000, ct);
                                        grem -= chunk;
                                    }
                                    sendAttempted = true;
                                    await _evo.SendPreparedVoiceNoteAsync(
                                        seller.EvolutionInstance.InstanceName, next.WhatsappPhone, gprep.OggBytes, ct);
                                    await Task.Delay(1200, ct); // pausa natural entre saludo y pitch
                                }
                            }
                            catch (Exception gex) { _log.LogWarning(gex, "Cold-open: falló el saludo de voz, sigo con el pitch"); }
                        }

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

                // Línea que comparte teléfono con el uso personal: el chat del lead va a
                // Archivados apenas se le manda algo. Best-effort: si falla, el mensaje ya salió.
                if (seller.AutoArchiveChats)
                    await TryArchiveAsync(seller.EvolutionInstance!.InstanceName, next.WhatsappPhone, ct);

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
    /// <summary>
    /// Manda el chat a Archivados usando el id del último mensaje que enviamos (el que
    /// exige Evolution). Nunca tira: archivar es cosmético, el envío ya ocurrió.
    /// </summary>
    private async Task TryArchiveAsync(string instanceName, string phone, CancellationToken ct)
    {
        try
        {
            var id = (_evo as SalesHub.Infrastructure.Evolution.EvolutionClient)?.LastSentMessageId(instanceName, phone);
            if (string.IsNullOrWhiteSpace(id)) return;
            await _evo.ArchiveChatAsync(instanceName, phone, id!, lastFromMe: true, archive: true, ct);
        }
        catch (Exception ex) { _log.LogDebug(ex, "No pude archivar el chat de {Phone}", phone); }
    }

}
