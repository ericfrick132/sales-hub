using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using SalesHub.Infrastructure.Instagram;
using SalesHub.Infrastructure.Services;

namespace SalesHub.Api.Controllers;

/// <summary>
/// Webhook oficial de Meta para los DMs de las cuentas propias de Instagram (Messenger Platform
/// para IG; object=instagram, campo messages). Cada DM entra al mismo motor que los chats de
/// WhatsApp de los anuncios (<see cref="ConversationService.HandleInstagramDmAsync"/>) y la
/// respuesta del bot sale por la Graph API. Ecos (is_echo): los del sistema se ignoran; los
/// escritos a mano desde la app de IG mutean el bot (takeover).
/// Config: sección <c>InstagramMessaging</c> (ver <see cref="InstagramMessagingOptions"/>).
/// Siempre devuelve 200 en el POST: si no, Meta reintenta y termina deshabilitando el webhook.
/// </summary>
[ApiController]
[Route("api/webhooks/meta-instagram")]
[AllowAnonymous]
public class MetaInstagramWebhookController : ControllerBase
{
    private readonly ConversationService _conv;
    private readonly InstagramMessengerClient _ig;
    private readonly InstagramMessagingOptions _opt;
    private readonly IConfiguration _config;
    private readonly ILogger<MetaInstagramWebhookController> _log;

    public MetaInstagramWebhookController(ConversationService conv, InstagramMessengerClient ig,
        IOptions<InstagramMessagingOptions> opt, IConfiguration config, ILogger<MetaInstagramWebhookController> log)
    {
        _conv = conv; _ig = ig; _opt = opt.Value; _config = config; _log = log;
    }

    private string? VerifyToken => string.IsNullOrWhiteSpace(_opt.VerifyToken) ? _config["Meta:VerifyToken"] : _opt.VerifyToken;
    private string? AppSecret => string.IsNullOrWhiteSpace(_opt.AppSecret) ? _config["Meta:AppSecret"] : _opt.AppSecret;

    /// <summary>Verificación del webhook (GET con hub.challenge al configurar la suscripción).</summary>
    [HttpGet]
    public IActionResult Verify()
    {
        var mode = Request.Query["hub.mode"].ToString();
        var token = Request.Query["hub.verify_token"].ToString();
        var challenge = Request.Query["hub.challenge"].ToString();
        var expected = VerifyToken;
        if (mode == "subscribe" && !string.IsNullOrEmpty(expected) && token == expected)
            return Content(challenge, "text/plain");
        _log.LogWarning("IG webhook verify rechazado (mode={Mode})", mode);
        return StatusCode(403);
    }

    [HttpPost]
    public async Task<IActionResult> Receive(CancellationToken ct)
    {
        string body;
        using (var reader = new StreamReader(Request.Body, Encoding.UTF8))
            body = await reader.ReadToEndAsync(ct);

        var secret = AppSecret;
        if (!string.IsNullOrEmpty(secret) && !ValidSignature(body, secret))
        {
            _log.LogWarning("IG webhook: firma X-Hub-Signature-256 inválida — ignorado");
            return Ok();
        }
        if (!_opt.Enabled) return Ok(new { skipped = "disabled" });

        JsonDocument doc;
        try { doc = JsonDocument.Parse(body); }
        catch (Exception ex) { _log.LogWarning(ex, "IG webhook: body no es JSON"); return Ok(); }

        var handled = 0;
        using (doc)
        {
            var root = doc.RootElement;
            if (Str(root, "object") != "instagram") return Ok(new { skipped = "object" });
            if (!root.TryGetProperty("entry", out var entries) || entries.ValueKind != JsonValueKind.Array) return Ok();

            foreach (var entry in entries.EnumerateArray())
            {
                var entryId = Str(entry, "id");
                // Formato normal: entry.messaging[]. Los "Test" del panel de la app mandan entry.changes[].value.
                var events = new List<JsonElement>();
                if (entry.TryGetProperty("messaging", out var messaging) && messaging.ValueKind == JsonValueKind.Array)
                    events.AddRange(messaging.EnumerateArray());
                if (entry.TryGetProperty("changes", out var changes) && changes.ValueKind == JsonValueKind.Array)
                    foreach (var ch in changes.EnumerateArray())
                        if (Str(ch, "field") == "messages" && ch.TryGetProperty("value", out var v) && v.ValueKind == JsonValueKind.Object)
                            events.Add(v);

                foreach (var ev in events)
                {
                    try { if (await HandleEventAsync(entryId, ev, ct)) handled++; }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        _log.LogError(ex, "IG webhook: falló el evento de la cuenta {Entry}", entryId);
                    }
                }
            }
        }
        return Ok(new { handled });
    }

    private async Task<bool> HandleEventAsync(string? entryId, JsonElement ev, CancellationToken ct)
    {
        var senderId = ev.TryGetProperty("sender", out var s) ? Str(s, "id") : null;
        var recipientId = ev.TryGetProperty("recipient", out var r) ? Str(r, "id") : null;
        if (senderId is null || recipientId is null) return false;
        var ts = ev.TryGetProperty("timestamp", out var tsEl) && tsEl.TryGetInt64(out var ms)
            ? DateTimeOffset.FromUnixTimeMilliseconds(ms) : DateTimeOffset.UtcNow;
        // Un timestamp del futuro (reloj de Meta adelantado) rompería la ventana de 24 h y los "nuevos".
        if (ts > DateTimeOffset.UtcNow) ts = DateTimeOffset.UtcNow;

        var hasMessage = ev.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.Object;
        var isEcho = hasMessage && message.TryGetProperty("is_echo", out var e) && e.ValueKind == JsonValueKind.True;

        if (isEcho)
        {
            // Eco: sender = nuestra cuenta, recipient = el contacto.
            if (!_ig.IsOwnAccount(senderId) || _ig.IsOwnAccount(recipientId)) return false;
            var echoText = MessageText(message);
            if (echoText is null) return false;
            return await _conv.HandleInstagramEchoAsync(new ConversationService.InstagramDm(
                _ig.FindAccount(senderId)!.IgUserId, recipientId, Str(message, "mid"), echoText, ts, ev.GetRawText()), ct);
        }

        // Lo que manda nuestra propia cuenta (o una cuenta nuestra a otra) no es un lead.
        if (_ig.IsOwnAccount(senderId)) return false;
        var account = _ig.FindAccount(recipientId) ?? _ig.FindAccount(entryId);
        if (account is null)
        {
            _log.LogInformation("IG webhook: evento para una cuenta no configurada (recipient={Rec}, entry={Entry})", recipientId, entryId);
            return false;
        }

        string? text;
        string? mid;
        if (hasMessage)
        {
            if (message.TryGetProperty("is_deleted", out var del) && del.ValueKind == JsonValueKind.True) return false;
            text = MessageText(message);
            mid = Str(message, "mid");
        }
        else if (ev.TryGetProperty("postback", out var pb) && pb.ValueKind == JsonValueKind.Object)
        {
            // Botones (ice breakers / respuestas rápidas): el título es lo que "dijo" el usuario.
            text = Str(pb, "title") ?? Str(pb, "payload");
            mid = Str(pb, "mid");
        }
        else
        {
            return false; // read, reaction, referral sin mensaje, etc.
        }
        if (string.IsNullOrWhiteSpace(text)) return false;

        return await _conv.HandleInstagramDmAsync(new ConversationService.InstagramDm(
            account.IgUserId, senderId, mid, text!.Trim(), ts, ev.GetRawText(), AdFrom(ev, hasMessage ? message : default)), ct);
    }

    /// <summary>Texto del mensaje, o un placeholder por tipo de adjunto (igual que WhatsApp).</summary>
    private static string? MessageText(JsonElement message)
    {
        var text = Str(message, "text");
        if (!string.IsNullOrWhiteSpace(text)) return text;
        if (message.TryGetProperty("is_unsupported", out var un) && un.ValueKind == JsonValueKind.True)
            return "[mensaje no soportado]";
        if (message.TryGetProperty("attachments", out var atts) && atts.ValueKind == JsonValueKind.Array)
        {
            foreach (var a in atts.EnumerateArray())
            {
                return Str(a, "type") switch
                {
                    // "[audio]" exacto: el agente lo transcribe (baja el audio de payload.url del RawJson).
                    "audio" => "[audio]",
                    "image" => "[imagen]",
                    "video" => "[video]",
                    "ig_reel" or "reel" => "[reel]",
                    "share" => "[publicación compartida]",
                    "story_mention" => "[te mencionó en una historia]",
                    "file" => "[documento]",
                    _ => "[adjunto]",
                };
            }
        }
        return null;
    }

    /// <summary>Atribución del anuncio (click-to-Instagram-Direct): referral con ad_id.</summary>
    private static ConversationService.AdReferral? AdFrom(JsonElement ev, JsonElement message)
    {
        JsonElement refEl = default;
        var found = (message.ValueKind == JsonValueKind.Object && message.TryGetProperty("referral", out refEl))
                    || ev.TryGetProperty("referral", out refEl);
        if (!found || refEl.ValueKind != JsonValueKind.Object) return null;
        var adId = Str(refEl, "ad_id");
        if (string.IsNullOrWhiteSpace(adId)) return null;
        string? title = null;
        if (refEl.TryGetProperty("ads_context_data", out var ctx) && ctx.ValueKind == JsonValueKind.Object)
            title = Str(ctx, "ad_title");
        return new ConversationService.AdReferral(adId, title, null, null, null);
    }

    private bool ValidSignature(string body, string appSecret)
    {
        if (!Request.Headers.TryGetValue("X-Hub-Signature-256", out var header)) return false;
        var sig = header.ToString();
        const string prefix = "sha256=";
        if (!sig.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(appSecret));
        var expected = Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(body))).ToLowerInvariant();
        return CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(expected), Encoding.ASCII.GetBytes(sig[prefix.Length..].ToLowerInvariant()));
    }

    private static string? Str(JsonElement el, string prop) =>
        el.ValueKind == JsonValueKind.Object && el.TryGetProperty(prop, out var v)
            ? v.ValueKind switch
            {
                JsonValueKind.String => v.GetString(),
                JsonValueKind.Number => v.GetRawText(),
                _ => null,
            }
            : null;
}
