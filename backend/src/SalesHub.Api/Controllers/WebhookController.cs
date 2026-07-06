using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using SalesHub.Infrastructure.Services;
using SalesHub.Infrastructure.Services.Social;

namespace SalesHub.Api.Controllers;

/// <summary>
/// Receives Evolution API webhooks. Evolution POSTs JSON with events like "messages.upsert".
/// We parse the inbound messages and hand them to ConversationService.
/// </summary>
[ApiController]
[Route("api/webhooks")]
public class WebhookController : ControllerBase
{
    private readonly ConversationService _conv;
    private readonly AudioTranscriptionRelay _relay;
    private readonly InspirationIntakeRelay _inspiration;
    private readonly ILogger<WebhookController> _log;

    public WebhookController(ConversationService conv, AudioTranscriptionRelay relay, InspirationIntakeRelay inspiration, ILogger<WebhookController> log)
    {
        _conv = conv; _relay = relay; _inspiration = inspiration; _log = log;
    }

    [HttpPost("evolution")]
    public async Task<IActionResult> Evolution([FromBody] JsonElement payload, CancellationToken ct)
    {
        try
        {
            var eventName = payload.TryGetProperty("event", out var ev) ? ev.GetString() : null;
            if (eventName is not ("messages.upsert" or "MESSAGES_UPSERT"))
                return Ok(new { skipped = true, @event = eventName });

            // Diag temporal: dump payload para debuggear LIDs / IDs raros.
            try { await System.IO.File.WriteAllTextAsync("/tmp/saleshub-last-webhook.json", payload.GetRawText(), ct); }
            catch { /* no-op */ }

            var instance = payload.TryGetProperty("instance", out var i) ? i.GetString() : null;
            if (instance is null) return Ok(new { skipped = true, reason = "no instance" });

            if (!payload.TryGetProperty("data", out var data)) return Ok(new { skipped = true });

            // El payload top-level trae "sender" con el JID real del cliente
            // (xxxxx@s.whatsapp.net) incluso cuando data.key.remoteJid viene
            // como "xxxxx@lid". Lo usamos como fallback para resolver el
            // teléfono real.
            var topSender = payload.TryGetProperty("sender", out var sEl) && sEl.ValueKind == JsonValueKind.String
                ? sEl.GetString() : null;

            // Evolution may deliver single message or array under data.messages/data.
            var messages = new List<JsonElement>();
            if (data.ValueKind == JsonValueKind.Array) messages.AddRange(data.EnumerateArray());
            else if (data.TryGetProperty("messages", out var arr) && arr.ValueKind == JsonValueKind.Array) messages.AddRange(arr.EnumerateArray());
            else messages.Add(data);

            int handled = 0;
            foreach (var msg in messages)
            {
                var incoming = ParseMessage(instance, msg, topSender);
                if (incoming is null) continue;
                // Relay de transcripción: si es una nota de voz de un número de la allowlist,
                // la transcribimos y respondemos el texto — sin pasarla al flujo de leads.
                // Incluye los self-messages (fromMe): mandarte un audio a vos mismo es válido.
                // Intake maestro (inspiraciones / PDF ruteado): corre ANTES que el relay de
                // transcripción para que el batch no se robe las imágenes del número maestro.
                // Los audios sueltos del maestro los deja pasar (siguen a transcripción).
                if (await _inspiration.TryHandleAsync(incoming, ct)) { handled++; continue; }
                if (await _relay.TryHandleAsync(incoming, ct)) { handled++; continue; }
                // El flujo normal de leads IGNORA los fromMe (nuestros propios envíos salientes);
                // sólo los relays los usan.
                if (incoming.FromMe) continue;
                if (await _conv.HandleIncomingAsync(incoming, ct)) handled++;
            }
            return Ok(new { handled });
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Webhook processing failed");
            return Ok(new { error = ex.Message });
        }
    }

    private static ConversationService.IncomingMessage? ParseMessage(string instance, JsonElement msg, string? topSender)
    {
        // fromMe: mensaje propio/saliente. NO lo descartamos acá — el relay de transcripción lo
        // necesita (self-chat: te mandás un audio a vos mismo). El flujo de leads lo ignora aparte.
        bool fromMe = false;
        if (msg.TryGetProperty("key", out var key))
        {
            if (key.TryGetProperty("fromMe", out var fm) && fm.ValueKind == JsonValueKind.True) fromMe = true;
        }
        else return null;

        string? remoteJid = key.TryGetProperty("remoteJid", out var rj) ? rj.GetString() : null;
        string? messageId = key.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
        if (remoteJid is null) return null;
        // Skip groups.
        if (remoteJid.EndsWith("@g.us", StringComparison.Ordinal)) return null;

        // WhatsApp introdujo Linked IDs (xxx@lid) que NO son teléfonos reales.
        // El número verdadero viene en otros campos. Probamos varias rutas
        // y al final caemos al payload.sender top-level (que en Evolution
        // v2.2.3 trae el JID real con teléfono incluso para chats LID).
        string? phoneOverride = null;
        if (remoteJid.EndsWith("@lid", StringComparison.Ordinal))
        {
            phoneOverride = TryGetString(key, "senderPn")
                ?? TryGetString(key, "participantPn")
                ?? TryGetString(key, "remoteJidAlt")
                ?? TryGetString(msg, "senderPn")
                ?? TryGetString(msg, "participantPn")
                ?? topSender;
            if (phoneOverride is not null)
            {
                var at = phoneOverride.IndexOf('@');
                if (at >= 0) phoneOverride = phoneOverride[..at];
            }
        }

        // Texto del mensaje. Si es un tipo sin texto (audio, sticker, etc.) generamos
        // un placeholder por tipo — así la respuesta del lead igual se captura y se
        // ve en Conversaciones. En AR la mayoría responde con nota de voz: descartar
        // los no-texto perdía casi todas las respuestas.
        string? text = null;
        if (msg.TryGetProperty("message", out var body))
        {
            if (body.TryGetProperty("conversation", out var conv) && conv.ValueKind == JsonValueKind.String)
                text = conv.GetString();
            else if (body.TryGetProperty("extendedTextMessage", out var ext) && ext.TryGetProperty("text", out var extText))
                text = extText.GetString();
            else if (body.TryGetProperty("imageMessage", out var img))
                text = Caption(img) ?? "[imagen]";
            else if (body.TryGetProperty("videoMessage", out var vid))
                text = Caption(vid) ?? "[video]";
            else if (body.TryGetProperty("audioMessage", out _))
                text = "[audio]";
            else if (body.TryGetProperty("documentMessage", out var doc))
                text = doc.TryGetProperty("fileName", out var fn) && fn.ValueKind == JsonValueKind.String
                    ? $"[documento: {fn.GetString()}]" : "[documento]";
            else if (body.TryGetProperty("stickerMessage", out _))
                text = "[sticker]";
            else if (body.TryGetProperty("locationMessage", out _))
                text = "[ubicación]";
            else if (body.TryGetProperty("contactMessage", out _) || body.TryGetProperty("contactsArrayMessage", out _))
                text = "[contacto]";
        }
        text ??= msg.TryGetProperty("messageText", out var mt) ? mt.GetString() : null;
        // Eventos sin contenido reconocible (reactions, edits, protocol messages) → skip.
        if (string.IsNullOrWhiteSpace(text)) return null;

        long ts = 0;
        if (msg.TryGetProperty("messageTimestamp", out var tsEl) && tsEl.ValueKind == JsonValueKind.Number) ts = tsEl.GetInt64();
        var timestamp = ts > 0 ? DateTimeOffset.FromUnixTimeSeconds(ts) : DateTimeOffset.UtcNow;

        return new ConversationService.IncomingMessage(
            instance, remoteJid, phoneOverride, messageId, text!, timestamp, msg.GetRawText(), fromMe);
    }

    private static string? TryGetString(JsonElement el, string prop) =>
        el.ValueKind == JsonValueKind.Object
            && el.TryGetProperty(prop, out var v)
            && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;

    /// <summary>Caption de un imageMessage/videoMessage, o null si vacío/ausente.</summary>
    private static string? Caption(JsonElement media)
    {
        var c = TryGetString(media, "caption");
        return string.IsNullOrWhiteSpace(c) ? null : c;
    }
}
