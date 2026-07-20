using System.Text.Json;
using SalesHub.Infrastructure.Services;

namespace SalesHub.Infrastructure.Evolution;

/// <summary>
/// Parsea un mensaje de Evolution (el "data" del webhook messages.upsert, o un record de
/// POST /chat/findMessages — misma forma: key + message + messageTimestamp + pushName) a
/// un <see cref="ConversationService.IncomingMessage"/>. Compartido entre el webhook en
/// vivo y el sync periódico de chats.
/// </summary>
public static class EvolutionMessageParser
{
    public static ConversationService.IncomingMessage? Parse(
        string instance, JsonElement msg, string? topSender, bool fromSync = false)
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
            instance, remoteJid, phoneOverride, messageId, text!, timestamp, msg.GetRawText(), fromMe,
            topSender, fromSync);
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
