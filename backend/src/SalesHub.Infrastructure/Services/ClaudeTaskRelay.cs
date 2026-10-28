using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SalesHub.Core.Abstractions;
using SalesHub.Infrastructure.Persistence;

namespace SalesHub.Infrastructure.Services;

/// <summary>
/// Tareas para Claude Code por audio: una nota de voz grabada (no reenviada) en el SELF-CHAT de la línea
/// del relay de transcripción se transcribe y se le muestra a Eric ("¿Lo mando?"); recién con su "sí" se
/// encola en <see cref="ClaudeTaskService"/> y la Mac la corre. "no" la descarta; otro audio la reemplaza.
/// Los audios reenviados al self-chat siguen yendo a transcripción (<see cref="AudioTranscriptionRelay"/>).
///
/// Doble filtro, como el relay de transcripción: la línea tiene que ser la de <c>transcription_settings</c>
/// y el dueño tiene que estar en la allowlist. Crítico: las líneas de los vendedores también tienen
/// self-chat, y esto termina ejecutando código en la Mac de Eric. Kill-switch: flag "claude_tasks".
/// </summary>
public class ClaudeTaskRelay
{
    private static readonly Regex NonDigit = new(@"\D", RegexOptions.Compiled);
    // "seguí …" / "Claude, seguí …" retoma la sesión de la última tarea.
    private static readonly Regex ResumeCue = new(@"^\s*(claude[\s,.:]*)?segu[ií]", RegexOptions.IgnoreCase | RegexOptions.Compiled);

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

    public async Task<bool> TryHandleAsync(ConversationService.IncomingMessage incoming, CancellationToken ct)
    {
        // Filtros baratos primero: la línea recibe todo el tráfico de leads.
        if (!incoming.FromMe || incoming.FromSync || !IsSelfChat(incoming)) return false;
        var voice = IsOwnVoiceNote(incoming.RawJson);
        // Texto: solo un sí/no a la confirmación. Nuestros avisos (🤖) también llegan como fromMe.
        var answer = voice ? null : ParseAnswer(incoming.Text);
        if (!voice && answer is null) return false;

        if (!await _db.IsFlagOnAsync("claude_tasks", true, ct)) return false;
        var line = await _db.TranscriptionSettings.AsNoTracking().Select(s => s.InstanceName).FirstOrDefaultAsync(ct);
        if (string.IsNullOrWhiteSpace(line) || !string.Equals(line, incoming.InstanceName, StringComparison.OrdinalIgnoreCase))
            return false;
        var owner = Digits(incoming.SenderJid);
        var allowed = await _db.TranscriptionPhones.AsNoTracking().Select(p => p.Phone).ToListAsync(ct);
        if (owner.Length < 8 || !allowed.Any(p => Suffix(Digits(p)) == Suffix(owner))) return false;

        if (incoming.MessageId is not null)
        {
            var cutoff = DateTimeOffset.UtcNow.AddMinutes(-10);
            foreach (var kv in Recent) if (kv.Value < cutoff) Recent.TryRemove(kv.Key, out _);
            if (!Recent.TryAdd(incoming.MessageId, DateTimeOffset.UtcNow)) return true;
        }

        if (answer is not null) return await _tasks.ConfirmAsync(owner, answer.Value, ct);

        string? text = null;
        if (_whisper.IsConfigured)
        {
            var audio = await _evo.GetMediaBase64Async(incoming.InstanceName, incoming.RawJson, ct);
            if (audio is not null) text = await _whisper.TranscribeAsync(audio, "voice.ogg", ct);
        }
        if (string.IsNullOrWhiteSpace(text))
        {
            _log.LogWarning("ClaudeTask: no se pudo transcribir el audio (instance={I})", incoming.InstanceName);
            await _evo.SendTextAsync(incoming.InstanceName, owner,
                ClaudeTaskService.Prefix + "No pude transcribir el audio, no armé ninguna tarea. Probá de nuevo.", ct);
            return true;
        }

        var task = await _tasks.CreateAsync(incoming.InstanceName, owner, text.Trim(), ResumeCue.IsMatch(text), ct);
        _log.LogInformation("ClaudeTask {Id} encolada (instance={I}, mac={Online})", task.Id, incoming.InstanceName, ClaudeTaskService.MacOnline);
        return true;
    }

    /// <summary>true = sí, false = no, null = otra cosa.</summary>
    private static bool? ParseAnswer(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
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

    /// <summary>Nota de voz/audio grabado acá: los reenviados (isForwarded / forwardingScore) no son tareas.</summary>
    private static bool IsOwnVoiceNote(string? rawJson)
    {
        if (string.IsNullOrWhiteSpace(rawJson)) return false;
        try
        {
            using var doc = JsonDocument.Parse(rawJson);
            if (!doc.RootElement.TryGetProperty("message", out var m) || m.ValueKind != JsonValueKind.Object
                || !m.TryGetProperty("audioMessage", out var audio) || audio.ValueKind != JsonValueKind.Object)
                return false;
            if (audio.TryGetProperty("contextInfo", out var ctx) && ctx.ValueKind == JsonValueKind.Object)
            {
                if (ctx.TryGetProperty("isForwarded", out var f) && f.ValueKind == JsonValueKind.True) return false;
                if (ctx.TryGetProperty("forwardingScore", out var s) && s.ValueKind == JsonValueKind.Number && s.GetInt32() > 0) return false;
            }
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string Digits(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var at = s.IndexOf('@');
        return NonDigit.Replace(at > 0 ? s[..at] : s, "");
    }

    private static string Suffix(string digits) => digits.Length >= 8 ? digits[^8..] : digits;
}
