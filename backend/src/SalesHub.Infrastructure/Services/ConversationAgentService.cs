using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SalesHub.Core.Abstractions;
using SalesHub.Core.Domain.Entities;
using SalesHub.Core.Domain.Enums;
using SalesHub.Infrastructure.Persistence;

namespace SalesHub.Infrastructure.Services;

/// <summary>
/// Procesa los mensajes inbound de WhatsApp: (1) transcribe notas de voz vía
/// Groq/Whisper, (2) genera la respuesta al lead, (3) re-engancha al lead que se
/// quedó callado. Para los productos con AutoPilot=true el bot AUTO-ENVÍA por
/// WhatsApp; si no, deja la respuesta como sugerencia para que el vendedor la mande.
/// </summary>
public class ConversationAgentService
{
    private const int MaxTranscriptionAttempts = 3;
    private const int BatchSize = 10;

    // Re-enganche: cuántas horas de silencio esperamos antes de un nudge, y el tope.
    private const int ReengageAfterHours = 48;
    private const int MaxNudges = 3;
    // Anti-ban: máximo de nudges proactivos auto-enviados por tick (evita ráfagas).
    private const int MaxNudgesPerTickGlobal = 2;

    private readonly ApplicationDbContext _db;
    private readonly IEvolutionClient _evo;
    private readonly GroqWhisperClient _whisper;
    private readonly AiSuggestionService _suggestions;
    private readonly OnboardingService _onboarding;
    private readonly ISendScheduler _scheduler;
    private readonly ILogger<ConversationAgentService> _log;

    public ConversationAgentService(
        ApplicationDbContext db, IEvolutionClient evo, GroqWhisperClient whisper,
        AiSuggestionService suggestions, OnboardingService onboarding, ISendScheduler scheduler, ILogger<ConversationAgentService> log)
    {
        _db = db; _evo = evo; _whisper = whisper; _suggestions = suggestions; _onboarding = onboarding; _scheduler = scheduler; _log = log;
    }

    public async Task<int> TickAsync(CancellationToken ct)
    {
        var transcribed = await TranscribeAudiosAsync(ct);
        var replied = await GenerateSuggestionsAsync(ct);
        var reengaged = await GenerateReengagementsAsync(ct);
        return transcribed + replied + reengaged;
    }

    /// <summary>Transcribe las notas de voz inbound que el webhook dejó como "[audio]".</summary>
    private async Task<int> TranscribeAudiosAsync(CancellationToken ct)
    {
        // Sin API key de Groq no tocamos nada — los audios quedan como "[audio]".
        if (!_whisper.IsConfigured) return 0;

        var pending = await _db.ConversationMessages
            .Where(m => m.Direction == MessageDirection.Inbound
                     && m.Text == "[audio]"
                     && m.TranscriptionAttempts < MaxTranscriptionAttempts
                     && m.RawJson != null
                     && m.EvolutionInstance != null)
            .OrderBy(m => m.Timestamp)
            .Take(BatchSize)
            .ToListAsync(ct);

        var done = 0;
        foreach (var msg in pending)
        {
            // Incrementamos el contador ANTES de intentar — si crashea a mitad no
            // quedamos reintentando infinito el mismo audio roto.
            msg.TranscriptionAttempts++;

            string? transcript = null;
            var audio = await _evo.GetMediaBase64Async(msg.EvolutionInstance!, msg.RawJson!, ct);
            if (audio is not null)
                transcript = await _whisper.TranscribeAsync(audio, "voice.ogg", ct);

            if (!string.IsNullOrWhiteSpace(transcript))
            {
                msg.Text = $"🎤 {transcript}";
                done++;
                _log.LogInformation("Audio transcripto: msg={Id} lead={Lead}", msg.Id, msg.LeadId);
            }
            else if (msg.TranscriptionAttempts >= MaxTranscriptionAttempts)
            {
                // Agotó los intentos: lo dejamos legible en vez de "[audio]".
                msg.Text = "[audio — no se pudo transcribir]";
                _log.LogWarning("Audio msg {Id} agotó los {N} intentos de transcripción",
                    msg.Id, MaxTranscriptionAttempts);
            }
            // else: queda "[audio]" con el contador subido, reintenta en el próximo tick.

            await _db.SaveChangesAsync(ct);
        }

        return done;
    }

    /// <summary>
    /// Para los leads cuyo último mensaje es del lead y todavía no tienen respuesta:
    /// genera la respuesta (regla de keyword del vendedor primero, si no Claude) y la
    /// AUTO-ENVÍA si el producto tiene AutoPilot, o la deja como sugerencia si no.
    /// </summary>
    private async Task<int> GenerateSuggestionsAsync(CancellationToken ct)
    {
        // Leads sin sugerencia cuyo último mensaje es del lead. Si el último es
        // "[audio]" todavía sin transcribir, esperamos al próximo tick.
        var candidates = await (
            from l in _db.Leads
            where l.AiSuggestedReply == null && l.SellerId != null
                && l.Status != LeadStatus.Closed && l.Status != LeadStatus.Lost
            let last = _db.ConversationMessages
                .Where(m => m.LeadId == l.Id)
                .OrderByDescending(m => m.Timestamp)
                .FirstOrDefault()
            where last != null
                && last.Direction == MessageDirection.Inbound
                && last.Text != "[audio]"
            select new { LeadId = l.Id, LastText = last.Text, LastAt = last.Timestamp }
        ).Take(BatchSize).ToListAsync(ct);

        var onboardingOn = await _db.IsFlagOnAsync("onboarding", false, ct);
        // Configs de onboarding habilitadas, por app (multi-app). Vacío si el flag está off.
        var onbConfigs = onboardingOn
            ? await _db.OnboardingConfigs.Where(c => c.Enabled).ToDictionaryAsync(c => c.ProductKey, ct)
            : new Dictionary<string, OnboardingConfig>();

        var done = 0;
        foreach (var c in candidates)
        {
            var lead = await _db.Leads
                .Include(l => l.Product)
                .Include(l => l.Seller).ThenInclude(s => s!.EvolutionInstance)
                .FirstOrDefaultAsync(l => l.Id == c.LeadId, ct);
            if (lead?.Product is null || lead.Seller is null) continue;

            // Cargamos el hilo COMPLETO una sola vez: sirve para clasificar el estado del
            // lead y, si hace falta, para generar la respuesta.
            var thread = await _db.ConversationMessages
                .Where(m => m.LeadId == lead.Id)
                .OrderBy(m => m.Timestamp)
                .ToListAsync(ct);

            var last = c.LastText;

            // ── Heurísticos SIN IA: resuelven gratis el ruido (auto-responders de los
            // propios gimnasios, rechazos, números equivocados, pedidos de no contacto).
            // Sacados de los chats reales de gymhero — patrones de bajo falso positivo.

            // Pidió explícitamente que no le escriban → Lost, bloquear re-enganche, sin responder.
            if (AiSuggestionService.IsHardStop(last))
            {
                lead.Status = LeadStatus.Lost;
                lead.ClosedAt ??= DateTimeOffset.UtcNow;
                lead.NudgeCount = MaxNudges; // no re-enganchar nunca
                lead.AiSuggestedReply = null; lead.AiSuggestedReplyAt = null;
                lead.UpdatedAt = DateTimeOffset.UtcNow;
                await _db.SaveChangesAsync(ct);
                _log.LogInformation("Lead {Lead} pidió no contacto → Lost + stop (sin IA)", lead.Id);
                done++;
                continue;
            }

            // Rechazo firme o número equivocado → cierre cordial scripteado (sin IA), Lost.
            var wrong = AiSuggestionService.IsWrongNumber(last);
            if (wrong || AiSuggestionService.IsFirmReject(last))
            {
                lead.Status = LeadStatus.Lost;
                lead.ClosedAt ??= DateTimeOffset.UtcNow;
                await DeliverAsync(lead, AiSuggestionService.ScriptedCordialClose(wrong), LeadIntent.NotInterested, "script", ct);
                done++;
                continue;
            }

            // ── Onboarding de ads MULTI-APP. Gated por flag 'onboarding' + config Enabled de la app.
            // Solo arranca si el lead ya está en el flujo o es FRESCO (sin outbound previo): así
            // prender el flag NO re-introduce leads viejos con historia (backfill / atendidos por n8n).
            onbConfigs.TryGetValue(lead.ProductKey, out var onbCfg);
            // El onboarding (alta + provisión de la cuenta vía bot-register) corre para:
            //  - anuncios CTWA (WhatsAppAd), como siempre;
            //  - RE-ENGANCHADOS (ProductReengage) y LEADS DE FORMULARIOS DE META (MetaLeadAd), sólo en
            //    productos self-serve → así no quedan en "interesado": el bot los lleva hasta CREAR la
            //    cuenta y mandarles el AccessUrl. A reengage/meta los dejamos entrar aunque ya tengan
            //    historia de mensajes (el opener ya salió; ese es justo el punto).
            var fedToClose = lead.Source is LeadSource.ProductReengage or LeadSource.MetaLeadAd;
            var onbEligible = onbCfg is not null
                && (lead.Source == LeadSource.WhatsAppAd || (fedToClose && onbCfg.SelfServe));
            var runOnboarding = onbEligible
                && (fedToClose
                    || await _db.Set<LeadOnboarding>().AnyAsync(o => o.LeadId == lead.Id, ct)
                    || !thread.Any(m => m.Direction == MessageDirection.Outbound));

            // Espera humana configurable: random estable entre Min y Max seg desde el mensaje del lead.
            if (runOnboarding && onbCfg!.ReplyDelayMaxSec > 0)
            {
                var range = onbCfg.ReplyDelayMaxSec - onbCfg.ReplyDelayMinSec;
                var delaySec = onbCfg.ReplyDelayMinSec + (range <= 0 ? 0 : (int)(Math.Abs(c.LastAt.ToUnixTimeSeconds()) % (range + 1)));
                if (DateTimeOffset.UtcNow < c.LastAt.AddSeconds(delaySec)) continue; // todavía no es hora de responder
            }

            if (runOnboarding)
            {
                var ob = await _onboarding.ProcessAsync(lead, last, onbCfg!, ct);
                if (!ob.OffScript)
                {
                    // Audio del pitch (nota de voz, variante al azar). En autoservicio el audio precede al
                    // texto (mail); en venta asistida el audio ES el cierre (no mandamos el texto).
                    var sentAudio = ob.WithPitchAudio && await TrySendPitchAudioAsync(lead, onbCfg!.ProductKey, ct);
                    if ((!sentAudio || onbCfg!.SelfServe) && !string.IsNullOrWhiteSpace(ob.Reply))
                        await OnboardingSendAsync(lead, ob.Reply!, ct);
                    await _db.SaveChangesAsync(ct);
                    done++;
                    continue;
                }
                // Off-script (preguntó precio/info/etc.): la IA contesta CORTO (sin follow-up) y
                // después reenviamos la pregunta pendiente del alta, para que el guion siga su curso.
                var aside = await _suggestions.AnswerOnboardingAsideAsync(lead, lead.Product!, thread, ct);
                var parts = new List<string>();
                if (!string.IsNullOrWhiteSpace(aside)) parts.Add(aside!);
                if (!string.IsNullOrWhiteSpace(ob.PendingQuestion)) parts.Add(ob.PendingQuestion!);
                if (parts.Count > 0) await OnboardingSendAsync(lead, string.Join("[NUEVO_MENSAJE]", parts), ct);
                await _db.SaveChangesAsync(ct);
                done++;
                continue;
            }

            // Auto-responder del propio gimnasio → re-pitch por texto scripteado (sin IA).
            // Si el del otro lado SOLO dispara auto-responders (≥2), es un número desatendido
            // o un bot: cortar (esto mata los loops IA-vs-IA que quemaban tokens).
            if (AiSuggestionService.IsAutoResponder(last))
            {
                var autoCount = thread.Count(m => m.Direction == MessageDirection.Inbound
                    && AiSuggestionService.IsAutoResponder(m.Text));
                if (autoCount >= 2)
                {
                    lead.NudgeCount = Math.Max(lead.NudgeCount, 1);
                    lead.UpdatedAt = DateTimeOffset.UtcNow;
                    await _db.SaveChangesAsync(ct);
                    continue;
                }
                await DeliverAsync(lead, AiSuggestionService.ScriptedAutoResponderReply(), LeadIntent.Unknown, "script", ct);
                done++;
                continue;
            }

            // Regla de keyword del vendedor (sin IA), si configuró alguna.
            var keywordReply = MatchKeywordRule(lead.Seller.KeywordRules, last);
            if (keywordReply is not null)
            {
                await DeliverAsync(lead, keywordReply, LeadIntent.Unknown, "keyword", ct);
                done++;
                continue;
            }

            // ── Charla real: UNA sola llamada que clasifica el estado Y genera la respuesta.
            if (!_suggestions.IsConfigured) continue;
            var (intent, shouldReply, reply) = await _suggestions.SuggestReplyWithIntentAsync(lead, lead.Product!, thread, ct);

            // Si quedó resuelto (no interesado / ya compró) no respondemos.
            if (ApplyIntentToStatus(lead, intent))
            {
                lead.AiSuggestedReply = null; lead.AiSuggestedReplyAt = null;
                lead.UpdatedAt = DateTimeOffset.UtcNow;
                await _db.SaveChangesAsync(ct);
                _log.LogInformation("Lead {Lead} clasificado {Intent} → {Status}; sin respuesta", lead.Id, intent, lead.Status);
                done++;
                continue;
            }

            if (!shouldReply || string.IsNullOrWhiteSpace(reply)) continue;

            await DeliverAsync(lead, reply!, intent, "IA", ct);
            done++;
        }

        return done;
    }

    /// <summary>
    /// Manda la nota de voz del pitch: elige una variante de audio al azar de la app (rotación
    /// anti-detección de Meta) y la envía como PTT por Evolution. False si no hay audios o falló.
    /// </summary>
    private async Task<bool> TrySendPitchAudioAsync(Lead lead, string productKey, CancellationToken ct)
    {
        var instance = lead.Seller?.EvolutionInstance;
        if (instance is null || instance.Status != InstanceStatus.Connected || string.IsNullOrWhiteSpace(lead.WhatsappPhone))
            return false;
        var audios = await _db.OnboardingAudios.Where(a => a.ProductKey == productKey).Select(a => a.Data).ToListAsync(ct);
        if (audios.Count == 0) return false;
        var pick = audios[Random.Shared.Next(audios.Count)];
        var ok = await _evo.SendPreparedVoiceNoteAsync(instance.InstanceName, lead.WhatsappPhone!, pick, ct);
        if (!ok) return false;
        _db.ConversationMessages.Add(new ConversationMessage
        {
            Id = Guid.NewGuid(),
            LeadId = lead.Id,
            SellerId = lead.SellerId,
            Direction = MessageDirection.Outbound,
            Status = MessageDeliveryStatus.Sent,
            Text = "[audio]",
            EvolutionInstance = instance.InstanceName,
            Timestamp = DateTimeOffset.UtcNow,
            IsRead = true,
        });
        return true;
    }

    /// <summary>
    /// Envío del bot de onboarding: splittea por [NUEVO_MENSAJE] y manda cada parte (como n8n).
    /// Auto-envía SIEMPRE (bypassa AutoPilot — es el bot). Si no se puede enviar (sin teléfono o
    /// instancia desconectada), deja la respuesta como sugerencia.
    /// </summary>
    private async Task OnboardingSendAsync(Lead lead, string text, CancellationToken ct)
    {
        var parts = text.Split("[NUEVO_MENSAJE]", StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Trim()).Where(p => p.Length > 0).ToList();
        if (parts.Count == 0) return;

        var instance = lead.Seller?.EvolutionInstance;
        var canSend = instance is not null && instance.Status == InstanceStatus.Connected
            && !string.IsNullOrWhiteSpace(lead.WhatsappPhone);

        if (!canSend)
        {
            lead.AiSuggestedReply = string.Join("\n", parts);
            lead.AiSuggestedReplyAt = DateTimeOffset.UtcNow;
            lead.UpdatedAt = DateTimeOffset.UtcNow;
            return;
        }

        var firstPart = true;
        foreach (var p in parts)
        {
            // No mandar todo en ráfaga: a partir del 2º mensaje, mostrar "escribiendo…" y una
            // pausa breve proporcional al largo (más humano, menos cara de bot).
            if (!firstPart)
            {
                var pause = Math.Clamp(p.Length / 25, 2, 5);
                try { await _evo.SetPresenceTypingAsync(instance!.InstanceName, lead.WhatsappPhone!, pause, ct); } catch { }
                await Task.Delay(TimeSpan.FromSeconds(pause), ct);
            }
            firstPart = false;
            var ok = await _evo.SendTextAsync(instance!.InstanceName, lead.WhatsappPhone!, p, ct);
            _db.ConversationMessages.Add(new ConversationMessage
            {
                Id = Guid.NewGuid(),
                LeadId = lead.Id,
                SellerId = lead.SellerId,
                Direction = MessageDirection.Outbound,
                Status = ok ? MessageDeliveryStatus.Sent : MessageDeliveryStatus.Failed,
                Text = p,
                EvolutionInstance = instance.InstanceName,
                Timestamp = DateTimeOffset.UtcNow,
                IsRead = true,
            });
        }
        lead.AiSuggestedReply = null;
        lead.AiSuggestedReplyAt = null;
        lead.UpdatedAt = DateTimeOffset.UtcNow;
    }

    /// <summary>
    /// Entrega un texto al lead: auto-envía si el producto tiene AutoPilot, está en horario,
    /// bajo el cap diario y no es needs_human; si no, lo deja como sugerencia para el vendedor.
    /// </summary>
    private async Task DeliverAsync(Lead lead, string text, LeadIntent intentForGate, string src, CancellationToken ct)
    {
        var canAutoReply = false;
        if (lead.Product!.AutoPilot && intentForGate != LeadIntent.NeedsHuman)
        {
            var st = await SellerSendStateAsync(lead.Seller!, ct);
            canAutoReply = st.active && st.sentToday < OutboxSender.MaxMessagesPerSellerPerDay;
        }

        if (canAutoReply && await AutoSendAsync(lead, text, ct))
        {
            _log.LogInformation("Auto-respondido a lead {Lead} (fuente: {Src})", lead.Id, src);
        }
        else
        {
            lead.AiSuggestedReply = text;
            lead.AiSuggestedReplyAt = DateTimeOffset.UtcNow;
            lead.UpdatedAt = DateTimeOffset.UtcNow;
            _log.LogInformation("Sugerencia generada para lead {Lead} (fuente: {Src})", lead.Id, src);
        }

        await _db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Mapea la intención detectada por IA a un LeadStatus y la aplica. Conservador:
    /// nunca pisa una venta ya cerrada (Closed). Devuelve true si la conversación quedó
    /// "resuelta" (no interesado o ganado) → no hay que seguir vendiendo ni re-enganchar.
    /// </summary>
    private bool ApplyIntentToStatus(Lead lead, LeadIntent intent)
    {
        if (lead.Status == LeadStatus.Closed) return true; // venta cerrada, no tocar

        switch (intent)
        {
            case LeadIntent.NotInterested:
                if (lead.Status != LeadStatus.Lost) lead.Status = LeadStatus.Lost;
                lead.ClosedAt ??= DateTimeOffset.UtcNow; // mismo criterio que LeadsController (Closed/Lost)
                return true; // dado por cerrado: el re-enganche ya excluye Lost

            case LeadIntent.Won:
                lead.Status = LeadStatus.Closed;
                lead.ClosedAt ??= DateTimeOffset.UtcNow; // así cuenta para el objetivo de Cierres
                return true;

            case LeadIntent.Scheduled:
                if (lead.Status != LeadStatus.DemoScheduled) lead.Status = LeadStatus.DemoScheduled;
                lead.DemoScheduledAt ??= DateTimeOffset.UtcNow; // así cuenta para el objetivo de Demos
                return false; // agendó, pero seguimos la charla / confirmamos

            case LeadIntent.Interested:
                // "posible": lo dejamos abierto en Interested → el re-enganche lo sigue
                // ofreciendo cada ReengageAfterHours. Reabrimos un Lost si vuelve a mostrar interés.
                if (lead.Status is LeadStatus.Sent or LeadStatus.Replied or LeadStatus.Lost)
                    lead.Status = LeadStatus.Interested;
                return false;

            case LeadIntent.NeedsHuman:
            case LeadIntent.Unknown:
            default:
                return false;
        }
    }

    /// <summary>
    /// Backfill: reclasifica el estado de los leads que tienen conversación previa y
    /// siguen en Sent/Replied (sin resolver), analizando todo el hilo con IA. Procesa
    /// hasta <paramref name="max"/> por llamada para no colgar la request. Devuelve
    /// cuántos procesó y cuántos quedan pendientes.
    /// </summary>
    public async Task<(int processed, int remaining)> ReclassifyExistingAsync(int max, CancellationToken ct)
    {
        if (!_suggestions.IsConfigured) return (0, 0);

        var candidateIds = await _db.Leads
            .Where(l => l.SellerId != null
                && (l.Status == LeadStatus.Sent || l.Status == LeadStatus.Replied)
                && _db.ConversationMessages.Any(m => m.LeadId == l.Id && m.Direction == MessageDirection.Inbound))
            .OrderByDescending(l => l.UpdatedAt)
            .Select(l => l.Id)
            .Take(Math.Clamp(max, 1, 200))
            .ToListAsync(ct);

        var processed = 0;
        foreach (var id in candidateIds)
        {
            ct.ThrowIfCancellationRequested();
            var lead = await _db.Leads.Include(l => l.Product).FirstOrDefaultAsync(l => l.Id == id, ct);
            if (lead?.Product is null) continue;

            var thread = await _db.ConversationMessages
                .Where(m => m.LeadId == id)
                .OrderBy(m => m.Timestamp)
                .ToListAsync(ct);

            var intent = await _suggestions.ClassifyLeadAsync(lead, lead.Product, thread, ct);
            ApplyIntentToStatus(lead, intent);
            lead.UpdatedAt = DateTimeOffset.UtcNow;
            await _db.SaveChangesAsync(ct);
            processed++;
        }

        var remaining = await _db.Leads
            .CountAsync(l => l.SellerId != null
                && (l.Status == LeadStatus.Sent || l.Status == LeadStatus.Replied)
                && _db.ConversationMessages.Any(m => m.LeadId == l.Id && m.Direction == MessageDirection.Inbound), ct);

        return (processed, remaining);
    }

    /// <summary>
    /// Re-engancha leads que respondieron alguna vez, donde el último mensaje es
    /// nuestro (outbound) y pasaron &gt;= ReengageAfterHours sin novedad. Capea a
    /// MaxNudges por lead. Auto-envía si AutoPilot; si no, deja sugerencia.
    /// </summary>
    private async Task<int> GenerateReengagementsAsync(CancellationToken ct)
    {
        if (!_suggestions.IsConfigured) return 0;

        var now = DateTimeOffset.UtcNow;
        var cutoff = now.AddHours(-ReengageAfterHours);

        var candidates = await (
            from l in _db.Leads
            where l.SellerId != null
                && l.FirstReplyAt != null
                && (l.Status == LeadStatus.Replied || l.Status == LeadStatus.Interested)
                && l.AiSuggestedReply == null
                && l.NudgeCount < MaxNudges
                && (l.LastNudgeAt == null || l.LastNudgeAt < cutoff)
            let last = _db.ConversationMessages
                .Where(m => m.LeadId == l.Id)
                .OrderByDescending(m => m.Timestamp)
                .FirstOrDefault()
            where last != null
                && last.Direction == MessageDirection.Outbound
                && last.Timestamp < cutoff
            select new { LeadId = l.Id, LastAt = last.Timestamp }
        ).Take(BatchSize).ToListAsync(ct);

        var done = 0;
        foreach (var c in candidates)
        {
            var lead = await _db.Leads
                .Include(l => l.Product)
                .Include(l => l.Seller).ThenInclude(s => s!.EvolutionInstance)
                .FirstOrDefaultAsync(l => l.Id == c.LeadId, ct);
            if (lead?.Product is null || lead.Seller is null) continue;

            // Re-enganche PROACTIVO automático solo si el producto opta in (AutoPilot +
            // AutoReengage). El proactivo es lo más baneable, así que es un toggle aparte.
            var autoReengage = lead.Product!.AutoPilot && lead.Product!.AutoReengage;

            var thread = await _db.ConversationMessages
                .Where(m => m.LeadId == lead.Id)
                .OrderBy(m => m.Timestamp)
                .ToListAsync(ct);

            // Una sola llamada a Claude: mensaje de re-enganche + score (0-100) del lead.
            var (msg, score) = await _suggestions.SuggestReengagementWithScoreAsync(
                lead, lead.Product!, thread, now - c.LastAt, ct);
            if (string.IsNullOrWhiteSpace(msg)) continue;
            lead.Score = score; // el análisis ⇒ prioridad en la cola

            var instance = lead.Seller!.EvolutionInstance;
            var canQueue = autoReengage && instance is not null
                && instance.Status == InstanceStatus.Connected
                && !string.IsNullOrWhiteSpace(lead.WhatsappPhone);

            if (canQueue)
            {
                // Encolar en la cola HUMANIZADA (OutboxSender + SendScheduler) con prioridad = score.
                // No mandamos directo: el sender respeta warmup/jitter/burst/caps y los CALIENTES
                // (score alto) saltan la fila. Así el re-enganche vende sin rafaguear.
                _db.Outbox.Add(new MessageOutbox
                {
                    Id = Guid.NewGuid(),
                    LeadId = lead.Id,
                    SellerId = lead.SellerId!.Value,
                    Channel = MessageChannel.WhatsApp,
                    EvolutionInstance = instance!.InstanceName,
                    WhatsappPhone = lead.WhatsappPhone!,
                    Message = msg!,
                    StepIndex = null,       // snapshot estático: el sender manda Message tal cual
                    Priority = score,
                    ScheduledAt = now,
                    Status = OutboxStatus.Scheduled,
                });
            }
            else
            {
                // No auto (o sin instancia/teléfono): dejar como sugerencia para el vendedor.
                lead.AiSuggestedReply = msg;
                lead.AiSuggestedReplyAt = now;
            }

            lead.NudgeCount++;
            lead.LastNudgeAt = now;
            lead.UpdatedAt = now;
            await _db.SaveChangesAsync(ct);
            done++;
            _log.LogInformation("Re-enganche {Mode} a lead {Lead} (score {Score}, nudge {N}/{Max})",
                canQueue ? "encolado" : "sugerido", lead.Id, score, lead.NudgeCount, MaxNudges);
        }

        return done;
    }

    /// <summary>
    /// Estado de envío del vendedor hoy: si está en horario activo, cuántos mensajes
    /// salientes ya mandó hoy (outbox + auto-enviados) y su cap diario con warmup ramp.
    /// Con esto el auto-envío del piloto respeta los mismos límites anti-ban que el
    /// outreach humanizado (horario activo, tope diario por número).
    /// </summary>
    private async Task<(bool active, int sentToday, int dailyCap)> SellerSendStateAsync(Seller seller, CancellationToken ct)
    {
        var tz = SafeTz(seller.Timezone);
        var localNow = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, tz);
        var active = localNow.Hour >= seller.ActiveHoursStart && localNow.Hour < seller.ActiveHoursEnd;

        var localMidnight = new DateTimeOffset(localNow.Year, localNow.Month, localNow.Day, 0, 0, 0, localNow.Offset);
        var startUtc = localMidnight.ToUniversalTime();
        var endUtc = startUtc.AddDays(1);

        var outbox = await _db.Outbox.CountAsync(o => o.SellerId == seller.Id
            && o.Status == OutboxStatus.Sent && o.SentAt != null
            && o.SentAt >= startUtc && o.SentAt < endUtc, ct);
        var conv = await _db.ConversationMessages.CountAsync(m => m.SellerId == seller.Id
            && m.Direction == MessageDirection.Outbound
            && m.Timestamp >= startUtc && m.Timestamp < endUtc, ct);

        var cap = _scheduler.ComputeTodayCap(seller, DateOnly.FromDateTime(localNow.DateTime));
        return (active, outbox + conv, cap);
    }

    private static TimeZoneInfo SafeTz(string id)
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
        catch { return TimeZoneInfo.Utc; }
    }

    /// <summary>
    /// Envía un mensaje al lead por la instancia de Evolution de su vendedor y lo
    /// registra como outbound. Devuelve false (sin enviar) si la instancia no está
    /// conectada o falta el teléfono — el caller cae a dejar sugerencia.
    /// </summary>
    private async Task<bool> AutoSendAsync(Lead lead, string text, CancellationToken ct)
    {
        var instance = lead.Seller?.EvolutionInstance;
        if (instance is null || instance.Status != InstanceStatus.Connected) return false;
        if (string.IsNullOrWhiteSpace(lead.WhatsappPhone)) return false;

        var ok = await _evo.SendTextAsync(instance.InstanceName, lead.WhatsappPhone, text, ct);
        _db.ConversationMessages.Add(new ConversationMessage
        {
            Id = Guid.NewGuid(),
            LeadId = lead.Id,
            SellerId = lead.SellerId,
            Direction = MessageDirection.Outbound,
            Status = ok ? MessageDeliveryStatus.Sent : MessageDeliveryStatus.Failed,
            Text = text,
            EvolutionInstance = instance.InstanceName,
            Timestamp = DateTimeOffset.UtcNow,
            IsRead = true
        });
        lead.AiSuggestedReply = null;
        lead.AiSuggestedReplyAt = null;
        lead.UpdatedAt = DateTimeOffset.UtcNow;
        return ok;
    }

    /// <summary>
    /// Devuelve la respuesta de la primera regla "keyword = respuesta" cuyo
    /// keyword aparezca (case-insensitive) en el texto del lead. null si no
    /// matchea ninguna. Soporta "\n" literal en la respuesta como salto de línea.
    /// </summary>
    private static string? MatchKeywordRule(IEnumerable<string> rules, string leadText)
    {
        if (string.IsNullOrWhiteSpace(leadText)) return null;
        foreach (var rule in rules)
        {
            var eq = rule.IndexOf('=');
            if (eq <= 0) continue;
            var keyword = rule[..eq].Trim();
            var reply = rule[(eq + 1)..].Trim().Replace("\\n", "\n");
            if (keyword.Length == 0 || reply.Length == 0) continue;
            if (leadText.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                return reply;
        }
        return null;
    }
}
