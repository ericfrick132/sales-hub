using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SalesHub.Core.Abstractions;
using SalesHub.Core.Domain.Entities.Social;
using SalesHub.Infrastructure.Persistence;

namespace SalesHub.Infrastructure.Services.Social;

/// <summary>
/// Intake de inspiraciones por WhatsApp: el número MAESTRO (config en
/// <c>inspiration_settings</c>) manda una imagen / texto / link a la línea
/// configurada, la guardamos como <see cref="InspirationItem"/> pendiente y el
/// bot repregunta "¿para qué es?". La siguiente respuesta de texto del maestro
/// etiqueta TODO lo pendiente reciente con el tema (y la app, si la primera
/// palabra matchea un productKey de Posteos).
///
/// Se engancha en el webhook ANTES del flujo de leads, igual que el relay de
/// transcripción. La imagen se persiste al toque (aunque nunca conteste, queda
/// en "Sin clasificar" en la web) — no hay estado volátil que se pueda perder.
///
/// Loop-guard: nuestras respuestas llegan como fromMe cuando el maestro es la
/// propia línea (self-chat); todas arrancan con <see cref="BotPrefix"/> y esos
/// textos se ignoran.
/// </summary>
public class InspirationIntakeRelay
{
    /// <summary>Prefijo de TODA respuesta del bot — corta el loop en self-chat.</summary>
    private const string BotPrefix = "💡";

    /// <summary>Ventana para considerar "pendiente reciente" al etiquetar con la respuesta.</summary>
    private static readonly TimeSpan PendingWindow = TimeSpan.FromMinutes(60);

    /// <summary>Máximo de bytes de imagen que guardamos (WhatsApp comprime a ~2-3 MB igual).</summary>
    private const int MaxImageBytes = 15 * 1024 * 1024;

    private readonly ApplicationDbContext _db;
    private readonly IEvolutionClient _evo;
    private readonly ILogger<InspirationIntakeRelay> _log;

    private static readonly Regex NonDigit = new(@"\D", RegexOptions.Compiled);
    private static readonly Regex UrlRegex = new(@"https?://\S+", RegexOptions.Compiled);

    // Dedup de message IDs por si Evolution reentrega el webhook (mismo patrón
    // que el relay de transcripción; single-user, no necesita persistencia).
    private static readonly ConcurrentDictionary<string, DateTimeOffset> Recent = new();
    private static readonly TimeSpan RecentTtl = TimeSpan.FromMinutes(10);

    // Última vez que preguntamos "¿para qué es?" — para no repetir la pregunta
    // por cada imagen de una ráfaga.
    private static DateTimeOffset _lastAskAt = DateTimeOffset.MinValue;
    private static readonly TimeSpan AskCooldown = TimeSpan.FromMinutes(2);

    // Menú numerado que mandamos con la última pregunta: la respuesta "2" se
    // resuelve contra este snapshot (single-user; si el server reinició en el
    // medio, re-mandamos el menú fresco).
    private static volatile IReadOnlyList<MenuOption>? _lastMenu;
    private sealed record MenuOption(string Topic, string? ProductKey);

    private sealed record ConfigSnapshot(bool Enabled, string? InstanceName, string? MasterSuffix);
    private sealed record CacheEntry(ConfigSnapshot Snap, DateTimeOffset Until);
    private static volatile CacheEntry? _cache;
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(15);

    public InspirationIntakeRelay(ApplicationDbContext db, IEvolutionClient evo, ILogger<InspirationIntakeRelay> log)
    {
        _db = db; _evo = evo; _log = log;
    }

    /// <summary>Fuerza recargar la config en el próximo mensaje (llamar al guardar cambios).</summary>
    public static void InvalidateCache() => _cache = null;

    /// <summary>
    /// true si el mensaje era del número maestro en la línea configurada y lo procesamos
    /// (el webhook NO debe pasarlo al flujo normal). false = sigue el flujo normal.
    /// </summary>
    public async Task<bool> TryHandleAsync(ConversationService.IncomingMessage incoming, CancellationToken ct)
    {
        var cfg = await GetConfigAsync(ct);
        if (!cfg.Enabled || cfg.InstanceName is null || cfg.MasterSuffix is null) return false;
        if (!string.Equals(incoming.InstanceName, cfg.InstanceName, StringComparison.OrdinalIgnoreCase))
            return false;

        // Solo el número maestro. En self-chat (el maestro ES la línea) los mensajes
        // vienen fromMe con remoteJid = el propio número, así que el suffix matchea igual.
        var phone = NormalizeDigits(incoming.FromPhone ?? ExtractPhone(incoming.FromJid));
        if (phone is null || phone.Length < 6) return false;
        if (Suffix(phone) != cfg.MasterSuffix) return false;

        var (kind, caption, mime) = Classify(incoming.RawJson);
        if (kind is null) return false; // audio/sticker/etc: no es nuestro, sigue el flujo normal

        // Loop-guard: nuestra propia respuesta en self-chat llega como texto fromMe con el prefijo.
        if (kind == IntakeKind.Text && incoming.Text.TrimStart().StartsWith(BotPrefix, StringComparison.Ordinal))
            return true; // es nuestro eco: interceptar y no hacer nada

        if (incoming.MessageId is not null)
        {
            CleanupRecent();
            if (!Recent.TryAdd(incoming.MessageId, incoming.Timestamp))
                return true; // webhook duplicado
        }

        switch (kind)
        {
            case IntakeKind.Image:
                await HandleImageAsync(incoming, caption, mime, ct);
                return true;
            case IntakeKind.Text:
                await HandleTextAsync(incoming, ct);
                return true;
            default:
                return false;
        }
    }

    // ── Imagen: guardar ya (pendiente) y preguntar para qué es ──────────────
    private async Task HandleImageAsync(ConversationService.IncomingMessage incoming, string? caption, string? mime, CancellationToken ct)
    {
        var bytes = await _evo.GetMediaBase64Async(incoming.InstanceName, incoming.RawJson, ct);
        if (bytes is null || bytes.Length == 0)
        {
            _log.LogWarning("Inspiración: no se pudo bajar la imagen (instance={I})", incoming.InstanceName);
            await ReplyAsync(incoming, "no pude bajar esa imagen 😕. Probá mandarla de nuevo.", ct);
            return;
        }
        if (bytes.Length > MaxImageBytes)
        {
            await ReplyAsync(incoming, "esa imagen es muy pesada. Mandala como foto normal (no como archivo).", ct);
            return;
        }

        _db.InspirationItems.Add(new InspirationItem
        {
            Id = Guid.NewGuid(),
            Topic = "sin-clasificar",
            Note = string.IsNullOrWhiteSpace(caption) ? null : caption.Trim(),
            MimeType = string.IsNullOrWhiteSpace(mime) ? "image/jpeg" : mime,
            ImageContent = bytes,
            SizeBytes = bytes.Length,
            PendingTopic = true,
        });
        await _db.SaveChangesAsync(ct);

        await AskTopicIfDueAsync(incoming, ct);
    }

    // ── Texto: o es la respuesta al "¿para qué es?", o es una idea/link nueva ─
    private async Task HandleTextAsync(ConversationService.IncomingMessage incoming, CancellationToken ct)
    {
        var text = incoming.Text.Trim();
        if (text.Length == 0) return;

        var since = DateTimeOffset.UtcNow - PendingWindow;
        var pending = await _db.InspirationItems
            .Where(i => i.PendingTopic && i.CreatedAt >= since)
            .OrderBy(i => i.CreatedAt)
            .ToListAsync(ct);

        if (pending.Count > 0)
        {
            if (string.Equals(text, "cancelar", StringComparison.OrdinalIgnoreCase))
            {
                _db.InspirationItems.RemoveRange(pending);
                await _db.SaveChangesAsync(ct);
                await ReplyAsync(incoming, $"listo, descarté {pending.Count} inspiración(es).", ct);
                return;
            }

            string? productKey; string topic;

            // Respuesta numérica → opción del último menú que mandamos.
            if (int.TryParse(text, out var n))
            {
                var menu = _lastMenu;
                if (menu is null || n < 1 || n > menu.Count)
                {
                    // Menú perdido (reinicio) o número fuera de rango: re-mandamos fresco.
                    _lastAskAt = DateTimeOffset.MinValue;
                    await AskTopicIfDueAsync(incoming, ct);
                    return;
                }
                (topic, productKey) = (menu[n - 1].Topic, menu[n - 1].ProductKey);
            }
            else
            {
                (productKey, topic) = await ParseTopicAsync(text, ct);
            }

            foreach (var item in pending)
            {
                item.Topic = topic;
                item.ProductKey = productKey ?? item.ProductKey;
                item.PendingTopic = false;
            }
            await _db.SaveChangesAsync(ct);
            _lastMenu = null;

            var appLabel = productKey is null ? "" : $" ({productKey})";
            await ReplyAsync(incoming,
                $"guardé {pending.Count} inspiración(es) en \"{topic}\"{appLabel} ✅. " +
                "Las uso para los próximos posteos (tema + estilo visual).", ct);
            return;
        }

        // Sin pendientes: es una idea nueva de texto (o un link).
        var url = UrlRegex.Match(text).Value;
        _db.InspirationItems.Add(new InspirationItem
        {
            Id = Guid.NewGuid(),
            Topic = "sin-clasificar",
            Note = text,
            SourceUrl = string.IsNullOrEmpty(url) ? null : url,
            PendingTopic = true,
        });
        await _db.SaveChangesAsync(ct);

        await AskTopicIfDueAsync(incoming, ct);
    }

    /// <summary>
    /// Pregunta "¿para qué es?" con un MENÚ NUMERADO de los temas existentes
    /// (contestás "2" y listo). Texto libre sigue creando un tema nuevo.
    /// Se saltea si ya preguntamos hace poco (ráfaga de imágenes).
    /// </summary>
    private async Task AskTopicIfDueAsync(ConversationService.IncomingMessage incoming, CancellationToken ct)
    {
        if (DateTimeOffset.UtcNow - _lastAskAt < AskCooldown) return;
        _lastAskAt = DateTimeOffset.UtcNow;

        // Temas ya usados, los más recientes primero (hasta 9 para que el menú sea de 1 dígito).
        var topics = await _db.InspirationItems.AsNoTracking()
            .Where(i => !i.PendingTopic && i.Topic != "sin-clasificar")
            .GroupBy(i => new { i.Topic, i.ProductKey })
            .Select(g => new { g.Key.Topic, g.Key.ProductKey, Last = g.Max(x => x.CreatedAt) })
            .OrderByDescending(x => x.Last)
            .Take(9)
            .ToListAsync(ct);

        if (topics.Count == 0)
        {
            _lastMenu = null;
            var apps = await _db.PostingProfiles.AsNoTracking()
                .OrderBy(p => p.ProductKey).Select(p => p.ProductKey).ToListAsync(ct);
            var appsHint = apps.Count > 0 ? $" Si es para una app, arrancá con su nombre ({string.Join("/", apps)})." : "";
            await ReplyAsync(incoming,
                "¡buena! ¿Para qué es esto? Contestame el tema (ej: \"motivación\" o \"gymhero motivación\")." +
                appsHint + " Mandá \"cancelar\" para descartarla.", ct);
            return;
        }

        var menu = topics.Select(t => new MenuOption(t.Topic, t.ProductKey)).ToList();
        _lastMenu = menu;

        var sb = new StringBuilder();
        sb.AppendLine("¡buena! ¿Para qué es? Contestá el número:");
        for (var i = 0; i < menu.Count; i++)
        {
            var app = menu[i].ProductKey is null ? "" : $" ({menu[i].ProductKey})";
            sb.AppendLine($"{i + 1}. {menu[i].Topic}{app}");
        }
        sb.Append("O escribí un tema nuevo (ej: \"gymhero motivación\"). \"cancelar\" descarta.");
        await ReplyAsync(incoming, sb.ToString(), ct);
    }

    /// <summary>
    /// "gymhero motivación" → (gymhero, "motivación"); "humor padel" → (null, "humor padel").
    /// La primera palabra sólo se toma como app si matchea un productKey real.
    /// </summary>
    private async Task<(string? ProductKey, string Topic)> ParseTopicAsync(string text, CancellationToken ct)
    {
        var normalized = text.Trim();
        var firstSpace = normalized.IndexOf(' ');
        var firstWord = (firstSpace < 0 ? normalized : normalized[..firstSpace]).Trim().ToLowerInvariant();

        var productKey = await _db.PostingProfiles.AsNoTracking()
            .Where(p => p.ProductKey.ToLower() == firstWord)
            .Select(p => p.ProductKey)
            .FirstOrDefaultAsync(ct);

        if (productKey is null) return (null, Truncate(normalized, 128));

        var rest = firstSpace < 0 ? "" : normalized[(firstSpace + 1)..].Trim();
        return (productKey, Truncate(rest.Length == 0 ? "general" : rest, 128));
    }

    private Task ReplyAsync(ConversationService.IncomingMessage incoming, string text, CancellationToken ct)
        => _evo.SendTextAsync(incoming.InstanceName, incoming.FromJid, $"{BotPrefix} {text}", ct);

    private async Task<ConfigSnapshot> GetConfigAsync(CancellationToken ct)
    {
        var entry = _cache;
        if (entry is not null && DateTimeOffset.UtcNow < entry.Until) return entry.Snap;

        var s = await _db.InspirationSettings.AsNoTracking().FirstOrDefaultAsync(ct);
        var masterDigits = s?.MasterPhone is null ? null : NormalizeDigits(s.MasterPhone);
        var snap = new ConfigSnapshot(
            s?.Enabled == true,
            s?.InstanceName,
            masterDigits is null || masterDigits.Length < 6 ? null : Suffix(masterDigits));

        _cache = new CacheEntry(snap, DateTimeOffset.UtcNow + CacheTtl);
        return snap;
    }

    private enum IntakeKind { Image, Text }

    /// <summary>Clasifica desde el JSON crudo: imagen (o doc imagen), texto, o nada nuestro.</summary>
    private static (IntakeKind? Kind, string? Caption, string? Mime) Classify(string rawJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(rawJson);
            if (!doc.RootElement.TryGetProperty("message", out var body) || body.ValueKind != JsonValueKind.Object)
                return (null, null, null);

            if (body.TryGetProperty("imageMessage", out var img))
                return (IntakeKind.Image, StringProp(img, "caption"), StringProp(img, "mimetype"));
            if (body.TryGetProperty("documentMessage", out var docMsg))
            {
                var mime = StringProp(docMsg, "mimetype");
                if (mime is not null && mime.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
                    return (IntakeKind.Image, StringProp(docMsg, "caption"), mime);
                return (null, null, null);
            }
            if (body.TryGetProperty("conversation", out var conv) && conv.ValueKind == JsonValueKind.String)
                return (IntakeKind.Text, null, null);
            if (body.TryGetProperty("extendedTextMessage", out var ext)
                && ext.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String)
                return (IntakeKind.Text, null, null);

            return (null, null, null);
        }
        catch
        {
            return (null, null, null);
        }
    }

    private static string? StringProp(JsonElement el, string prop) =>
        el.ValueKind == JsonValueKind.Object && el.TryGetProperty(prop, out var v)
            && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static string Truncate(string s, int n) => s.Length <= n ? s : s[..n];

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
        if (Recent.Count < 64) return;
        var cutoff = DateTimeOffset.UtcNow - RecentTtl;
        foreach (var kv in Recent)
            if (kv.Value < cutoff) Recent.TryRemove(kv.Key, out _);
    }
}
