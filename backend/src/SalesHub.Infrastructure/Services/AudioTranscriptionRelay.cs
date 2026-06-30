using System.Collections.Concurrent;
using System.Text.Json;
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
///
/// Tiene dos modos (config en <c>transcription_settings</c>):
///  - Clásico: cada nota de voz → su transcripción de texto.
///  - Batch (<c>BatchMode</c>): junta imágenes + texto + audios que el número manda
///    seguido (debounce de <c>BatchWindowSeconds</c>) y responde un único PDF.
/// </summary>
public class AudioTranscriptionRelay
{
    private readonly ApplicationDbContext _db;
    private readonly IEvolutionClient _evo;
    private readonly GroqWhisperClient _whisper;
    private readonly TranscriptionBatchAccumulator _batch;
    private readonly ILogger<AudioTranscriptionRelay> _log;

    private static readonly Regex NonDigit = new(@"\D", RegexOptions.Compiled);

    // Dedup en memoria de los últimos message IDs procesados, por si Evolution
    // reentrega el mismo webhook (evita responder/encolar dos veces). Es single-user:
    // no necesita persistencia. Entradas viejas se limpian solas.
    private static readonly ConcurrentDictionary<string, DateTimeOffset> Recent = new();
    private static readonly TimeSpan RecentTtl = TimeSpan.FromMinutes(10);

    // Cache de config (settings + allowlist) para no pegarle a la DB en cada mensaje
    // del webhook (la línea recibe TODO el tráfico de leads). Se invalida desde el
    // controller al guardar cambios; igual expira sola por TTL.
    private sealed record ConfigSnapshot(
        bool Enabled, string? InstanceName, bool BatchMode, int WindowSeconds, HashSet<string> AllowedSuffixes);
    private sealed record CacheEntry(ConfigSnapshot Snap, DateTimeOffset Until);
    private static volatile CacheEntry? _cache;
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(15);

    public AudioTranscriptionRelay(
        ApplicationDbContext db, IEvolutionClient evo, GroqWhisperClient whisper,
        TranscriptionBatchAccumulator batch, ILogger<AudioTranscriptionRelay> log)
    {
        _db = db; _evo = evo; _whisper = whisper; _batch = batch; _log = log;
    }

    /// <summary>Fuerza recargar la config en el próximo mensaje (llamar al guardar cambios).</summary>
    public static void InvalidateCache() => _cache = null;

    /// <summary>
    /// Devuelve true si el mensaje era de un número autorizado en la línea configurada y lo
    /// procesamos (el webhook NO debe pasarlo al flujo normal de leads). Devuelve false para
    /// cualquier otro mensaje (sigue el flujo normal).
    /// </summary>
    public async Task<bool> TryHandleAsync(ConversationService.IncomingMessage incoming, CancellationToken ct)
    {
        var cfg = await GetConfigAsync(ct);
        if (!cfg.Enabled) return false;
        // Sin línea elegida → apagado (fail-safe). Y sólo la línea configurada (ej. GymHero):
        // así un mensaje que llega a cualquier otra línea no se toca.
        if (string.IsNullOrWhiteSpace(cfg.InstanceName)) return false;
        if (!string.Equals(incoming.InstanceName, cfg.InstanceName, StringComparison.OrdinalIgnoreCase))
            return false;

        // Qué tipo de mensaje es (lo leemos del JSON crudo, no del placeholder).
        var (kind, caption) = Classify(incoming.RawJson);

        if (!cfg.BatchMode)
        {
            // Modo clásico: sólo notas de voz. Cualquier otra cosa sigue el flujo normal.
            if (kind != BatchItemKind.Audio) return false;
        }
        else
        {
            // Modo batch: imágenes, texto y audio. Otros tipos (sticker, ubicación, video…)
            // no los tocamos — siguen el flujo normal.
            if (kind is null) return false;
        }

        // Doble filtro: además de la línea, el remitente tiene que estar en la allowlist.
        // Crítico — la línea recibe leads reales; sin esto les procesaríamos sus mensajes.
        var phone = NormalizeDigits(incoming.FromPhone ?? ExtractPhone(incoming.FromJid));
        if (phone is null || phone.Length < 6) return false;
        if (!cfg.AllowedSuffixes.Contains(Suffix(phone))) return false;

        // A partir de acá el mensaje es nuestro: lo interceptamos sí o sí (return true).
        if (incoming.MessageId is not null)
        {
            CleanupRecent();
            if (!Recent.TryAdd(incoming.MessageId, incoming.Timestamp))
                return true; // webhook duplicado: ya lo tomamos.
        }

        if (cfg.BatchMode)
        {
            var item = kind!.Value switch
            {
                BatchItemKind.Text => new BatchItem(BatchItemKind.Text, incoming.Text, null),
                BatchItemKind.Image => new BatchItem(BatchItemKind.Image, caption, incoming.RawJson),
                _ => new BatchItem(BatchItemKind.Audio, null, incoming.RawJson),
            };
            _batch.Enqueue(cfg.InstanceName!, phone, item, cfg.WindowSeconds);
            return true;
        }

        await ProcessAsync(incoming, phone, ct);
        return true;
    }

    private async Task<ConfigSnapshot> GetConfigAsync(CancellationToken ct)
    {
        var entry = _cache;
        if (entry is not null && DateTimeOffset.UtcNow < entry.Until) return entry.Snap;

        var settings = await _db.TranscriptionSettings.AsNoTracking().FirstOrDefaultAsync(ct);
        ConfigSnapshot snap;
        if (settings is null)
        {
            snap = new ConfigSnapshot(false, null, false, 10, new HashSet<string>());
        }
        else
        {
            var phones = await _db.TranscriptionPhones.AsNoTracking().Select(p => p.Phone).ToListAsync(ct);
            // Match tolerante por los últimos 8 dígitos (el abonado): los números pueden venir
            // con/sin 54, el 9 móvil, +, separadores.
            var suffixes = phones.Select(s => Suffix(NonDigit.Replace(s, ""))).ToHashSet();
            snap = new ConfigSnapshot(
                settings.Enabled, settings.InstanceName, settings.BatchMode,
                settings.BatchWindowSeconds < 1 ? 10 : settings.BatchWindowSeconds, suffixes);
        }

        _cache = new CacheEntry(snap, DateTimeOffset.UtcNow + CacheTtl);
        return snap;
    }

    /// <summary>Clasifica el mensaje a partir del JSON crudo. Devuelve el tipo (o null si no
    /// nos interesa) y, para imágenes, el caption (puede ser null).</summary>
    private static (BatchItemKind? Kind, string? Caption) Classify(string rawJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(rawJson);
            if (!doc.RootElement.TryGetProperty("message", out var body) || body.ValueKind != JsonValueKind.Object)
                return (null, null);

            if (body.TryGetProperty("imageMessage", out var img))
                return (BatchItemKind.Image, CaptionOf(img));
            if (body.TryGetProperty("audioMessage", out _))
                return (BatchItemKind.Audio, null);
            if (body.TryGetProperty("documentMessage", out var docMsg))
            {
                // Sólo documentos que sean imágenes (foto enviada como archivo) entran al PDF.
                var mime = StringProp(docMsg, "mimetype");
                if (mime is not null && mime.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
                    return (BatchItemKind.Image, CaptionOf(docMsg));
                return (null, null);
            }
            if (body.TryGetProperty("conversation", out var conv) && conv.ValueKind == JsonValueKind.String)
                return (BatchItemKind.Text, null);
            if (body.TryGetProperty("extendedTextMessage", out var ext)
                && ext.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String)
                return (BatchItemKind.Text, null);

            return (null, null);
        }
        catch
        {
            return (null, null);
        }
    }

    private static string? CaptionOf(JsonElement media)
    {
        var c = StringProp(media, "caption");
        return string.IsNullOrWhiteSpace(c) ? null : c;
    }

    private static string? StringProp(JsonElement el, string prop) =>
        el.ValueKind == JsonValueKind.Object && el.TryGetProperty(prop, out var v)
            && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

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
