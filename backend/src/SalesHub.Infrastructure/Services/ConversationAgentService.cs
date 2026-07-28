using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
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
    // Techo diario de RESPUESTAS automáticas por vendedor, APARTE del cap de outreach
    // (OutboxSender.MaxMessagesPerSellerPerDay). Contestar a alguien que nos escribió es
    // mucho menos baneable que el contacto frío; cuando compartían cap, el outreach
    // saturaba el cupo temprano y el bot degradaba a "sugerencia" silenciosa el resto del
    // día — leads calientes ("quiero contratar") sin respuesta. Caso real 2026-07-27.
    private const int MaxAutoRepliesPerSellerPerDay = 300;
    // Horas tras crear la cuenta en que seguimos atendiendo al lead aunque quedó Closed
    // (reenviar el link, contestar "cómo funciona"). Después vuelve a quedar excluido.
    private const int PostSignupGraceHours = 72;

    // Re-enganche: cuántas horas de silencio esperamos antes de un nudge, y el tope.
    private const int ReengageAfterHours = 48;
    private const int MaxNudges = 3;
    // Anti-ban: máximo de nudges proactivos auto-enviados por tick (evita ráfagas).
    private const int MaxNudgesPerTickGlobal = 2;

    /// <summary>
    /// Ventana de "settle": no respondemos hasta que el lead estuvo callado este tiempo desde
    /// su último mensaje. Evita responder a mitad de una ráfaga (el clásico: manda el mail y
    /// sigue escribiendo — sin esto contestábamos al 1er mensaje y perdíamos el resto).
    /// </summary>
    private const int BurstSettleSeconds = 12;

    private readonly ApplicationDbContext _db;
    private readonly IEvolutionClient _evo;
    private readonly GroqWhisperClient _whisper;
    private readonly AiSuggestionService _suggestions;
    private readonly OnboardingService _onboarding;
    private readonly ISendScheduler _scheduler;
    private readonly VoiceNoteService _voiceNotes;
    private readonly IMessageRenderer _renderer;
    private readonly Microsoft.Extensions.Configuration.IConfiguration _config;
    private readonly TakeoverSignal _takeover;
    private readonly ILogger<ConversationAgentService> _log;

    public ConversationAgentService(
        ApplicationDbContext db, IEvolutionClient evo, GroqWhisperClient whisper,
        AiSuggestionService suggestions, OnboardingService onboarding, ISendScheduler scheduler,
        VoiceNoteService voiceNotes, IMessageRenderer renderer,
        Microsoft.Extensions.Configuration.IConfiguration config,
        TakeoverSignal takeover,
        ILogger<ConversationAgentService> log)
    {
        _db = db; _evo = evo; _whisper = whisper; _suggestions = suggestions; _onboarding = onboarding;
        _scheduler = scheduler; _voiceNotes = voiceNotes; _renderer = renderer; _config = config;
        _takeover = takeover; _log = log;
    }

    /// <summary>Candidato a respuesta: Priority = reactivado con "+" (salta settle y esperas).</summary>
    private sealed record ReplyCandidate(Guid LeadId, string LastText, DateTimeOffset LastAt, bool Priority);

    public async Task<int> TickAsync(CancellationToken ct)
    {
        var transcribed = await TranscribeAudiosAsync(ct);
        var replied = await GenerateSuggestionsAsync(ct);
        var reengaged = await GenerateReengagementsAsync(ct);
        var postSignup = await GeneratePostSignupNudgesAsync(ct);
        return transcribed + replied + reengaged + postSignup;
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
        // Los Closed normalmente no entran (venta cerrada). EXCEPCIÓN: un lead al que RECIÉN le
        // creamos la cuenta (LeadOnboarding provisionado, ventana de gracia) suele seguir escribiendo
        // ("pasame el link", "cómo funciona") — no lo dejamos hablando solo. Fuera de la ventana
        // vuelve a quedar excluido.
        var postSignupGraceStart = DateTimeOffset.UtcNow.AddHours(-PostSignupGraceHours);
        // Política de mensajería: orígenes cuyos mensajes el bot tiene permitido contestar.
        // Apagado = ni genera la sugerencia (no gastamos IA en algo que no va a salir);
        // el chat sigue entrando a Conversaciones para que lo tome un humano.
        var replySources = (await _db.LoadMessagingPolicyAsync(ct)).ReplySources;
        // AiSuggestedReplyAt sin texto = "ya evaluado, decidimos no responder" (ver abajo):
        // sin ese marcador, los leads con shouldReply=false re-entraban al batch en CADA
        // tick y se re-clasificaban con Claude para siempre. El inbound siguiente limpia
        // ambos campos y el lead vuelve a ser elegible.
        // Orden por recencia del último mensaje: sin ORDER BY, Postgres devolvía un subset
        // arbitrario de un pool de cientos y un lead recién llegado podía quedar hambreado
        // indefinidamente (caso real 2026-07-27: ad lead "quiero contratar" 16 min sin
        // procesar mientras el batch se llenaba de zombies).
        var pool = await (
            from l in _db.Leads
            where l.AiSuggestedReply == null && l.AiSuggestedReplyAt == null && l.SellerId != null
                && replySources.Contains(l.Source)
                && l.BotMutedAt == null // takeover humano: el bot no toca esta conversación
                && l.Status != LeadStatus.Lost
                && (l.Status != LeadStatus.Closed
                    || _db.Set<LeadOnboarding>().Any(o => o.LeadId == l.Id
                        && o.ProvisionedAt != null && o.ProvisionedAt > postSignupGraceStart)
                    // Soporte: un cliente YA convertido (Closed) con un case abierto sigue atendido.
                    || _db.SupportCases.Any(sc => sc.LeadId == l.Id && sc.Status != SupportCaseStatus.Resolved))
            let last = _db.ConversationMessages
                .Where(m => m.LeadId == l.Id)
                .OrderByDescending(m => m.Timestamp)
                .FirstOrDefault()
            where last != null
                && last.Direction == MessageDirection.Inbound
                && last.Text != "[audio]"
            orderby last.Timestamp descending
            select new { LeadId = l.Id, LastText = last.Text, LastAt = last.Timestamp }
        ).Take(BatchSize).ToListAsync(ct);
        var candidates = pool.Select(x => new ReplyCandidate(x.LeadId, x.LastText, x.LastAt, false)).ToList();

        // Leads reactivados con "+": saltan la cola YA, sin filtros de política/estado (es
        // una orden explícita del humano) y aunque su último mensaje sea nuestro (el humano
        // llevó la charla y la devuelve — el bot tiene que retomarla, no esperar al lead).
        var priorityIds = _takeover.Drain();
        if (priorityIds.Count > 0)
        {
            var prio = await (
                from l in _db.Leads
                where priorityIds.Contains(l.Id) && l.SellerId != null && l.BotMutedAt == null
                let last = _db.ConversationMessages
                    .Where(m => m.LeadId == l.Id)
                    .OrderByDescending(m => m.Timestamp)
                    .FirstOrDefault()
                where last != null && last.Text != "[audio]"
                select new { LeadId = l.Id, LastText = last.Text, LastAt = last.Timestamp }
            ).ToListAsync(ct);
            candidates = prio.Select(x => new ReplyCandidate(x.LeadId, x.LastText, x.LastAt, true))
                .Concat(candidates.Where(c => prio.All(p => p.LeadId != c.LeadId)))
                .ToList();
        }

        var onboardingOn = await _db.IsFlagOnAsync("onboarding", false, ct);
        // Notas de voz IA en momentos decisivos (espejo de audio / precio / despedida).
        var voiceNoteOn = await _db.IsFlagOnAsync("voicenote", false, ct);
        // Configs de onboarding habilitadas, por app (multi-app). Vacío si el flag está off.
        var onbConfigs = onboardingOn
            ? await _db.OnboardingConfigs.Where(c => c.Enabled).ToDictionaryAsync(c => c.ProductKey, ct)
            : new Dictionary<string, OnboardingConfig>();

        var done = 0;
        foreach (var c in candidates)
        {
            // Settle: si el lead escribió hace menos de BurstSettleSeconds, esperamos al
            // próximo tick — así juntamos toda la ráfaga (mail + lo que siga) antes de responder.
            // Los reactivados con "+" no esperan: el humano acaba de pedir respuesta inmediata.
            if (!c.Priority && DateTimeOffset.UtcNow < c.LastAt.AddSeconds(BurstSettleSeconds)) continue;

            var lead = await _db.Leads
                .Include(l => l.Product)
                .Include(l => l.Seller).ThenInclude(s => s!.EvolutionInstance)
                .FirstOrDefaultAsync(l => l.Id == c.LeadId, ct);
            if (lead?.Product is null || lead.Seller is null)
            {
                // Candidato roto (producto/vendedor dangling): marcarlo como evaluado para
                // que no vuelva a ocupar un slot del batch en cada tick.
                if (lead is not null)
                {
                    lead.AiSuggestedReplyAt = DateTimeOffset.UtcNow;
                    await _db.SaveChangesAsync(ct);
                }
                continue;
            }

            // Cargamos el hilo COMPLETO una sola vez: sirve para clasificar el estado del
            // lead y, si hace falta, para generar la respuesta.
            var thread = await _db.ConversationMessages
                .Where(m => m.LeadId == lead.Id)
                .OrderBy(m => m.Timestamp)
                .ToListAsync(ct);

            var last = c.LastText;

            // Ráfaga = todo lo que el lead escribió desde nuestra última respuesta (o desde el
            // arranque si nunca respondimos). El onboarding busca el mail en TODA la ráfaga.
            var lastOutboundAt = thread.LastOrDefault(m => m.Direction == MessageDirection.Outbound)?.Timestamp;
            var recentBurst = string.Join("\n", thread
                .Where(m => m.Direction == MessageDirection.Inbound
                         && (lastOutboundAt == null || m.Timestamp > lastOutboundAt))
                .Select(m => m.Text));

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

            // ── Audio ILEGIBLE: el lead mandó una nota de voz que no se pudo transcribir
            // (Evolution no pudo bajar/desencriptar el media, o Groq falló los 3 intentos) y no
            // escribió nada más legible en la ráfaga. NO hay contenido para responder: antes el
            // bot le contestaba a "[audio — no se pudo transcribir]" con la IA → "cualquier cosa".
            // Ahora le pedimos que lo escriba. Al entregar, su último mensaje pasa a outbound (o
            // queda como sugerencia) → no re-entra, sin loop.
            if (IsFailedAudioPlaceholder(last) && !BurstHasRealText(recentBurst))
            {
                await DeliverAsync(lead, PickAudioUnreadableReply(lead.Id), LeadIntent.Unknown, "audio-ilegible", ct);
                _log.LogInformation("Lead {Lead}: audio inaudible → pedido de reescritura (sin IA)", lead.Id);
                done++;
                continue;
            }

            // ── Post-alta: si ya le creamos la cuenta (onboarding provisionado), el alta terminó.
            // SOPORTE REAL en vez de improvisar: si pide el link / no puede entrar / no tiene
            // contraseña, REGENERAMOS el acceso contra la app (bot-register idempotente devuelve
            // un link fresco de auto-login) y le mandamos ese. Fallback: el guardado. Nunca se
            // le "explica" un acceso inventado.
            var onb = await _db.Set<LeadOnboarding>().FirstOrDefaultAsync(o => o.LeadId == lead.Id, ct);
            if (onb?.ProvisionedAt != null && AsksForAccessLink(last)
                && (!string.IsNullOrWhiteSpace(onb.Email) || !string.IsNullOrWhiteSpace(onb.AccessUrl)))
            {
                var fresh = await _onboarding.RegenerateAccessAsync(lead, onb, ct);
                var url = fresh ?? onb.AccessUrl;
                if (!string.IsNullOrWhiteSpace(url))
                {
                    await OnboardingSendAsync(lead,
                        $"entrá con este link, es acceso directo (sin usuario ni contraseña):[NUEVO_MENSAJE]{url}[NUEVO_MENSAJE]avisame si te abre bien, ¿dale?", ct);
                    await _db.SaveChangesAsync(ct);
                    done++;
                    continue;
                }
            }

            // ── Primeros pasos en DOS toques (mantiene el ida y vuelta, mata el "muere en
            // gracias"): 1er inbound post-alta → OFRECER "queres que te diga paso a paso como
            // empezar?"; siguiente inbound → si no dijo que no, va la bullet list de la app.
            if (onb?.ProvisionedAt != null && onb.FirstStepsSentAt == null)
            {
                onbConfigs.TryGetValue(lead.ProductKey, out var fsCfg);
                if (!string.IsNullOrWhiteSpace(fsCfg?.FirstStepsMessage))
                {
                    if (onb.FirstStepsOfferedAt == null)
                    {
                        onb.FirstStepsOfferedAt = DateTimeOffset.UtcNow;
                        var contacto = MessageRenderer.FirstName(onb.ContactName);
                        await OnboardingSendAsync(lead,
                            "{genial|buenisimo}" + (contacto.Length > 0 ? " " + contacto : "") + "! queres que te diga paso a paso como empezar?", ct);
                        await _db.SaveChangesAsync(ct);
                        done++;
                        continue;
                    }
                    // respuesta a la oferta: solo un "no" claro la saltea; cualquier otra cosa = dale
                    if (!DeclinesFirstStepsRx.IsMatch(last ?? string.Empty))
                    {
                        onb.FirstStepsSentAt = DateTimeOffset.UtcNow;
                        var c2 = MessageRenderer.FirstName(onb.ContactName);
                        await OnboardingSendAsync(lead,
                            fsCfg!.FirstStepsMessage.Replace("{contacto}", c2.Length > 0 ? " " + c2 : ""), ct);
                        await _db.SaveChangesAsync(ct);
                        done++;
                        continue;
                    }
                    onb.FirstStepsSentAt = DateTimeOffset.UtcNow; // dijo que no: no insistir
                    // sigue al flujo normal (IA) para responderle lo que haya dicho
                }
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
            // Tras un "+" (StepHumanHandoff) el guion NO retoma el alta: el humano llevó la
            // charla y el bot tiene que SEGUIRLA con la IA libre (todo el hilo), no caer al
            // off-script del onboarding que contesta "corto" como si fuera una duda del alta.
            var handoff = onb?.Step == OnboardingService.StepHumanHandoff;
            var runOnboarding = onbEligible && !handoff
                && (fedToClose
                    || onb is not null
                    || !thread.Any(m => m.Direction == MessageDirection.Outbound));

            // Espera humana configurable: random estable entre Min y Max seg desde el mensaje del lead.
            // Los "+" no esperan (el humano pidió respuesta ya).
            if (!c.Priority && runOnboarding && onbCfg!.ReplyDelayMaxSec > 0)
            {
                var range = onbCfg.ReplyDelayMaxSec - onbCfg.ReplyDelayMinSec;
                var delaySec = onbCfg.ReplyDelayMinSec + (range <= 0 ? 0 : (int)(Math.Abs(c.LastAt.ToUnixTimeSeconds()) % (range + 1)));
                if (DateTimeOffset.UtcNow < c.LastAt.AddSeconds(delaySec)) continue; // todavía no es hora de responder
            }

            if (runOnboarding)
            {
                var ob = await _onboarding.ProcessAsync(lead, last, onbCfg!, ct, recentBurst);
                if (!ob.OffScript)
                {
                    // Audio del pitch (nota de voz, variante al azar). En autoservicio el audio precede al
                    // texto (mail); en venta asistida el audio ES el cierre (no mandamos el texto).
                    var sentAudio = ob.WithPitchAudio && await TrySendPitchAudioAsync(lead, onbCfg!.ProductKey, ct);
                    if ((!sentAudio || onbCfg!.SelfServe) && !string.IsNullOrWhiteSpace(ob.Reply))
                        await OnboardingSendAsync(lead, ob.Reply!, ct);
                    // Reenganche: adjuntos prometidos (video/precios) DESPUÉS de la reacción, y
                    // la 1ª pregunta recién al final — orden de la conversación real que convirtió.
                    if (ob.MediaAssetIds is { Count: > 0 })
                        await TrySendOnboardingMediaAsync(lead, ob.MediaAssetIds, ob.MediaCaptions, ct);
                    if (!string.IsNullOrWhiteSpace(ob.PostMediaText))
                        await OnboardingSendAsync(lead, ob.PostMediaText!, ct);
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
                    // Evaluado, no responder: fuera del pool hasta el próximo inbound.
                    lead.AiSuggestedReplyAt = DateTimeOffset.UtcNow;
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

            // ── SOPORTE: si el lead tiene un case abierto, sus inbounds van al pipeline de
            // soporte (FAQ → LLM+KB → escalación), no al de venta. El case "viejo" (sin
            // actividad hace días) se auto-resuelve y el lead vuelve al pipeline normal.
            var supportCase = await _db.SupportCases
                .FirstOrDefaultAsync(sc => sc.LeadId == lead.Id && sc.Status != SupportCaseStatus.Resolved, ct);
            if (supportCase is not null)
            {
                var staleAfterDays = _config.GetValue("Support:AutoResolveAfterDays", 5);
                var lastActivity = supportCase.LastBotReplyAt ?? supportCase.OpenedAt;
                if (DateTimeOffset.UtcNow - lastActivity > TimeSpan.FromDays(staleAfterDays))
                {
                    supportCase.Status = SupportCaseStatus.Resolved;
                    supportCase.ResolvedAt = DateTimeOffset.UtcNow;
                    await _db.SaveChangesAsync(ct);
                    _log.LogInformation("SupportCase {Case} auto-resuelto por silencio; el lead vuelve al pipeline normal", supportCase.Id);
                    // cae al flujo normal de venta con este mismo mensaje
                }
                else
                {
                    await HandleSupportAsync(lead, thread, last, supportCase, ct);
                    done++;
                    continue;
                }
            }

            // ── Charla real: UNA sola llamada que clasifica el estado Y genera la respuesta.
            if (!_suggestions.IsConfigured) continue;
            var (intent, shouldReply, reply) = await _suggestions.SuggestReplyWithIntentAsync(lead, lead.Product!, thread, ct);

            // El clasificador detectó un problema de PRODUCTO (no de venta) → abre un case
            // y lo toma el pipeline de soporte desde este mismo mensaje.
            if (intent == LeadIntent.Support)
            {
                var opened = new SupportCase
                {
                    Id = Guid.NewGuid(),
                    LeadId = lead.Id,
                    ProductKey = lead.ProductKey,
                    Summary = last.Length > 480 ? last[..480] : last,
                };
                _db.SupportCases.Add(opened);
                await _db.SaveChangesAsync(ct);
                _log.LogInformation("SupportCase {Case} abierto para lead {Lead} ({Pk})", opened.Id, lead.Id, lead.ProductKey);
                await HandleSupportAsync(lead, thread, last, opened, ct);
                done++;
                continue;
            }

            // Señal de compra FUERTE (audio + interés, o frase de avance) → avisamos al vendedor
            // para que tome la charla con un audio personal: el patrón que cierra ventas.
            await MaybeAlertHotLeadAsync(lead, thread, recentBurst, intent, ct);

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

            if (!shouldReply || string.IsNullOrWhiteSpace(reply))
            {
                // Persistir la decisión "no responder": sin esto el lead re-entraba al batch
                // en CADA tick y se re-clasificaba con Claude infinitamente — quemaba tokens
                // y hambreaba a los leads nuevos. El próximo mensaje del lead limpia el
                // marcador y lo vuelve a hacer elegible.
                lead.AiSuggestedReplyAt = DateTimeOffset.UtcNow;
                lead.UpdatedAt = DateTimeOffset.UtcNow;
                await _db.SaveChangesAsync(ct);
                continue;
            }

            // Momento decisivo (espejo de audio / precio / despedida): a veces la respuesta
            // sale como NOTA DE VOZ con la voz clonada. Si el audio salió, ese fue el mensaje;
            // si no (azar, cooldown, tope, fallo), sigue el texto normal.
            if (voiceNoteOn && await _voiceNotes.TryReplyWithVoiceAsync(lead, thread, last, reply!, intent, ct))
            {
                done++;
                continue;
            }

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
    /// Manda los adjuntos del guion de reenganche (video demo / imagen de precios) en el orden
    /// configurado, registrándolos en la conversación. Best-effort: un adjunto que falla no corta
    /// el flujo (la pregunta post-media sale igual). No aplica a productos con transporte
    /// delegado a la app (el relay es solo texto).
    /// </summary>
    private async Task TrySendOnboardingMediaAsync(Lead lead, List<Guid> assetIds, List<string>? captions, CancellationToken ct)
    {
        if (lead.Product?.AppManagedTransport == true) return;
        var instance = lead.Seller?.EvolutionInstance;
        if (instance is null || instance.Status != InstanceStatus.Connected || string.IsNullOrWhiteSpace(lead.WhatsappPhone))
            return;
        var assets = await _db.MediaAssets.AsNoTracking()
            .Where(a => assetIds.Contains(a.Id)).ToListAsync(ct);
        for (var i = 0; i < assetIds.Count; i++)
        {
            var id = assetIds[i];
            var asset = assets.FirstOrDefault(a => a.Id == id);
            if (asset is null) continue;
            var caption = captions is not null && i < captions.Count && !string.IsNullOrWhiteSpace(captions[i])
                ? captions[i] : null;
            if (i > 0) await Task.Delay(TimeSpan.FromSeconds(Random.Shared.Next(4, 9)), ct);
            var ok = await _evo.SendMediaAsync(instance.InstanceName, lead.WhatsappPhone!,
                asset.Content, asset.MimeType, asset.FileName, caption, ct);
            if (!ok)
            {
                _log.LogWarning("Onboarding: falló el adjunto {Asset} para lead {Lead}", id, lead.Id);
                continue;
            }
            var mt = asset.MimeType ?? string.Empty;
            _db.ConversationMessages.Add(new ConversationMessage
            {
                Id = Guid.NewGuid(),
                LeadId = lead.Id,
                SellerId = lead.SellerId,
                Direction = MessageDirection.Outbound,
                Status = MessageDeliveryStatus.Sent,
                Text = (mt.StartsWith("video", StringComparison.OrdinalIgnoreCase) ? "[video]"
                     : mt.StartsWith("image", StringComparison.OrdinalIgnoreCase) ? "[imagen]" : "[archivo]")
                     + (caption is null ? string.Empty : " " + caption),
                EvolutionInstance = instance.InstanceName,
                Timestamp = DateTimeOffset.UtcNow,
                IsRead = true,
            });
        }
        await _db.SaveChangesAsync(ct);
    }

    // "no"/"despues" claros a la oferta del paso a paso — cualquier otra respuesta cuenta como sí.
    private static readonly Regex DeclinesFirstStepsRx = new(
        @"^\s*(no+\b|nah\b|no hace falta|no gracias|despu[eé]s|luego|m[aá]s tarde|ahora no|ya s[eé]\b|ya la vi|ya entend)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex AccessLinkRx = new(
        @"(link|enlace|url|ingres|entrar|acced|acceso|logue|log ?in|inici[aá]r? sesi[oó]n|no me lleg|no puedo entrar|no pude entrar|c[oó]mo entro|d[oó]nde entro|clave|contrase|password|regist|c[oó]digo|no (me )?(anda|funciona|carga|abre)|error|pantalla (en )?blanc)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>El lead ya provisionado pide el acceso/link/credenciales → se lo reenviamos.</summary>
    private static bool AsksForAccessLink(string? msg)
        => !string.IsNullOrWhiteSpace(msg) && AccessLinkRx.IsMatch(msg!);

    // ── Audio inaudible: en vez de contestarle "cualquier cosa" al placeholder de un audio que
    // no se pudo transcribir, le pedimos que lo escriba. Variantes al azar (estable por lead)
    // para no sonar robótico si pasa seguido en la misma línea.
    private static readonly string[] AudioUnreadableReplies =
    {
        "perdón, se me escuchó cortado tu audio 🙈 ¿me lo escribís en un mensajito así te sigo?",
        "uh, no me llegó bien esa nota de voz 🙏 ¿me la pasás por texto?",
        "disculpá, no pude escuchar bien el audio 😅 ¿me lo escribís y seguimos?",
    };
    private static string PickAudioUnreadableReply(Guid seed)
        => AudioUnreadableReplies[(int)((uint)seed.GetHashCode() % (uint)AudioUnreadableReplies.Length)];

    // "[audio — no se pudo transcribir]" (o cualquier variante que arranque con [audio ... no se pudo).
    private static readonly Regex FailedAudioRx = new(
        @"^\[audio\b.*no se pudo", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static bool IsFailedAudioPlaceholder(string? t)
        => !string.IsNullOrWhiteSpace(t) && FailedAudioRx.IsMatch(t!.TrimStart());

    // Línea que es SOLO un placeholder del sistema ([audio], [imagen], [audio — no se pudo…], etc.).
    // Un audio ya transcripto ("🎤 hola quiero info") NO matchea → cuenta como texto real.
    private static readonly Regex SystemPlaceholderLineRx = new(
        @"^\s*(🎤\s*)?\[(audio|imagen|video|documento|sticker|ubicaci[oó]n|contacto)\b[^\]]*\]\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    /// <summary>True si en la ráfaga el lead escribió algo legible además de placeholders.</summary>
    private static bool BurstHasRealText(string? burst)
        => !string.IsNullOrWhiteSpace(burst)
           && burst!.Split('\n').Any(l => !string.IsNullOrWhiteSpace(l) && !SystemPlaceholderLineRx.IsMatch(l));

    // Salvavidas determinístico: el prompt ya prohíbe "boludo" pero el modelo a veces lo ignora.
    // Lo sacamos SIEMPRE del texto que sale al lead (cualquier fuente: aside, chat, sugerencia),
    // pase lo que pase. Nunca, jamás, sale un "boludo".
    private static readonly Regex BoludoRx = new(
        @"\b(bolud[oa]s?|boludit[oa]s?|bolu|boludez(?:es)?|boludeando|pelotud[oa]s?)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static string StripBoludo(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return text ?? string.Empty;
        var t = BoludoRx.Replace(text!, " ");
        t = Regex.Replace(t, @"[ \t]+([,.;:!?])", "$1");     // espacio huérfano antes de puntuación
        t = Regex.Replace(t, @"(^|\n)[ \t]*,[ \t]*", "$1");  // coma que quedó abriendo una línea
        t = Regex.Replace(t, @"[ \t]{2,}", " ");
        return t.Trim();
    }

    /// <summary>
    /// Envío del bot de onboarding: splittea por [NUEVO_MENSAJE] y manda cada parte (como n8n).
    /// Auto-envía SIEMPRE (bypassa AutoPilot — es el bot). Si no se puede enviar (sin teléfono o
    /// instancia desconectada), deja la respuesta como sugerencia.
    /// </summary>
    private async Task OnboardingSendAsync(Lead lead, string text, CancellationToken ct)
    {
        // Placeholders del guion ({seller}, {nombre}, {saludo}, {price}, {checkout_url}…): el
        // guion vivía con "Eric" hardcodeado; ahora usa el vendedor real asignado al lead.
        if (lead.Product is not null)
            text = _renderer.RenderTemplate(text, lead, lead.Product, lead.Seller);
        text = text.Replace("{contacto}", string.Empty);
        text = StripBoludo(text);
        var parts = text.Split("[NUEVO_MENSAJE]", StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Trim()).Where(p => p.Length > 0).ToList();
        if (parts.Count == 0) return;

        // Transporte gestionado por la app: encolar para que la app mande por SU Evolution
        // (no requiere instancia de sales-hub conectada).
        if (lead.Product?.AppManagedTransport == true) { EnqueueRelay(lead, parts); return; }

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
                // "de a poco": pausa proporcional al largo del mensaje (como alguien tipeando
                // de verdad). Antes 2-5s: una pared de 5 burbujas caia en 15 segundos.
                var pause = Math.Clamp(p.Length / 15, 4, 10);
                try { await _evo.SetPresenceTypingAsync(instance!.InstanceName, lead.WhatsappPhone!, pause, ct); } catch { }
                await Task.Delay(TimeSpan.FromSeconds(pause), ct);
            }
            firstPart = false;
            // Persistir ANTES de mandar (ver AutoSendAsync): si el eco fromMe llega antes
            // del commit, el takeover lo confunde con mensaje manual y mutea el bot.
            var msg = new ConversationMessage
            {
                Id = Guid.NewGuid(),
                LeadId = lead.Id,
                SellerId = lead.SellerId,
                Direction = MessageDirection.Outbound,
                Status = MessageDeliveryStatus.Sent,
                Text = p,
                EvolutionInstance = instance!.InstanceName,
                Timestamp = DateTimeOffset.UtcNow,
                IsRead = true,
            };
            _db.ConversationMessages.Add(msg);
            await _db.SaveChangesAsync(ct);
            var ok = await _evo.SendTextAsync(instance!.InstanceName, lead.WhatsappPhone!, p, ct);
            if (!ok) msg.Status = MessageDeliveryStatus.Failed;
        }
        lead.AiSuggestedReply = null;
        lead.AiSuggestedReplyAt = null;
        lead.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Entrega un texto al lead: auto-envía si el producto tiene AutoPilot, está en horario,
    /// bajo el cap diario y no es needs_human; si no, lo deja como sugerencia para el vendedor.
    /// forceAuto bypassa el gate de AutoPilot (lo usa la FAQ canónica de soporte, que es
    /// segura de auto-enviar aun con el producto en modo asistido).
    /// </summary>
    private async Task DeliverAsync(Lead lead, string text, LeadIntent intentForGate, string src, CancellationToken ct, bool forceAuto = false)
    {
        text = StripBoludo(text);
        var autoGate = (forceAuto || lead.Product!.AutoPilot) && intentForGate != LeadIntent.NeedsHuman;
        // Relay: si el producto delega el transporte a la app, encolamos la respuesta (la app la
        // manda por su Evolution). Respetamos AutoPilot + needs_human; el cap/pacing lo aplica el
        // endpoint /hub/outbound. No requiere instancia de sales-hub conectada.
        if (autoGate && lead.Product!.AppManagedTransport)
        {
            EnqueueRelay(lead, new[] { text });
            await _db.SaveChangesAsync(ct);
            _log.LogInformation("Relay: respuesta encolada para lead {Lead} (la app manda; fuente {Src})", lead.Id, src);
            return;
        }

        var canAutoReply = false;
        if (autoGate)
        {
            var st = await SellerSendStateAsync(lead.Seller!, ct);
            // Reply-driven a CUALQUIER hora (pedido 2026-07-21): si el lead escribe a las 23,
            // el bot contesta a las 23 — esperar a la ventana mataba conversiones. La ventana
            // (st.active) sigue aplicando a lo PROACTIVO (OutboxSender/nudges).
            // Cap PROPIO de respuestas: el outreach no consume este cupo (antes compartían
            // los 150 y el bot quedaba mudo en modo sugerencia el resto del día).
            canAutoReply = st.repliesToday < MaxAutoRepliesPerSellerPerDay;
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
    /// <summary>Cooldown para no re-avisar el mismo lead caliente.</summary>
    private static readonly TimeSpan HotAlertCooldown = TimeSpan.FromHours(6);

    /// <summary>Frases de avance/compra fuerte (el momento de saltar con un audio).</summary>
    private static readonly System.Text.RegularExpressions.Regex HotBuyRx = new(
        @"\b(lo necesito|necesito (esto|esta app|el sistema|eso)|quiero (avanzar|arrancar|empezar|contratar|el plan|comprarlo|el sistema)|me sirve|dale vamos|c[oó]mo (contrato|pago|arranco|empiezo|lo compro)|d[oó]nde pago|me interesa (mucho|un mont[oó]n)|cu[aá]ndo (arranco|empiezo|lo tengo))\b",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>
    /// Si el lead da una señal de compra fuerte (mandó un audio + interés, o una frase de
    /// avance), le avisa al vendedor por WhatsApp con un link directo al chat para que lo tome
    /// con un audio personal. Best-effort: si no hay teléfono de aviso configurado, no hace nada.
    /// </summary>
    private async Task MaybeAlertHotLeadAsync(Lead lead, List<ConversationMessage> thread, string recentBurst, LeadIntent intent, CancellationToken ct)
    {
        if (lead.HotAlertedAt is { } last && DateTimeOffset.UtcNow - last < HotAlertCooldown) return;

        // Audio del lead en la ráfaga actual (transcripto = "🎤 …", o "[audio]" todavía sin transcribir).
        var lastOutboundAt = thread.LastOrDefault(m => m.Direction == MessageDirection.Outbound)?.Timestamp;
        var burstHasAudio = thread.Any(m => m.Direction == MessageDirection.Inbound
            && (lastOutboundAt == null || m.Timestamp > lastOutboundAt)
            && (m.Text.StartsWith("🎤", StringComparison.Ordinal) || m.Text.Contains("[audio", StringComparison.OrdinalIgnoreCase)));

        var hot = HotBuyRx.IsMatch(recentBurst)
               || (burstHasAudio && intent is LeadIntent.Interested or LeadIntent.Scheduled);
        if (!hot) return;

        // Teléfono de aviso: el número maestro (el que también recibe inspiraciones).
        var alertPhone = await _db.Set<Core.Domain.Entities.Social.InspirationSettings>()
            .AsNoTracking().Select(s => s.MasterPhone).FirstOrDefaultAsync(ct);
        var instanceName = lead.Seller?.EvolutionInstance?.InstanceName;
        if (string.IsNullOrWhiteSpace(alertPhone) || string.IsNullOrWhiteSpace(instanceName)) return;

        var digits = string.IsNullOrWhiteSpace(lead.WhatsappPhone) ? "" : System.Text.RegularExpressions.Regex.Replace(lead.WhatsappPhone, @"\D", "");
        var wa = digits.Length == 0 ? "" : $"https://wa.me/{digits}";
        var name = string.IsNullOrWhiteSpace(lead.Name) ? "un prospecto" : lead.Name;
        var msg = $"🔥 Lead CALIENTE: {name} — {lead.ProductKey}. Dio señal de compra (audio/quiere avanzar). " +
                  $"Tomalo VOS con un audio ahora 🎙️ {wa}\n(En su chat: \"-\" frena el bot, \"+\" lo reactiva.)";
        try
        {
            await _evo.SendTextAsync(instanceName!, alertPhone!, msg, ct);
            lead.HotAlertedAt = DateTimeOffset.UtcNow;
            await _db.SaveChangesAsync(ct);
            _log.LogInformation("Lead caliente {Lead} → aviso enviado al vendedor", lead.Id);
        }
        catch (Exception ex) { _log.LogWarning(ex, "No pude avisar el lead caliente {Lead}", lead.Id); }
    }

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

        // Re-enganche = seguimiento proactivo a un lead ya contactado: lo manda el mismo
        // switch que los follow-ups de la cadencia.
        var followupSources = (await _db.LoadMessagingPolicyAsync(ct)).FollowupSources;

        var candidates = await (
            from l in _db.Leads
            where l.SellerId != null
                && followupSources.Contains(l.Source)
                && l.BotMutedAt == null // takeover humano: tampoco re-enganchar
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
            select new { LeadId = l.Id, LastAt = last.Timestamp, NeverReplied = false }
        ).Take(BatchSize).ToListAsync(ct);

        // ── Los que NUNCA respondieron: sin esto, un lead contactado que no contesta queda
        // muerto para siempre (el caso típico: Meta Lead Ads que reciben la intro del
        // onboarding y nada más — la cadencia no aplica y el re-enganche clásico exige
        // FirstReplyAt). Nudge a las Reengage:NoReplyAfterHours (default 24) del último
        // outbound, mismo tope MaxNudges. Solo si el lead NO tiene steps de cadencia
        // pendientes (la cadencia encolada YA es su follow-up; no duplicar).
        var noReplyCutoff = now.AddHours(-_config.GetValue("Reengage:NoReplyAfterHours", 24));
        var noReplyCandidates = await (
            from l in _db.Leads
            where l.SellerId != null
                && followupSources.Contains(l.Source)
                && l.BotMutedAt == null
                && l.FirstReplyAt == null
                && l.Status == LeadStatus.Sent
                // Solo leads CALIENTES (anuncios/forms/app-fed, source >= 400): a los scrapeados
                // fríos ya los persiguió su cadencia; nudgearlos a todos quemaría la línea.
                && (int)l.Source >= 400
                && l.AiSuggestedReply == null
                && l.NudgeCount < MaxNudges
                && (l.LastNudgeAt == null || l.LastNudgeAt < noReplyCutoff)
                && !_db.Outbox.Any(o => o.LeadId == l.Id && o.Status == OutboxStatus.Scheduled)
            let last = _db.ConversationMessages
                .Where(m => m.LeadId == l.Id)
                .OrderByDescending(m => m.Timestamp)
                .FirstOrDefault()
            where last != null
                && last.Direction == MessageDirection.Outbound
                && last.Timestamp < noReplyCutoff
            select new { LeadId = l.Id, LastAt = last.Timestamp, NeverReplied = true }
        ).Take(BatchSize).ToListAsync(ct);

        var done = 0;
        foreach (var c in candidates.Concat(noReplyCandidates))
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
                lead, lead.Product!, thread, now - c.LastAt, ct, neverReplied: c.NeverReplied);
            if (string.IsNullOrWhiteSpace(msg))
            {
                // "(sin respuesta)" también consume el intento: sin persistir LastNudgeAt/NudgeCount
                // el lead re-entraba al batch en CADA tick (20s) y se re-puntuaba con Claude para
                // siempre (mismo zombie que ya se arregló en GenerateSuggestionsAsync).
                lead.Score = score;
                lead.NudgeCount++;
                lead.LastNudgeAt = now;
                lead.UpdatedAt = now;
                await _db.SaveChangesAsync(ct);
                _log.LogInformation("Re-enganche declinado por IA para lead {Lead} (score {Score}, nudge {N}/{Max})",
                    lead.Id, score, lead.NudgeCount, MaxNudges);
                continue;
            }
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
    /// Follow-ups POST-ALTA (cuenta ya provisionada): 1) check-in "pudiste entrar? como te
    /// fue?" a las ~20h; 2) oferta de descuento por activación anticipada a las ~96h (solo si
    /// la app la configuró — no inventamos descuentos). Una vez cada uno, solo a leads que NO
    /// escribieron nada desde el alta (los que hablan ya los atiende el bot conversacional).
    /// Salen por el Outbox humanizado → respetan ventana/warmup/caps.
    /// </summary>
    private async Task<int> GeneratePostSignupNudgesAsync(CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var checkinCutoff = now.AddHours(-_config.GetValue("Reengage:CheckinAfterHours", 20));
        var discountCutoff = now.AddHours(-_config.GetValue("Reengage:DiscountAfterHours", 96));
        var tooOld = now.AddDays(-14); // altas viejas: no resucitar historia antigua

        // Nudge post-alta = seguimiento proactivo: mismo switch que la cadencia.
        var followupSources = (await _db.LoadMessagingPolicyAsync(ct)).FollowupSources;

        var rows = await (
            from ob in _db.Set<LeadOnboarding>()
            join l in _db.Leads on ob.LeadId equals l.Id
            where ob.ProvisionedAt != null && ob.ProvisionedAt > tooOld
                && followupSources.Contains(l.Source)
                && l.BotMutedAt == null && l.SellerId != null
                && ((ob.CheckinSentAt == null && ob.ProvisionedAt < checkinCutoff)
                 || (ob.DiscountNudgeSentAt == null && ob.ProvisionedAt < discountCutoff))
                // silencio total desde el alta: si escribió, lo lleva la conversación normal
                && !_db.ConversationMessages.Any(m => m.LeadId == l.Id
                        && m.Direction == MessageDirection.Inbound
                        && m.Timestamp > ob.ProvisionedAt)
            select new { Ob = ob, LeadId = l.Id }
        ).Take(30).ToListAsync(ct);
        if (rows.Count == 0) return 0;

        var cfgs = await _db.OnboardingConfigs.AsNoTracking()
            .ToDictionaryAsync(c => c.ProductKey, ct);

        var done = 0;
        foreach (var r in rows)
        {
            var lead = await _db.Leads
                .Include(l => l.Product)
                .Include(l => l.Seller).ThenInclude(s => s!.EvolutionInstance)
                .FirstOrDefaultAsync(l => l.Id == r.LeadId, ct);
            if (lead?.Product is null || lead.Seller?.EvolutionInstance is not { Status: InstanceStatus.Connected } inst
                || string.IsNullOrWhiteSpace(lead.WhatsappPhone)) continue;
            cfgs.TryGetValue(lead.ProductKey, out var cfg);

            string? template = null;
            var isDiscount = false;
            if (r.Ob.CheckinSentAt == null && r.Ob.ProvisionedAt < checkinCutoff)
            {
                template = !string.IsNullOrWhiteSpace(cfg?.PostSignupCheckin)
                    ? cfg!.PostSignupCheckin
                    : "{resaludo}{pudiste entrar|entraste} al panel? {como te fue|que tal}?[NUEVO_MENSAJE]cualquier cosa que se {trabe|complique} avisame y lo {vemos|resolvemos} juntos";
                r.Ob.CheckinSentAt = now;
            }
            else if (r.Ob.DiscountNudgeSentAt == null && r.Ob.ProvisionedAt < discountCutoff
                && !string.IsNullOrWhiteSpace(cfg?.TrialDiscountNudge))
            {
                template = cfg!.TrialDiscountNudge;
                r.Ob.DiscountNudgeSentAt = now;
                isDiscount = true;
            }
            if (template is null) continue;

            // {resaludo}: saludar por hora SOLO si paso un dia entero desde el ultimo mensaje
            // del hilo (no se re-saluda a alguien con quien hablaste hace un rato).
            var lastMsgAt = await _db.ConversationMessages
                .Where(m => m.LeadId == lead.Id)
                .OrderByDescending(m => m.Timestamp)
                .Select(m => (DateTimeOffset?)m.Timestamp)
                .FirstOrDefaultAsync(ct);
            var greet = lastMsgAt == null || (now - lastMsgAt.Value) >= TimeSpan.FromHours(24)
                ? MessageRenderer.TimeGreeting(lead.Seller) + "! "
                : string.Empty;
            template = template.Replace("{resaludo}", greet).Replace("{saludo}! ", greet);

            var msg = _renderer.RenderTemplate(template, lead, lead.Product, lead.Seller);
            foreach (var part in msg.Split("[NUEVO_MENSAJE]", StringSplitOptions.RemoveEmptyEntries))
            {
                _db.Outbox.Add(new MessageOutbox
                {
                    Id = Guid.NewGuid(),
                    LeadId = lead.Id,
                    SellerId = lead.SellerId!.Value,
                    Channel = MessageChannel.WhatsApp,
                    EvolutionInstance = inst.InstanceName,
                    WhatsappPhone = lead.WhatsappPhone!,
                    Message = part.Trim(),
                    StepIndex = null,
                    Priority = 420, // caliente: cliente con cuenta creada
                    ScheduledAt = now,
                    Status = OutboxStatus.Scheduled,
                });
            }
            await _db.SaveChangesAsync(ct);
            done++;
            _log.LogInformation("Post-alta {Kind} encolado para lead {Lead} ({Product})",
                isDiscount ? "descuento" : "check-in", lead.Id, lead.ProductKey);
        }
        return done;
    }

    /// <summary>
    /// Estado de envío del vendedor hoy: si está en horario activo, cuántos mensajes
    /// salientes ya mandó hoy (outbox + auto-enviados) y su cap diario con warmup ramp.
    /// Con esto el auto-envío del piloto respeta los mismos límites anti-ban que el
    /// outreach humanizado (horario activo, tope diario por número).
    /// </summary>
    private async Task<(bool active, int sentToday, int repliesToday, int dailyCap)> SellerSendStateAsync(Seller seller, CancellationToken ct)
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
        // conversation_messages YA incluye los sends del outbox (el sender los espeja antes
        // de mandar para el eco-matching) además de auto-replies/onboarding/manuales: es el
        // total real del día. OJO: sumarle outbox de nuevo contaba DOBLE cada send del
        // outreach y el cap efectivo quedaba en la mitad del declarado.
        var conv = await _db.ConversationMessages.CountAsync(m => m.SellerId == seller.Id
            && m.Direction == MessageDirection.Outbound
            && m.Timestamp >= startUtc && m.Timestamp < endUtc, ct);

        var cap = _scheduler.ComputeTodayCap(seller, DateOnly.FromDateTime(localNow.DateTime));
        // repliesToday ≈ salientes que NO son outreach del outbox (respuestas del bot,
        // onboarding, manuales): es lo que se compara contra el cap de respuestas.
        return (active, conv, Math.Max(0, conv - outbox), cap);
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

        // El outbound se persiste ANTES de mandar: el eco fromMe del webhook llega en ~1s
        // y HandleOwnMessageAsync lo matchea contra estos registros — si todavía no está
        // commiteado, lo toma como mensaje manual del humano y mutea el bot solo.
        var msg = new ConversationMessage
        {
            Id = Guid.NewGuid(),
            LeadId = lead.Id,
            SellerId = lead.SellerId,
            Direction = MessageDirection.Outbound,
            Status = MessageDeliveryStatus.Sent,
            Text = text,
            EvolutionInstance = instance.InstanceName,
            Timestamp = DateTimeOffset.UtcNow,
            IsRead = true
        };
        _db.ConversationMessages.Add(msg);
        lead.AiSuggestedReply = null;
        lead.AiSuggestedReplyAt = null;
        lead.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);

        var ok = await _evo.SendTextAsync(instance.InstanceName, lead.WhatsappPhone, text, ct);
        if (!ok)
        {
            msg.Status = MessageDeliveryStatus.Failed;
            await _db.SaveChangesAsync(ct);
        }
        return ok;
    }

    /// <summary>
    /// Producto con transporte gestionado por la app (AppManagedTransport): en vez de mandar por
    /// la Evolution de sales-hub, ENCOLA cada parte en el outbox (Scheduled) para que la app la baje
    /// por GET /hub/outbound y la mande por SU propia línea. No requiere instancia de sales-hub
    /// conectada. Registra el outbound en la conversación (corta el loop del bot). Prioridad alta:
    /// son respuestas de una charla activa, salen antes que los openers fríos.
    /// </summary>
    private void EnqueueRelay(Lead lead, IEnumerable<string> parts)
    {
        var now = DateTimeOffset.UtcNow;
        if (lead.SellerId is null || string.IsNullOrWhiteSpace(lead.WhatsappPhone))
        {
            lead.AiSuggestedReply = string.Join("\n", parts);
            lead.AiSuggestedReplyAt = now; lead.UpdatedAt = now;
            return;
        }
        var inst = lead.Seller?.EvolutionInstance?.InstanceName ?? "";
        foreach (var raw in parts)
        {
            var p = raw?.Trim();
            if (string.IsNullOrWhiteSpace(p)) continue;
            _db.Outbox.Add(new MessageOutbox
            {
                Id = Guid.NewGuid(),
                LeadId = lead.Id,
                SellerId = lead.SellerId.Value,
                Channel = MessageChannel.WhatsApp,
                EvolutionInstance = inst,   // referencia; la app manda por SU instancia
                WhatsappPhone = lead.WhatsappPhone!,
                Message = p,
                StepIndex = null,
                Priority = 90,              // charla activa: salta la fila de openers
                ScheduledAt = now,
                Status = OutboxStatus.Scheduled,
            });
            _db.ConversationMessages.Add(new ConversationMessage
            {
                Id = Guid.NewGuid(),
                LeadId = lead.Id,
                SellerId = lead.SellerId,
                Direction = MessageDirection.Outbound,
                Status = MessageDeliveryStatus.Sent,
                Text = p,
                EvolutionInstance = inst,
                Timestamp = now,
                IsRead = true,
            });
        }
        lead.AiSuggestedReply = null;
        lead.AiSuggestedReplyAt = null;
        lead.UpdatedAt = now;
    }

    // ─────────────────────────── SOPORTE (capas) ───────────────────────────

    // Confirmación de resolución ("gracias, ya anda") con guarda contra negaciones
    // ("no anda", "sigue igual") — la negación gana siempre.
    private static readonly Regex ResolvedThanksRx = new(
        @"\b(mil )?gracias\b|ya (est[aá]|qued[oó]|sali[oó]|anda|funciona|pude|entr[eé])\b|\blisto\b|\bperfecto\b|\bgenial\b|solucionad|resuelto",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex StillBrokenRx = new(
        @"no (anda|funciona|puedo|pude|me deja|carga|abre)|sigue (igual|sin|fallando)|todav[ií]a no|a[uú]n no|tampoco",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static bool ConfirmsResolution(string? t)
        => !string.IsNullOrWhiteSpace(t) && ResolvedThanksRx.IsMatch(t!) && !StillBrokenRx.IsMatch(t!);

    /// <summary>
    /// Pipeline de soporte por capas para un inbound con case abierto:
    ///   0. confirmación del cliente → case Resolved + cierre cordial.
    ///   1. FAQ curada (sin IA): respuesta canónica; única capa que puede auto-enviarse
    ///      en modo asistido (Support:FaqAutoSend).
    ///   2. LLM + knowledge base con self-rating: sugiere (asistido) o envía si la
    ///      confianza supera Support:AutoSendMinConfidence (default 999 = nunca).
    ///   3. Escalación (derivar / confianza &lt; Support:EscalateBelowConfidence /
    ///      Support:MaxBotTurns agotados) → aviso al humano + borrador sugerido.
    /// </summary>
    private async Task HandleSupportAsync(Lead lead, List<ConversationMessage> thread, string last,
        SupportCase supportCase, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;

        // 0) El cliente confirma que quedó resuelto → cerrar con un toque humano.
        if (supportCase.LastBotReplyAt != null && ConfirmsResolution(last))
        {
            supportCase.Status = SupportCaseStatus.Resolved;
            supportCase.ResolvedAt = now;
            await DeliverAsync(lead, "de una  cualquier cosa me escribís por acá 🙌", LeadIntent.Support, "soporte-cierre", ct,
                forceAuto: _config.GetValue("Support:FaqAutoSend", false));
            _log.LogInformation("SupportCase {Case} resuelto (confirmación del cliente)", supportCase.Id);
            return;
        }

        // 1) FAQ curada — match por keywords, respuesta canónica tal cual (sin IA).
        var faqs = await _db.ProductFaqs
            .Where(f => f.ProductKey == lead.ProductKey && f.Enabled)
            .OrderByDescending(f => f.TimesUsed)
            .ToListAsync(ct);
        var faq = MatchFaq(faqs, last);
        if (faq is not null)
        {
            faq.TimesUsed++; faq.UpdatedAt = now;
            supportCase.BotTurns++; supportCase.LastBotReplyAt = now;
            await DeliverAsync(lead, faq.Answer, LeadIntent.Support, "soporte-faq", ct,
                forceAuto: _config.GetValue("Support:FaqAutoSend", false));
            _log.LogInformation("SupportCase {Case}: FAQ \"{Q}\" usada", supportCase.Id, faq.Question);
            return;
        }

        // Turnos agotados → directo a escalación, sin quemar más al cliente.
        var maxBotTurns = _config.GetValue("Support:MaxBotTurns", 4);
        if (supportCase.BotTurns >= maxBotTurns)
        {
            await EscalateSupportCaseAsync(lead, supportCase, last, draft: null, ct);
            return;
        }

        // 2) LLM + knowledge base del producto (generada del repo en deploy).
        var kb = await _db.ProductKnowledge.AsNoTracking()
            .Where(k => k.ProductKey == lead.ProductKey)
            .Select(k => k.Content)
            .FirstOrDefaultAsync(ct);
        var (reply, confidence, outcome) = await _suggestions.SuggestSupportReplyAsync(
            lead, lead.Product!, kb, faqs, thread, ct);

        var escalate = outcome == AiSuggestionService.SupportOutcome.Handoff
            || confidence < _config.GetValue("Support:EscalateBelowConfidence", 40)
            || string.IsNullOrWhiteSpace(reply);
        if (escalate)
        {
            await EscalateSupportCaseAsync(lead, supportCase, last, reply, ct);
            return;
        }

        supportCase.BotTurns++;
        supportCase.LastBotReplyAt = now;
        if (confidence >= _config.GetValue("Support:AutoSendMinConfidence", 999))
        {
            await DeliverAsync(lead, reply!, LeadIntent.Support, "soporte-kb", ct);
        }
        else
        {
            // Asistido: queda como borrador para que el vendedor lo apruebe/edite.
            lead.AiSuggestedReply = reply;
            lead.AiSuggestedReplyAt = now;
            lead.UpdatedAt = now;
            await _db.SaveChangesAsync(ct);
            _log.LogInformation("SupportCase {Case}: sugerencia KB (confianza {C})", supportCase.Id, confidence);
        }
    }

    /// <summary>Primera FAQ cuyo keyword aparece en el texto (case-insensitive).</summary>
    private static ProductFaq? MatchFaq(IReadOnlyList<ProductFaq> faqs, string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        foreach (var f in faqs)
            foreach (var k in f.Keywords)
                if (!string.IsNullOrWhiteSpace(k) && text.Contains(k, StringComparison.OrdinalIgnoreCase))
                    return f;
        return null;
    }

    /// <summary>
    /// Capa 3: aviso al humano por WhatsApp (patrón hot-lead: MasterPhone, dedup por
    /// EscalatedAt + cooldown) y el borrador del LLM queda como sugerencia. El case queda
    /// Escalated hasta que un humano lo resuelva en la UI (o auto-resolve por silencio).
    /// </summary>
    private async Task EscalateSupportCaseAsync(Lead lead, SupportCase supportCase, string lastText,
        string? draft, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        supportCase.Status = SupportCaseStatus.Escalated;
        if (!string.IsNullOrWhiteSpace(draft))
        {
            lead.AiSuggestedReply = draft;
            lead.AiSuggestedReplyAt = now;
        }
        lead.UpdatedAt = now;

        if (supportCase.EscalatedAt is { } prev && now - prev < HotAlertCooldown)
        {
            await _db.SaveChangesAsync(ct);
            return; // ya avisado hace poco
        }

        var alertPhone = await _db.Set<Core.Domain.Entities.Social.InspirationSettings>()
            .AsNoTracking().Select(s => s.MasterPhone).FirstOrDefaultAsync(ct);
        var instanceName = lead.Seller?.EvolutionInstance?.InstanceName;
        if (!string.IsNullOrWhiteSpace(alertPhone) && !string.IsNullOrWhiteSpace(instanceName))
        {
            var digits = string.IsNullOrWhiteSpace(lead.WhatsappPhone) ? "" : Regex.Replace(lead.WhatsappPhone, @"\D", "");
            var wa = digits.Length == 0 ? "" : $"https://wa.me/{digits}";
            var name = string.IsNullOrWhiteSpace(lead.Name) ? "un cliente" : lead.Name;
            var problem = supportCase.Summary ?? lastText;
            if (problem.Length > 160) problem = problem[..160] + "…";
            var msg = $"🛟 SOPORTE: {name} — {lead.ProductKey}. Problema: \"{problem}\". " +
                      $"El bot no lo pudo resolver; hay borrador sugerido en el hub. {wa}";
            try
            {
                await _evo.SendTextAsync(instanceName!, alertPhone!, msg, ct);
                supportCase.EscalatedAt = now;
                _log.LogInformation("SupportCase {Case} escalado → aviso al humano", supportCase.Id);
            }
            catch (Exception ex) { _log.LogWarning(ex, "No pude avisar la escalación del case {Case}", supportCase.Id); }
        }
        else
        {
            // Sin canal de aviso: queda Escalated igual (visible en la UI de Soporte).
            supportCase.EscalatedAt ??= now;
        }
        await _db.SaveChangesAsync(ct);
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
