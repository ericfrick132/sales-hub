using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace SalesHub.Infrastructure.Instagram;

/// <summary>
/// Cliente de la Graph API para los DMs de las cuentas propias de IG (Messenger Platform para
/// Instagram): manda texto, trae el perfil del que escribió y baja adjuntos (audios). Ver
/// <see cref="InstagramMessagingOptions"/>.
/// </summary>
public class InstagramMessengerClient
{
    /// <summary>
    /// Prefijo con el que se guardan en <c>conversation_messages.evolution_instance</c> los mensajes
    /// de IG: <c>ig:{igUserId}</c>. Así el hilo sabe por qué cuenta entró y por cuál contestar.
    /// </summary>
    public const string LinePrefix = "ig:";

    // Instagram corta en 1000 bytes UTF-8 por mensaje de texto.
    private const int MaxTextBytes = 1000;

    // Tokens de página derivados del token de system user (typed client = transient → estático).
    private static readonly ConcurrentDictionary<string, string> PageTokens = new();

    private readonly HttpClient _http;
    private readonly InstagramMessagingOptions _opt;
    private readonly ILogger<InstagramMessengerClient> _log;

    public InstagramMessengerClient(HttpClient http, IOptions<InstagramMessagingOptions> opt, ILogger<InstagramMessengerClient> log)
    {
        _http = http; _opt = opt.Value; _log = log;
        _http.Timeout = TimeSpan.FromSeconds(20);
    }

    public InstagramMessagingOptions Options => _opt;

    public static string LineName(string igUserId) => LinePrefix + igUserId;

    /// <summary>Id de la cuenta IG de un nombre de línea "ig:…", o null si no es de IG.</summary>
    public static string? AccountFromLine(string? lineName) =>
        lineName is not null && lineName.StartsWith(LinePrefix, StringComparison.Ordinal) ? lineName[LinePrefix.Length..] : null;

    public InstagramMessagingOptions.Account? FindAccount(string? igUserId) =>
        string.IsNullOrWhiteSpace(igUserId) ? null
            : _opt.Accounts.FirstOrDefault(a => a.IgUserId == igUserId || (a.PageId.Length > 0 && a.PageId == igUserId));

    /// <summary>¿El id es una de nuestras cuentas (IG o página)? Sirve para descartar lo propio.</summary>
    public bool IsOwnAccount(string? id) => FindAccount(id) is not null;

    private string Graph(string path) => $"https://graph.facebook.com/{_opt.GraphVersion}/{path}";

    private async Task<string?> PageTokenAsync(InstagramMessagingOptions.Account acc, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(acc.PageAccessToken)) return acc.PageAccessToken;
        if (string.IsNullOrWhiteSpace(_opt.SystemUserToken) || string.IsNullOrWhiteSpace(acc.PageId)) return null;
        if (PageTokens.TryGetValue(acc.PageId, out var cached)) return cached;
        try
        {
            using var res = await _http.GetAsync(Graph($"{acc.PageId}?fields=access_token&access_token={Uri.EscapeDataString(_opt.SystemUserToken)}"), ct);
            var body = await res.Content.ReadAsStringAsync(ct);
            if (!res.IsSuccessStatusCode)
            {
                _log.LogWarning("IG messaging: no pude obtener el token de la página {Page}: {Status} {Body}", acc.PageId, (int)res.StatusCode, Trunc(body));
                return null;
            }
            using var doc = JsonDocument.Parse(body);
            var tok = doc.RootElement.TryGetProperty("access_token", out var t) ? t.GetString() : null;
            if (!string.IsNullOrWhiteSpace(tok)) PageTokens[acc.PageId] = tok!;
            return tok;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex, "IG messaging: falló el pedido del token de la página {Page}", acc.PageId);
            return null;
        }
    }

    /// <summary>
    /// Manda un texto por DM al IGSID desde la cuenta <paramref name="igUserId"/>
    /// (<c>POST /{page-id}/messages</c>). La ventana de 24 h la chequea quien llama.
    /// </summary>
    public async Task<bool> SendTextAsync(string igUserId, string recipientIgsid, string text, CancellationToken ct)
    {
        var acc = FindAccount(igUserId);
        if (acc is null) { _log.LogWarning("IG messaging: cuenta {Ig} no configurada — no se manda", igUserId); return false; }
        var token = await PageTokenAsync(acc, ct);
        if (token is null) { _log.LogWarning("IG messaging: sin token para la cuenta @{User} — no se manda", acc.Username ?? acc.IgUserId); return false; }

        var sender = string.IsNullOrWhiteSpace(acc.PageId) ? acc.IgUserId : acc.PageId;
        var ok = true;
        foreach (var chunk in SplitByBytes(text.Trim(), MaxTextBytes))
        {
            var payload = new
            {
                recipient = new { id = recipientIgsid },
                messaging_type = "RESPONSE",
                message = new { text = chunk },
            };
            try
            {
                using var res = await _http.PostAsJsonAsync(Graph($"{sender}/messages?access_token={Uri.EscapeDataString(token)}"), payload, ct);
                if (!res.IsSuccessStatusCode)
                {
                    var body = await res.Content.ReadAsStringAsync(ct);
                    _log.LogWarning("IG messaging: envío a {Igsid} por @{User} falló: {Status} {Body}",
                        recipientIgsid, acc.Username ?? acc.IgUserId, (int)res.StatusCode, Trunc(body));
                    // Token vencido/revocado: que el próximo envío lo vuelva a pedir.
                    if ((int)res.StatusCode is 400 or 401 && body.Contains("OAuthException", StringComparison.Ordinal)
                        && string.IsNullOrWhiteSpace(acc.PageAccessToken))
                        PageTokens.TryRemove(acc.PageId, out _);
                    ok = false;
                    break;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogWarning(ex, "IG messaging: envío a {Igsid} falló", recipientIgsid);
                ok = false;
                break;
            }
        }
        return ok;
    }

    /// <summary>Nombre y usuario del que escribió (User Profile API). Best-effort: (null, null) si falla.</summary>
    public async Task<(string? Username, string? Name)> GetProfileAsync(string igUserId, string igsid, CancellationToken ct)
    {
        var acc = FindAccount(igUserId);
        var token = acc is null ? null : await PageTokenAsync(acc, ct);
        if (token is null) return (null, null);
        try
        {
            using var res = await _http.GetAsync(Graph($"{Uri.EscapeDataString(igsid)}?fields=name,username&access_token={Uri.EscapeDataString(token)}"), ct);
            if (!res.IsSuccessStatusCode) return (null, null);
            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
            string? Get(string k) => doc.RootElement.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(v.GetString()) ? v.GetString()!.Trim() : null;
            return (Get("username"), Get("name"));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogDebug(ex, "IG messaging: no pude traer el perfil de {Igsid}", igsid);
            return (null, null);
        }
    }

    /// <summary>
    /// Baja el primer adjunto de audio de un evento de messaging guardado en RawJson
    /// (message.attachments[].payload.url, URL de CDN firmada). Null si no hay o falla.
    /// </summary>
    public async Task<byte[]?> DownloadAudioAsync(string rawJson, CancellationToken ct)
    {
        try
        {
            using var doc = JsonDocument.Parse(rawJson);
            if (!doc.RootElement.TryGetProperty("message", out var msg)
                || !msg.TryGetProperty("attachments", out var atts) || atts.ValueKind != JsonValueKind.Array) return null;
            foreach (var a in atts.EnumerateArray())
            {
                if (!a.TryGetProperty("type", out var type) || type.GetString() != "audio") continue;
                if (!a.TryGetProperty("payload", out var p) || !p.TryGetProperty("url", out var u)) continue;
                var url = u.GetString();
                if (string.IsNullOrWhiteSpace(url)) continue;
                return await _http.GetByteArrayAsync(url, ct);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex, "IG messaging: no pude bajar el audio");
        }
        return null;
    }

    /// <summary>Corta el texto en partes de hasta <paramref name="maxBytes"/> bytes UTF-8, por espacios.</summary>
    internal static IEnumerable<string> SplitByBytes(string text, int maxBytes)
    {
        if (Encoding.UTF8.GetByteCount(text) <= maxBytes) { yield return text; yield break; }
        var sb = new StringBuilder();
        foreach (var word in text.Split(' '))
        {
            var candidate = sb.Length == 0 ? word : sb + " " + word;
            if (Encoding.UTF8.GetByteCount(candidate) > maxBytes && sb.Length > 0)
            {
                yield return sb.ToString();
                sb.Clear().Append(word);
            }
            else
            {
                sb.Clear().Append(candidate);
            }
        }
        if (sb.Length > 0) yield return sb.ToString();
    }

    private static string Trunc(string s) => s.Length > 400 ? s[..400] : s;
}
