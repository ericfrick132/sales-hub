using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SalesHub.Core.Abstractions;
using SalesHub.Infrastructure.Persistence;

namespace SalesHub.Infrastructure.Services;

/// <summary>
/// Relay personal de transcripción: cuando un número de la allowlist
/// (<see cref="TranscriptionPhones"/>) manda una nota de voz al WhatsApp del bot,
/// la bajamos, la transcribimos con Whisper (Groq) y le respondemos el texto por
/// WhatsApp. NO crea lead ni toca el inbox de Conversaciones — es una utilidad aparte.
///
/// Se engancha en el webhook ANTES del flujo normal: si interceptamos el mensaje,
/// el webhook no lo pasa a <see cref="ConversationService"/>.
/// On/off global vía RuntimeFlag key "transcription" (default OFF).
/// </summary>
public class AudioTranscriptionRelay
{
    private readonly ApplicationDbContext _db;
    private readonly IEvolutionClient _evo;
    private readonly GroqWhisperClient _whisper;
    private readonly ILogger<AudioTranscriptionRelay> _log;

    private static readonly Regex NonDigit = new(@"\D", RegexOptions.Compiled);

    // Dedup en memoria de los últimos message IDs procesados, por si Evolution
    // reentrega el mismo webhook (evita responder dos veces). Es single-user: no
    // necesita persistencia. Entradas viejas se limpian solas.
    private static readonly ConcurrentDictionary<string, DateTimeOffset> Recent = new();
    private static readonly TimeSpan RecentTtl = TimeSpan.FromMinutes(10);

    public AudioTranscriptionRelay(
        ApplicationDbContext db, IEvolutionClient evo, GroqWhisperClient whisper,
        ILogger<AudioTranscriptionRelay> log)
    {
        _db = db; _evo = evo; _whisper = whisper; _log = log;
    }

    /// <summary>
    /// Devuelve true si el mensaje era una nota de voz de un número autorizado y lo
    /// procesamos (el webhook NO debe pasarlo al flujo normal de leads). Devuelve
    /// false para cualquier otro mensaje (sigue el flujo normal).
    /// </summary>
    public async Task<bool> TryHandleAsync(ConversationService.IncomingMessage incoming, CancellationToken ct)
    {
        // El webhook deja "[audio]" como placeholder de las notas de voz. Sólo nos
        // interesan esas; todo lo demás cae sin tocar la DB.
        if (incoming.Text != "[audio]") return false;

        var settings = await _db.TranscriptionSettings.AsNoTracking().FirstOrDefaultAsync(ct);
        if (settings is null || !settings.Enabled) return false;
        // Sin línea elegida → apagado (fail-safe). Y sólo la línea configurada
        // (ej. GymHero): así un audio que llega a cualquier otra línea no se toca.
        if (string.IsNullOrWhiteSpace(settings.InstanceName)) return false;
        if (!string.Equals(incoming.InstanceName, settings.InstanceName, StringComparison.OrdinalIgnoreCase))
            return false;

        // Doble filtro: además de la línea, el remitente tiene que estar en la allowlist.
        // Crítico — la línea recibe leads reales; sin esto les transcribiríamos su audio.
        var phone = NormalizeDigits(incoming.FromPhone ?? ExtractPhone(incoming.FromJid));
        if (phone is null || phone.Length < 6) return false;

        if (!await IsAllowedAsync(phone, ct)) return false;

        // A partir de acá el mensaje es nuestro: lo interceptamos sí o sí (return true),
        // aunque la transcripción falle, para que no caiga al flujo de leads.
        if (incoming.MessageId is not null)
        {
            CleanupRecent();
            if (!Recent.TryAdd(incoming.MessageId, incoming.Timestamp))
                return true; // webhook duplicado: ya lo respondimos.
        }

        await ProcessAsync(incoming, phone, ct);
        return true;
    }

    private async Task<bool> IsAllowedAsync(string phone, CancellationToken ct)
    {
        // Match tolerante por los últimos 8 dígitos (el abonado), igual que el matcheo
        // de leads: los números pueden venir con/sin 54, el 9 móvil, +, separadores.
        var suffix = Suffix(phone);
        var stored = await _db.TranscriptionPhones.AsNoTracking()
            .Select(p => p.Phone).ToListAsync(ct);
        return stored.Any(s => Suffix(NonDigit.Replace(s, "")) == suffix);
    }

    private async Task ProcessAsync(ConversationService.IncomingMessage incoming, string replyTo, CancellationToken ct)
    {
        var instance = incoming.InstanceName;

        string? transcript = null;
        if (_whisper.IsConfigured)
        {
            var audio = await _evo.GetMediaBase64Async(instance, incoming.RawJson, ct);
            if (audio is not null)
                transcript = await _whisper.TranscribeAsync(audio, "voice.ogg", ct);
            else
                _log.LogWarning("Relay: no se pudo bajar el media del audio (instance={I})", instance);
        }
        else
        {
            _log.LogWarning("Relay: Groq sin API key — no se puede transcribir");
        }

        var reply = !string.IsNullOrWhiteSpace(transcript)
            ? transcript!
            : "No pude transcribir ese audio 😕. Probá reenviarlo de nuevo.";

        var ok = await _evo.SendTextAsync(instance, replyTo, reply, ct);
        _log.LogInformation("Relay transcripción: instance={I} to={Phone} transcrito={T} enviado={Ok}",
            instance, replyTo, transcript is not null, ok);
    }

    private static string? NormalizeDigits(string? phone)
    {
        if (string.IsNullOrWhiteSpace(phone)) return null;
        var digits = NonDigit.Replace(phone, "");
        return string.IsNullOrWhiteSpace(digits) ? null : digits;
    }

    private static string Suffix(string digits) => digits.Length >= 8 ? digits[^8..] : digits;

    private static string? ExtractPhone(string? jid)
    {
        if (string.IsNullOrWhiteSpace(jid)) return null;
        var at = jid.IndexOf('@');
        return at > 0 ? jid[..at] : jid;
    }

    private static void CleanupRecent()
    {
        if (Recent.Count < 64) return; // sólo barremos si creció
        var cutoff = DateTimeOffset.UtcNow - RecentTtl;
        foreach (var kv in Recent)
            if (kv.Value < cutoff) Recent.TryRemove(kv.Key, out _);
    }
}
