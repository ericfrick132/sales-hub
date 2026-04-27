using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using SalesHub.Infrastructure.Services;

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
    private readonly ILogger<WebhookController> _log;

    public WebhookController(ConversationService conv, ILogger<WebhookController> log)
    {
        _conv = conv; _log = log;
    }

    [HttpPost("evolution")]
    public async Task<IActionResult> Evolution([FromBody] JsonElement payload, CancellationToken ct)
    {
        try
        {
            var eventName = payload.TryGetProperty("event", out var ev) ? ev.GetString() : null;
            if (eventName is not ("messages.upsert" or "MESSAGES_UPSERT"))
                return Ok(new { skipped = true, @event = eventName });

            var instance = payload.TryGetProperty("instance", out var i) ? i.GetString() : null;
            if (instance is null) return Ok(new { skipped = true, reason = "no instance" });

            if (!payload.TryGetProperty("data", out var data)) return Ok(new { skipped = true });

            // Evolution may deliver single message or array under data.messages/data.
            var messages = new List<JsonElement>();
            if (data.ValueKind == JsonValueKind.Array) messages.AddRange(data.EnumerateArray());
            else if (data.TryGetProperty("messages", out var arr) && arr.ValueKind == JsonValueKind.Array) messages.AddRange(arr.EnumerateArray());
            else messages.Add(data);

            int handled = 0;
            foreach (var msg in messages)
            {
                var incoming = ParseMessage(instance, msg);
                if (incoming is null) continue;
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

    private static ConversationService.IncomingMessage? ParseMessage(string instance, JsonElement msg)
    {
        // Only inbound messages (fromMe=false).
        if (msg.TryGetProperty("key", out var key))
        {
            if (key.TryGetProperty("fromMe", out var fromMe) && fromMe.ValueKind == JsonValueKind.True) return null;
        }
        else return null;

        string? remoteJid = key.TryGetProperty("remoteJid", out var rj) ? rj.GetString() : null;
        string? messageId = key.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
        if (remoteJid is null) return null;
        // Skip groups.
        if (remoteJid.EndsWith("@g.us", StringComparison.Ordinal)) return null;

        string? text = null;
        if (msg.TryGetProperty("message", out var body))
        {
            if (body.TryGetProperty("conversation", out var conv) && conv.ValueKind == JsonValueKind.String) text = conv.GetString();
            else if (body.TryGetProperty("extendedTextMessage", out var ext) && ext.TryGetProperty("text", out var extText)) text = extText.GetString();
            else if (body.TryGetProperty("imageMessage", out var img) && img.TryGetProperty("caption", out var cap)) text = cap.GetString();
        }
        text ??= msg.TryGetProperty("messageText", out var mt) ? mt.GetString() : null;
        if (string.IsNullOrWhiteSpace(text)) return null;

        long ts = 0;
        if (msg.TryGetProperty("messageTimestamp", out var tsEl) && tsEl.ValueKind == JsonValueKind.Number) ts = tsEl.GetInt64();
        var timestamp = ts > 0 ? DateTimeOffset.FromUnixTimeSeconds(ts) : DateTimeOffset.UtcNow;

        return new ConversationService.IncomingMessage(
            instance, remoteJid, null, messageId, text!, timestamp, msg.GetRawText());
    }
}
