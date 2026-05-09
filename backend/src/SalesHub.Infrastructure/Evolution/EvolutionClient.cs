using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SalesHub.Core.Abstractions;
using SalesHub.Infrastructure.Options;

namespace SalesHub.Infrastructure.Evolution;

public class EvolutionClient : IEvolutionClient
{
    private readonly HttpClient _http;
    private readonly EvolutionOptions _opts;
    private readonly ILogger<EvolutionClient> _log;

    public EvolutionClient(HttpClient http, IOptions<EvolutionOptions> opts, ILogger<EvolutionClient> log)
    {
        _http = http;
        _opts = opts.Value;
        _log = log;
        _http.BaseAddress = new Uri(_opts.BaseUrl.TrimEnd('/') + "/");
        _http.DefaultRequestHeaders.Remove("apikey");
        _http.DefaultRequestHeaders.Add("apikey", _opts.ApiKey);
        _http.Timeout = TimeSpan.FromSeconds(_opts.TimeoutSeconds);
    }

    public async Task<InstanceConnectionInfo> GetInstanceStatusAsync(string instanceName, CancellationToken ct = default)
    {
        var resp = await _http.GetAsync($"instance/connectionState/{Uri.EscapeDataString(instanceName)}", ct);
        if (resp.StatusCode == HttpStatusCode.NotFound)
            return new InstanceConnectionInfo("not_found", null, null);
        resp.EnsureSuccessStatusCode();
        var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        var state = doc.RootElement.TryGetProperty("instance", out var inst)
            && inst.TryGetProperty("state", out var stEl) ? stEl.GetString() : null;
        return new InstanceConnectionInfo(state ?? "unknown", null, null);
    }

    public async Task<InstanceConnectionInfo> EnsureInstanceAsync(string instanceName, CancellationToken ct = default)
    {
        var status = await GetInstanceStatusAsync(instanceName, ct);
        if (status.Status == "not_found")
        {
            var body = new
            {
                instanceName,
                qrcode = true,
                integration = "WHATSAPP-BAILEYS"
            };
            var resp = await _http.PostAsJsonAsync("instance/create", body, ct);
            if (!resp.IsSuccessStatusCode)
            {
                var text = await resp.Content.ReadAsStringAsync(ct);
                _log.LogWarning("Create instance {Name} failed: {Status} {Body}", instanceName, resp.StatusCode, text);
                resp.EnsureSuccessStatusCode();
            }
            status = new InstanceConnectionInfo("connecting", null, null);
        }
        return status;
    }

    public async Task<string?> GetQrCodeAsync(string instanceName, CancellationToken ct = default)
    {
        var resp = await _http.GetAsync($"instance/connect/{Uri.EscapeDataString(instanceName)}", ct);
        if (!resp.IsSuccessStatusCode) return null;
        var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        if (doc.RootElement.TryGetProperty("base64", out var b64)) return b64.GetString();
        if (doc.RootElement.TryGetProperty("qrcode", out var qr) && qr.TryGetProperty("base64", out var b642)) return b642.GetString();
        return null;
    }

    public async Task LogoutInstanceAsync(string instanceName, CancellationToken ct = default)
    {
        await _http.DeleteAsync($"instance/logout/{Uri.EscapeDataString(instanceName)}", ct);
    }

    public async Task<IReadOnlyList<WhatsappCheckResult>> CheckNumbersAsync(string instanceName, IEnumerable<string> phoneNumbers, CancellationToken ct = default)
    {
        var list = phoneNumbers.Where(s => !string.IsNullOrWhiteSpace(s)).Distinct().ToList();
        if (list.Count == 0) return Array.Empty<WhatsappCheckResult>();
        var resp = await _http.PostAsJsonAsync($"chat/whatsappNumbers/{Uri.EscapeDataString(instanceName)}", new { numbers = list }, ct);
        if (!resp.IsSuccessStatusCode)
        {
            _log.LogWarning("CheckNumbers failed for {Instance}: {Status}", instanceName, resp.StatusCode);
            return list.Select(n => new WhatsappCheckResult(n, false, null)).ToArray();
        }
        var results = await resp.Content.ReadFromJsonAsync<List<CheckResponseItem>>(cancellationToken: ct) ?? new();
        return results.Select(r => new WhatsappCheckResult(r.number ?? "", r.exists ?? false, r.jid)).ToArray();
    }

    public async Task SetPresenceTypingAsync(string instanceName, string jid, int durationSeconds, CancellationToken ct = default)
    {
        try
        {
            await _http.PostAsJsonAsync($"chat/sendPresence/{Uri.EscapeDataString(instanceName)}",
                new { number = jid, delay = durationSeconds * 1000, presence = "composing" }, ct);
        }
        catch (Exception ex) { _log.LogDebug(ex, "Presence error (non-fatal)"); }
    }

    public async Task MarkAllChatsReadAsync(string instanceName, CancellationToken ct = default)
    {
        try
        {
            await _http.PostAsJsonAsync($"chat/markChatUnread/{Uri.EscapeDataString(instanceName)}",
                new { }, ct);
        }
        catch (Exception ex) { _log.LogDebug(ex, "Mark read error (non-fatal)"); }
    }

    public async Task<bool> SendTextAsync(string instanceName, string jid, string message, CancellationToken ct = default)
    {
        var body = new
        {
            number = jid,
            text = message
        };
        var resp = await _http.PostAsJsonAsync($"message/sendText/{Uri.EscapeDataString(instanceName)}", body, ct);
        if (!resp.IsSuccessStatusCode)
        {
            var txt = await resp.Content.ReadAsStringAsync(ct);
            _log.LogWarning("SendText {Instance} -> {Jid} failed: {Status} {Body}", instanceName, jid, resp.StatusCode, txt);
            return false;
        }
        return true;
    }

    public async Task<bool> SendVoiceNoteAsync(string instanceName, string jid, byte[] audio, CancellationToken ct = default)
    {
        // WhatsApp solo reproduce notas de voz si vienen en container OGG con codec
        // Opus (mono, ~16-48kHz). Si dejamos que Evolution se ocupe con encoding=true
        // a veces no convierte y el destinatario ve "no se puede abrir". Lo hacemos
        // server-side con ffmpeg para que sea determinístico (mp3, m4a, wav → ogg/opus).
        byte[] ogg;
        try
        {
            ogg = await ConvertToOggOpusAsync(audio, ct);
            _log.LogInformation("ffmpeg ok: {InBytes}b → {OutBytes}b, signature={Sig}",
                audio.Length, ogg.Length, BytesSignature(ogg));
            // Diagnóstico temporal: dejar el último input/output para inspección.
            try
            {
                await File.WriteAllBytesAsync("/tmp/saleshub-last-input.bin", audio, ct);
                await File.WriteAllBytesAsync("/tmp/saleshub-last-output.ogg", ogg, ct);
            }
            catch { /* no-op */ }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "ffmpeg convert FAILED; mando audio raw a Evolution con encoding=true como fallback");
            ogg = audio;
        }

        // /message/sendWhatsAppAudio fuerza envío como PTT.
        var body = new
        {
            number = jid,
            audio = Convert.ToBase64String(ogg),
            encoding = true,
            delay = 0
        };
        var resp = await _http.PostAsJsonAsync($"message/sendWhatsAppAudio/{Uri.EscapeDataString(instanceName)}", body, ct);
        if (!resp.IsSuccessStatusCode)
        {
            var txt = await resp.Content.ReadAsStringAsync(ct);
            _log.LogWarning("SendVoiceNote {Instance} -> {Jid} ({Bytes}b) failed: {Status} {Body}",
                instanceName, jid, ogg.Length, resp.StatusCode, txt);
            return false;
        }
        return true;
    }

    private static async Task<byte[]> ConvertToOggOpusAsync(byte[] input, CancellationToken ct)
    {
        // Pasamos input por archivo temporal en vez de stdin: containers MP4/M4A
        // tienen el moov atom al final y ffmpeg necesita poder hacer seek para
        // leerlos. Por pipe el demuxer falla y exit != 0.
        var tempIn = Path.Combine(Path.GetTempPath(), $"saleshub-audio-{Guid.NewGuid():N}");
        await File.WriteAllBytesAsync(tempIn, input, ct);
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "ffmpeg",
                ArgumentList =
                {
                    "-hide_banner", "-loglevel", "error",
                    "-i", tempIn,
                    "-vn",
                    "-c:a", "libopus",
                    "-b:a", "32k",
                    "-ac", "1",
                    "-ar", "16000",
                    "-application", "voip",
                    "-f", "ogg",
                    "pipe:1"
                },
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var proc = Process.Start(psi)
                ?? throw new InvalidOperationException("No se pudo iniciar ffmpeg");

            var stdoutTask = ReadAllBytesAsync(proc.StandardOutput.BaseStream, ct);
            var stderrTask = proc.StandardError.ReadToEndAsync();

            var output = await stdoutTask;
            await proc.WaitForExitAsync(ct);

            if (proc.ExitCode != 0 || output.Length == 0)
            {
                var err = await stderrTask;
                throw new InvalidOperationException($"ffmpeg exit {proc.ExitCode}: {err}");
            }
            return output;
        }
        finally
        {
            try { File.Delete(tempIn); } catch { /* best-effort */ }
        }
    }

    private static async Task<byte[]> ReadAllBytesAsync(Stream s, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        await s.CopyToAsync(ms, ct);
        return ms.ToArray();
    }

    private static string BytesSignature(byte[] b)
    {
        var n = Math.Min(b.Length, 4);
        var hex = new System.Text.StringBuilder(2 * n + n);
        for (var i = 0; i < n; i++)
        {
            hex.Append(b[i].ToString("X2"));
            if (i < n - 1) hex.Append(' ');
        }
        var ascii = System.Text.Encoding.ASCII.GetString(b, 0, n);
        return $"{hex} ('{ascii}')";
    }

    public async Task<bool> SendMediaAsync(string instanceName, string jid, byte[] content, string mimeType, string fileName, string? caption, CancellationToken ct = default)
    {
        // Evolution acepta el archivo en base64 vía /message/sendMedia/{instance}.
        // mediatype: "image" | "document" | "video" | "audio". Acá solo
        // distinguimos imagen vs document (PDF y demás).
        var mediaType = mimeType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)
            ? "image"
            : "document";
        var body = new
        {
            number = jid,
            mediatype = mediaType,
            mimetype = mimeType,
            caption = caption ?? string.Empty,
            media = Convert.ToBase64String(content),
            fileName
        };
        var resp = await _http.PostAsJsonAsync($"message/sendMedia/{Uri.EscapeDataString(instanceName)}", body, ct);
        if (!resp.IsSuccessStatusCode)
        {
            var txt = await resp.Content.ReadAsStringAsync(ct);
            _log.LogWarning("SendMedia {Instance} -> {Jid} ({Bytes}b {Mime}) failed: {Status} {Body}",
                instanceName, jid, content.Length, mimeType, resp.StatusCode, txt);
            return false;
        }
        return true;
    }

    private class CheckResponseItem
    {
        [JsonPropertyName("number")] public string? number { get; set; }
        [JsonPropertyName("exists")] public bool? exists { get; set; }
        [JsonPropertyName("jid")] public string? jid { get; set; }
    }
}
