using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SalesHub.Core.Abstractions;
using SalesHub.Core.Domain.Entities;
using SalesHub.Infrastructure.Persistence;

namespace SalesHub.Infrastructure.Services;

/// <summary>
/// Tareas para Claude Code por WhatsApp, solo en el SELF-CHAT de la línea del relay de transcripción:
///  - Nota de voz grabada (no reenviada): se transcribe y se le muestra a Eric ("¿Lo mando?"); recién con su
///    "sí" se encola en <see cref="ClaudeTaskService"/> y la Mac la corre. "no" la descarta; otro audio la reemplaza.
///  - Citar un mensaje de una tarea (los del bot llevan #código) o su audio sigue ESE thread: un audio citando
///    pide confirmación y retoma la sesión de esa tarea; un texto escrito citando va directo (no hay transcripción
///    que verificar); "sí"/"no" citando una confirmación decide esa tarea puntual.
///  - Imagen: se adjunta a la tarea que espera el "sí"; con caption y sin tarea esperando, el caption es el
///    pedido (pide confirmación). Una imagen suelta sin caption sigue la cadena (inspiración / PDF).
/// Los audios reenviados al self-chat siguen yendo a transcripción (<see cref="AudioTranscriptionRelay"/>).
///
/// Doble filtro, como el relay de transcripción: la línea tiene que ser la de <c>transcription_settings</c>
/// y el dueño tiene que estar en la allowlist. Crítico: las líneas de los vendedores también tienen
/// self-chat, y esto termina ejecutando código en la Mac de Eric. Kill-switch: flag "claude_tasks".
/// </summary>
public class ClaudeTaskRelay
{
    private static readonly Regex NonDigit = new(@"\D", RegexOptions.Compiled);
    // "seguí …" / "Claude, seguí …" sin citar nada retoma la sesión de la última tarea.
    private static readonly Regex ResumeCue = new(@"^\s*(claude[\s,.:]*)?segu[ií]", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    // Código de la tarea en los mensajes del bot: "🤖 #a3f9c1 …".
    private static readonly Regex TaskCode = new(@"^\s*🤖\s*#([0-9a-f]{6})\b", RegexOptions.Compiled);

    // Respuesta a "¿Lo mando?". Mensaje entero, para no comerse un texto que solo arranca con "ok".
    private static readonly Regex YesAnswer = new(@"^\s*(s[ií]+|dale|ok(a|ey)?|mand[aá](la|lo)?|va|listo|👍)[\s.!]*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex NoAnswer = new(@"^\s*(no+|nop|cancel[aá](la|lo)?|cancelar|👎)[\s.!]*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Dedup de webhooks reentregados (como en AudioTranscriptionRelay).
    private static readonly ConcurrentDictionary<string, DateTimeOffset> Recent = new();

    private readonly ApplicationDbContext _db;
    private readonly IEvolutionClient _evo;
    private readonly GroqWhisperClient _whisper;
    private readonly ClaudeTaskService _tasks;
    private readonly ILogger<ClaudeTaskRelay> _log;

    public ClaudeTaskRelay(ApplicationDbContext db, IEvolutionClient evo, GroqWhisperClient whisper,
        ClaudeTaskService tasks, ILogger<ClaudeTaskRelay> log)
    {
        _db = db; _evo = evo; _whisper = whisper; _tasks = tasks; _log = log;
    }

    private sealed record Parsed(bool Voice, bool Image, string? Mime, string? Caption, bool Forwarded, string? QuotedId, string? QuotedCode);

    public async Task<bool> TryHandleAsync(ConversationService.IncomingMessage incoming, CancellationToken ct)
    {
        // Filtros baratos primero: la línea recibe todo el tráfico de leads.
        if (!incoming.FromMe || incoming.FromSync || !IsSelfChat(incoming)) return false;
        var msg = Parse(incoming.RawJson);
        // Audio reenviado = transcripción; una captura reenviada sí sirve como imagen de la tarea.
        if (msg is null || (msg.Forwarded && !msg.Image)) return false;
        var text = (incoming.Text ?? "").Trim();
        var isText = !msg.Voice && !msg.Image;
        // Nuestros avisos (🤖) también llegan como fromMe en el self-chat.
        if (isText && (text.Length == 0 || text.StartsWith(ClaudeTaskService.Prefix.Trim(), StringComparison.Ordinal))) return false;
        var answer = isText ? ParseAnswer(text) : null;
        var quotes = msg.QuotedId is not null || msg.QuotedCode is not null;
        // Texto suelto que no es sí/no ni cita nada: no es nuestro.
        if (isText && answer is null && !quotes) return false;

        if (!await _db.IsFlagOnAsync("claude_tasks", true, ct)) return false;
        var line = await _db.TranscriptionSettings.AsNoTracking().Select(s => s.InstanceName).FirstOrDefaultAsync(ct);
        if (string.IsNullOrWhiteSpace(line) || !string.Equals(line, incoming.InstanceName, StringComparison.OrdinalIgnoreCase))
            return false;
        var owner = Digits(incoming.SenderJid);
        var allowed = await _db.TranscriptionPhones.AsNoTracking().Select(p => p.Phone).ToListAsync(ct);
        if (owner.Length < 8 || !allowed.Any(p => Suffix(Digits(p)) == Suffix(owner))) return false;

        var quoted = quotes ? await _tasks.FindQuotedAsync(owner, msg.QuotedCode, msg.QuotedId, ct) : null;
        // Un texto que cita algo que no es una tarea (otra nota del self-chat) no es nuestro.
        if (isText && answer is null && quoted is null) return false;

        // Imagen: se adjunta a la tarea que espera el "sí" (la citada o la última). Sin tarea esperando, solo
        // si trae caption (es el pedido); una imagen suelta sigue la cadena (inspiración / PDF).
        ClaudeTask? waiting = null;
        if (msg.Image)
        {
            waiting = await _tasks.FindWaitingAsync(owner, quoted is { Status: "confirm" } ? quoted : null, ct);
            if (waiting is null && string.IsNullOrWhiteSpace(msg.Caption)) return false;
        }

        if (incoming.MessageId is not null)
        {
            var cutoff = DateTimeOffset.UtcNow.AddMinutes(-10);
            foreach (var kv in Recent) if (kv.Value < cutoff) Recent.TryRemove(kv.Key, out _);
            if (!Recent.TryAdd(incoming.MessageId, DateTimeOffset.UtcNow)) return true;
        }

        if (answer is not null)
        {
            if (await _tasks.ConfirmAsync(owner, answer.Value, quoted, ct)) return true;
            if (quoted is null) return false; // "ok" suelto sin nada esperando: sigue la cadena
            // "dale"/"no" citando una tarea ya terminada: se toma como instrucción para ese thread.
        }

        if (msg.Image)
        {
            var bytes = await _evo.GetMediaBase64Async(incoming.InstanceName, incoming.RawJson, ct);
            if (bytes is null)
            {
                await _evo.SendTextAsync(incoming.InstanceName, owner,
                    ClaudeTaskService.Prefix + "No pude bajar la imagen, mandala de nuevo.", ct);
                return true;
            }
            waiting ??= await _tasks.CreateAsync(incoming.InstanceName, owner, msg.Caption!.Trim(), incoming.MessageId,
                quoted, resumeLast: false, confirmed: false, ct);
            await _tasks.AttachAsync(waiting, msg.Mime ?? "image/jpeg", bytes, ct);
            return true;
        }

        if (isText)
        {
            await _tasks.CreateAsync(incoming.InstanceName, owner, text, null, quoted, false, confirmed: true, ct);
            return true;
        }

        string? transcript = null;
        if (_whisper.IsConfigured)
        {
            var audio = await _evo.GetMediaBase64Async(incoming.InstanceName, incoming.RawJson, ct);
            if (audio is not null) transcript = await _whisper.TranscribeAsync(audio, "voice.ogg", ct);
        }
        if (string.IsNullOrWhiteSpace(transcript))
        {
            _log.LogWarning("ClaudeTask: no se pudo transcribir el audio (instance={I})", incoming.InstanceName);
            await _evo.SendTextAsync(incoming.InstanceName, owner,
                ClaudeTaskService.Prefix + "No pude transcribir el audio, no armé ninguna tarea. Probá de nuevo.", ct);
            return true;
        }

        var task = await _tasks.CreateAsync(incoming.InstanceName, owner, transcript.Trim(), incoming.MessageId, quoted,
            resumeLast: quoted is null && ResumeCue.IsMatch(transcript), confirmed: false, ct);
        _log.LogInformation("ClaudeTask {Id} esperando confirmación (instance={I}, thread={P})", task.Id, incoming.InstanceName, task.ParentTaskId);
        return true;
    }

    /// <summary>true = sí, false = no, null = otra cosa.</summary>
    private static bool? ParseAnswer(string text)
    {
        if (YesAnswer.IsMatch(text)) return true;
        if (NoAnswer.IsMatch(text)) return false;
        return null;
    }

    private static bool IsSelfChat(ConversationService.IncomingMessage incoming)
    {
        var owner = Digits(incoming.SenderJid);
        var chat = Digits(incoming.FromJid);
        return owner.Length >= 8 && chat.Length >= 8 && owner[^8..] == chat[^8..];
    }

    /// <summary>
    /// Tipo de mensaje y cita (contextInfo del cuerpo: stanzaId = id del mensaje citado, quotedMessage = su
    /// contenido, de donde sale el #código si citó un mensaje del bot). Reenviados (isForwarded /
    /// forwardingScore) quedan marcados: no son tareas.
    /// </summary>
    private static Parsed? Parse(string? rawJson)
    {
        if (string.IsNullOrWhiteSpace(rawJson)) return null;
        try
        {
            using var doc = JsonDocument.Parse(rawJson);
            if (!doc.RootElement.TryGetProperty("message", out var m) || m.ValueKind != JsonValueKind.Object) return null;

            var voice = m.TryGetProperty("audioMessage", out var audio) && audio.ValueKind == JsonValueKind.Object;
            // Imagen: imageMessage, o una foto mandada como archivo (documentMessage con mime de imagen).
            JsonElement? media = m.TryGetProperty("imageMessage", out var img) && img.ValueKind == JsonValueKind.Object ? img
                : m.TryGetProperty("documentMessage", out var docMsg) && docMsg.ValueKind == JsonValueKind.Object
                  && (Str(docMsg, "mimetype") ?? "").StartsWith("image/", StringComparison.OrdinalIgnoreCase) ? docMsg : null;
            string? mime = media is { } me ? Str(me, "mimetype")?.Split(';')[0].Trim() : null;
            string? caption = media is { } mc ? Str(mc, "caption") : null;
            JsonElement? ctx = null;
            foreach (var body in m.EnumerateObject())
                if (body.Value.ValueKind == JsonValueKind.Object
                    && body.Value.TryGetProperty("contextInfo", out var c) && c.ValueKind == JsonValueKind.Object)
                { ctx = c; break; }
            if (ctx is null && doc.RootElement.TryGetProperty("contextInfo", out var top) && top.ValueKind == JsonValueKind.Object)
                ctx = top;

            bool forwarded = false;
            string? quotedId = null, quotedCode = null;
            if (ctx is { } x)
            {
                forwarded = (x.TryGetProperty("isForwarded", out var f) && f.ValueKind == JsonValueKind.True)
                    || (x.TryGetProperty("forwardingScore", out var s) && s.ValueKind == JsonValueKind.Number && s.GetInt32() > 0);
                if (x.TryGetProperty("stanzaId", out var sid) && sid.ValueKind == JsonValueKind.String) quotedId = sid.GetString();
                if (x.TryGetProperty("quotedMessage", out var q) && q.ValueKind == JsonValueKind.Object)
                {
                    var qt = q.TryGetProperty("conversation", out var conv) && conv.ValueKind == JsonValueKind.String ? conv.GetString()
                        : q.TryGetProperty("extendedTextMessage", out var ext) && ext.TryGetProperty("text", out var t)
                          && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
                    var code = qt is null ? null : TaskCode.Match(qt);
                    if (code is { Success: true }) quotedCode = code.Groups[1].Value;
                }
            }
            return new Parsed(voice, media is not null, mime, caption, forwarded, quotedId, quotedCode);
        }
        catch
        {
            return null;
        }
    }

    private static string? Str(JsonElement el, string prop) =>
        el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static string Digits(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var at = s.IndexOf('@');
        return NonDigit.Replace(at > 0 ? s[..at] : s, "");
    }

    private static string Suffix(string digits) => digits.Length >= 8 ? digits[^8..] : digits;
}
