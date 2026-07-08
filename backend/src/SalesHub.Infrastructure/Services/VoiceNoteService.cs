using System.Text.RegularExpressions;
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
/// Respuestas del bot en NOTA DE VOZ (voz clonada de Eric, ElevenLabs) en momentos
/// decisivos de la venta, con azar para que no sea mecánico:
///  - ESPEJO: el lead mandó un audio → alta probabilidad de contestarle en audio.
///  - DECISIVO: la respuesta toca precio/planes/crear cuenta/demo, o el lead quedó
///    clasificado interesado → probabilidad media.
///  - DESPEDIDA: la charla cierra en buenos términos → audio que termina en "un abrazo".
/// El guion NO es el texto del chat: Claude lo reescribe con la receta de pronunciación
/// aprobada (voseo con tilde, "shama", montos en lucas, conectores reales de Eric,
/// puntuación completa, cierre afirmativo). Gates: flag 'voicenote', producto AutoPilot,
/// cooldown por lead y tope diario global.
/// </summary>
public class VoiceNoteService
{
    private readonly ApplicationDbContext _db;
    private readonly ElevenLabsClient _tts;
    private readonly ClaudeClient _claude;
    private readonly IEvolutionClient _evo;
    private readonly VoiceNoteOptions _opts;
    private readonly ILogger<VoiceNoteService> _log;

    public VoiceNoteService(
        ApplicationDbContext db, ElevenLabsClient tts, ClaudeClient claude,
        IEvolutionClient evo, IOptions<VoiceNoteOptions> opts, ILogger<VoiceNoteService> log)
    {
        _db = db; _tts = tts; _claude = claude; _evo = evo; _opts = opts.Value; _log = log;
    }

    /// <summary>Marcador en el hilo: outbound de audio del bot con su guion (para contexto de la IA y la UI).</summary>
    private const string AudioPrefix = "🎤 ";

    private static readonly Regex DecisiveRx = new(
        @"(cu[aá]nto (sale|vale|cuesta|es)|precio|planes|mensual|por mes|lucas|te (armo|creo) la cuenta|pasame tu mail|tu correo|demo|lo vemos juntos|contrat|empez[aá]mos|arrancamos)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex FarewellRx = new(
        @"(gracias|genial|buen[ií]simo|lo pruebo|lo veo y te (digo|aviso)|te aviso|hablamos|nos vemos|abrazo|dale,? lo (miro|veo)|perfecto,? lo)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private enum Trigger { None, Mirror, Decisive, Farewell }

    /// <summary>
    /// Si el momento lo amerita (y el azar acompaña), manda la respuesta como nota de voz
    /// y devuelve true — en ese caso el caller NO manda el texto. False = seguir por texto.
    /// </summary>
    public async Task<bool> TryReplyWithVoiceAsync(
        Lead lead, IReadOnlyList<ConversationMessage> thread, string lastInbound, string reply,
        LeadIntent intent, CancellationToken ct)
    {
        if (!_tts.IsConfigured || !_claude.IsConfigured) return false;
        if (lead.Product?.AutoPilot != true) return false;
        var instance = lead.Seller?.EvolutionInstance;
        if (instance is null || instance.Status != InstanceStatus.Connected) return false;
        if (string.IsNullOrWhiteSpace(lead.WhatsappPhone)) return false;

        var trigger = DetectTrigger(thread, lastInbound, reply, intent);
        if (trigger == Trigger.None) return false;

        // Cooldown por lead: si ya le mandamos un audio en la ventana, seguimos por texto.
        var cooldownStart = DateTimeOffset.UtcNow.AddHours(-_opts.CooldownHours);
        if (thread.Any(m => m.Direction == MessageDirection.Outbound
                         && m.Timestamp >= cooldownStart
                         && (m.Text.StartsWith(AudioPrefix) || m.Text.StartsWith("[audio"))))
            return false;

        // Tope global diario (guarda de créditos).
        var dayStart = DateTimeOffset.UtcNow.AddHours(-24);
        var sentToday = await _db.ConversationMessages.CountAsync(m =>
            m.Direction == MessageDirection.Outbound
            && m.Timestamp >= dayStart
            && m.Text.StartsWith(AudioPrefix), ct);
        if (sentToday >= _opts.MaxPerDay)
        {
            _log.LogInformation("VoiceNote: tope diario alcanzado ({N}) — lead {Lead} sigue por texto", sentToday, lead.Id);
            return false;
        }

        // El azar: que no sea mecánico.
        var prob = trigger switch
        {
            Trigger.Mirror => _opts.MirrorProbability,
            Trigger.Farewell => _opts.FarewellProbability,
            _ => _opts.DecisiveProbability,
        };
        if (Random.Shared.NextDouble() > prob) return false;

        // Datos reales del producto: única fuente de verdad para precios/planes/features
        // en el guion (el modelo tiene PROHIBIDO inventar números).
        var facts = BuildProductFacts(lead.Product!);
        var script = await BuildScriptAsync(reply, trigger, facts, ct);
        if (string.IsNullOrWhiteSpace(script)) return false;

        var mp3 = await _tts.SynthesizeAsync(script!, _opts.VoiceId,
            new ElevenLabsClient.TtsVoiceSettings(_opts.Stability, _opts.SimilarityBoost, _opts.Style, _opts.Speed), ct);
        if (mp3 is null) return false;

        PreparedVoiceNote prep;
        try { prep = await _evo.PrepareVoiceNoteAsync(mp3, ct); }
        catch (Exception ex) { _log.LogWarning(ex, "VoiceNote: falló la conversión a OGG (lead {Lead})", lead.Id); return false; }

        // Persistir ANTES de mandar (regla anti auto-mute del takeover): el registro
        // guarda el guion para que la IA y la UI conserven el contexto de qué se dijo.
        var msg = new ConversationMessage
        {
            Id = Guid.NewGuid(),
            LeadId = lead.Id,
            SellerId = lead.SellerId,
            Direction = MessageDirection.Outbound,
            Status = MessageDeliveryStatus.Sent,
            Text = AudioPrefix + script,
            EvolutionInstance = instance.InstanceName,
            Timestamp = DateTimeOffset.UtcNow,
            IsRead = true,
        };
        _db.ConversationMessages.Add(msg);
        lead.AiSuggestedReply = null;
        lead.AiSuggestedReplyAt = null;
        lead.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);

        // "Grabando audio…" un ratito antes de mandar (humano) y el envío real.
        var jid = $"{lead.WhatsappPhone}@s.whatsapp.net";
        var presence = Math.Clamp(prep.DurationSeconds, 2, 12);
        try
        {
            await _evo.SetPresenceRecordingAsync(instance.InstanceName, jid, presence, ct);
            await Task.Delay(TimeSpan.FromSeconds(presence), ct);
        }
        catch { /* la presencia es cosmética */ }

        var ok = await _evo.SendPreparedVoiceNoteAsync(instance.InstanceName, lead.WhatsappPhone!, prep.OggBytes, ct);
        if (!ok)
        {
            msg.Status = MessageDeliveryStatus.Failed;
            await _db.SaveChangesAsync(ct);
            return false; // el caller cae al texto normal
        }

        _log.LogInformation("VoiceNote enviada a lead {Lead} (trigger {Trigger}, {Dur}s)", lead.Id, trigger, prep.DurationSeconds);
        return true;
    }

    private static Trigger DetectTrigger(
        IReadOnlyList<ConversationMessage> thread, string lastInbound, string reply, LeadIntent intent)
    {
        // Espejo: el último mensaje del lead fue nota de voz (transcripta con 🎤 o cruda).
        if (lastInbound.StartsWith(AudioPrefix) || lastInbound.StartsWith("[audio"))
            return Trigger.Mirror;

        // Despedida en buenos términos (venta ganada o saludo de cierre del lead).
        if (intent == LeadIntent.Won || FarewellRx.IsMatch(lastInbound))
            return Trigger.Farewell;

        // Momento decisivo: precio/planes/cuenta/demo en lo que preguntó o en lo que respondemos.
        if (intent == LeadIntent.Interested || DecisiveRx.IsMatch(lastInbound) || DecisiveRx.IsMatch(reply))
            return Trigger.Decisive;

        return Trigger.None;
    }

    /// <summary>Contexto real del producto para el guion (precios/planes/features verdaderos).</summary>
    private static string? BuildProductFacts(Product product)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(product.DisplayName)) parts.Add($"App: {product.DisplayName}");
        if (!string.IsNullOrWhiteSpace(product.PriceDisplay)) parts.Add($"Precio: {product.PriceDisplay}");
        if (!string.IsNullOrWhiteSpace(product.AiSalesPlaybook)) parts.Add($"Playbook (planes/features reales):\n{product.AiSalesPlaybook}");
        return parts.Count == 0 ? null : string.Join("\n", parts);
    }

    private async Task<string?> BuildScriptAsync(string chatReply, Trigger trigger, string? productFacts, CancellationToken ct)
    {
        var farewell = trigger == Trigger.Farewell
            ? " Es una DESPEDIDA: cerrá el guion con \"un abrazo\" (literal, al final)."
            : " Cerrá con una afirmación corta que baja (ej: \"cualquier cosa me escribís por acá, dale.\") — NUNCA termines con una pregunta.";
        var system =
            "Reescribís mensajes de chat de WhatsApp como GUIONES de nota de voz para un TTS con la voz clonada de Eric " +
            "(vendedor argentino, es-AR, voseo). El TTS arma la entonación desde la puntuación, así que el guion define cómo suena. " +
            "REGLAS OBLIGATORIAS:\n" +
            "- Puntuación completa: mayúsculas, tildes, comas, puntos, signos ¿? completos.\n" +
            "- Voseo SIEMPRE con tilde final: mirá, escuchá, decime, pasame, fijate, entrá, probá, avisame.\n" +
            "- Escribí \"llama/llamar\" como \"shama/shamar\" (yeísmo argentino; SOLO en esas palabras, el resto normal).\n" +
            "- Montos en plata: \"treinta lucas\" en vez de \"30.000 pesos\" (número en palabras + lucas).\n" +
            "- Usá 1 o 2 conectores de Eric donde suenen naturales: \"nada,\", \"obviamente\", \"así que nada\", \"tranqui\", " +
            "\"justamente\", \"viste\", \"la verdad\", \"o sea\", \"che\".\n" +
            "- Micro-pausas con puntos suspensivos (\"...\") en las dudas naturales. Sin emojis, sin markdown.\n" +
            "- Evitá cadenas de plurales con S seguidas (\"alumnos ni de clases\" → reformulá) y la frase \"lo vemos\" al final.\n" +
            "- Las preguntas van al MEDIO del guion, nunca al final." + farewell + "\n" +
            "- Si el tema es PRECIO o PLANES: decí los números CONCRETOS y pasá por TODOS los planes con lo que " +
            "incluye cada uno (features), como un vendedor que se lo sabe de memoria — nada de \"tenemos varios planes\" " +
            "a secas. Usá EXCLUSIVAMENTE los datos reales del producto que van abajo; si un número no está ahí, no lo digas. " +
            $"En ese caso podés estirarte hasta {_opts.MaxScriptWords * 2} palabras.\n" +
            $"- Si NO es de precio: máximo {_opts.MaxScriptWords} palabras (audio corto de WhatsApp).\n" +
            "Devolvé SOLO el guion, sin comillas ni explicación.";
        var user = $"Mensaje de chat a convertir en guion de audio:\n«{chatReply}»" +
                   (string.IsNullOrWhiteSpace(productFacts) ? "" : $"\n\nDATOS REALES DEL PRODUCTO:\n{productFacts}");
        var script = await _claude.CompleteAsync(system, user, "voicenote", ct);
        if (string.IsNullOrWhiteSpace(script)) return null;
        script = script!.Trim().Trim('"', '«', '»');
        // Guarda dura: si el modelo se fue de largo, no mandamos un audio eterno.
        var words = script.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length > _opts.MaxScriptWords * 3) return null;
        return script;
    }

    /// <summary>Envío de PRUEBA end-to-end a un número arbitrario (panel de test, no toca leads).</summary>
    public async Task<(bool ok, string? script, string? error)> SendTestAsync(
        string instanceName, string phone, string chatReply, bool farewell, string? productKey, CancellationToken ct)
    {
        if (!_tts.IsConfigured) return (false, null, "ElevenLabs sin API key");
        string? facts = null;
        if (!string.IsNullOrWhiteSpace(productKey))
        {
            var product = await _db.Products.AsNoTracking().FirstOrDefaultAsync(p => p.ProductKey == productKey, ct);
            if (product is not null) facts = BuildProductFacts(product);
        }
        var script = await BuildScriptAsync(chatReply, farewell ? Trigger.Farewell : Trigger.Decisive, facts, ct);
        if (string.IsNullOrWhiteSpace(script)) return (false, null, "No se pudo generar el guion");
        var mp3 = await _tts.SynthesizeAsync(script!, _opts.VoiceId,
            new ElevenLabsClient.TtsVoiceSettings(_opts.Stability, _opts.SimilarityBoost, _opts.Style, _opts.Speed), ct);
        if (mp3 is null) return (false, script, "Falló la síntesis de ElevenLabs");
        var prep = await _evo.PrepareVoiceNoteAsync(mp3, ct);
        var ok = await _evo.SendPreparedVoiceNoteAsync(instanceName, phone, prep.OggBytes, ct);
        return (ok, script, ok ? null : "Evolution rechazó el envío");
    }
}
