using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using SalesHub.Infrastructure.Evolution;
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
    private readonly VoiceCalibrationRelay _calibration;
    private readonly ColdOpenAudioRelay _coldOpen;
    private readonly SalesHub.Api.AdminMenu.AdminMenuRelay _adminMenu;
    private readonly ILogger<WebhookController> _log;

    public WebhookController(ConversationService conv, AudioTranscriptionRelay relay, InspirationIntakeRelay inspiration,
        VoiceCalibrationRelay calibration, ColdOpenAudioRelay coldOpen, SalesHub.Api.AdminMenu.AdminMenuRelay adminMenu,
        ILogger<WebhookController> log)
    {
        _conv = conv; _relay = relay; _inspiration = inspiration; _calibration = calibration; _coldOpen = coldOpen;
        _adminMenu = adminMenu; _log = log;
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
                var incoming = EvolutionMessageParser.Parse(instance, msg, topSender);
                if (incoming is null) continue;
                // Relay de transcripción: si es una nota de voz de un número de la allowlist,
                // la transcribimos y respondemos el texto — sin pasarla al flujo de leads.
                // Incluye los self-messages (fromMe): mandarte un audio a vos mismo es válido.
                // Intake maestro (inspiraciones / PDF ruteado): corre ANTES que el relay de
                // transcripción para que el batch no se robe las imágenes del número maestro.
                // Los audios sueltos del maestro los deja pasar (siguen a transcripción).
                // Calibración de voz (self-chat, comando "calibrar"): corre PRIMERO — solo toma
                // mensajes del propio dueño en su self-chat durante una sesión activa.
                // Cold-open: colecta los audios del pitch por producto (bot-iniciado o
                // "grabar openers"). Corre antes que todo para consumir sus notas de voz.
                if (await _coldOpen.TryHandleAsync(incoming, ct)) { handled++; continue; }
                if (await _calibration.TryHandleAsync(incoming, ct)) { handled++; continue; }
                // Bot de config del maestro: consume "menu"/"config" y todo mientras esté en modo menú
                // (los números NO caen en inspiración). Devuelve false para el resto → sigue la cadena.
                if (await _adminMenu.TryHandleAsync(incoming, ct)) { handled++; continue; }
                if (await _inspiration.TryHandleAsync(incoming, ct)) { handled++; continue; }
                if (await _relay.TryHandleAsync(incoming, ct)) { handled++; continue; }
                // fromMe = mensaje saliente por la línea. Takeover humano: "-" mutea el bot
                // para ese lead, "+" lo reactiva, y un mensaje manual (no-eco) mutea solo.
                // Los ecos de envíos del propio bot se descartan adentro.
                if (incoming.FromMe) { await _conv.HandleOwnMessageAsync(incoming, ct); continue; }
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

}
