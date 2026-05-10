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
        // Asegurarnos de que el webhook esté seteado, sea instancia nueva o
        // ya creada anteriormente sin webhook (idempotente). Sin esto, los
        // inbound de los leads nunca llegan al backend y la conversación se
        // ve "muda" en /conversations.
        await SetWebhookAsync(instanceName, ct);
        return status;
    }

    private async Task SetWebhookAsync(string instanceName, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_opts.WebhookUrl)) return;
        try
        {
            var body = new
            {
                webhook = new
                {
                    url = _opts.WebhookUrl,
                    enabled = true,
                    events = new[] { "MESSAGES_UPSERT" },
                    webhookByEvents = false,
                    webhookBase64 = false
                }
            };
            var resp = await _http.PostAsJsonAsync($"webhook/set/{Uri.EscapeDataString(instanceName)}", body, ct);
            if (!resp.IsSuccessStatusCode)
            {
                var text = await resp.Content.ReadAsStringAsync(ct);
                _log.LogWarning("SetWebhook {Instance} failed: {Status} {Body}", instanceName, resp.StatusCode, text);
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "SetWebhook {Instance} threw — non-fatal", instanceName);
        }
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

    public async Task SetPresenceRecordingAsync(string instanceName, string jid, int durationSeconds, CancellationToken ct = default)
    {
        try
        {
            await _http.PostAsJsonAsync($"chat/sendPresence/{Uri.EscapeDataString(instanceName)}",
                new { number = jid, delay = durationSeconds * 1000, presence = "recording" }, ct);
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
        var prepared = await PrepareVoiceNoteAsync(audio, ct);
        return await SendPreparedVoiceNoteAsync(instanceName, jid, prepared.OggBytes, ct);
    }

    public async Task<PreparedVoiceNote> PrepareVoiceNoteAsync(byte[] input, CancellationToken ct = default)
    {
        // WhatsApp solo reproduce notas de voz si vienen en container OGG con
        // codec Opus (mono, ~16-48kHz). Convertimos con ffmpeg para garantizarlo.
        // Además aplicamos tweaks aleatorios (bitrate variable, padding/silencio,
        // micro atempo) en cada envío para que dos audios "iguales" tengan hash
        // distinto y Meta no nos flaggee por repetición.
        byte[] ogg;
        try
        {
            ogg = await ConvertToOggOpusAsync(input, withTweaks: true, ct);
            _log.LogDebug("ffmpeg ok (tweaks on): {InBytes}b → {OutBytes}b", input.Length, ogg.Length);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "ffmpeg con tweaks FALLÓ — retry sin tweaks");
            try
            {
                ogg = await ConvertToOggOpusAsync(input, withTweaks: false, ct);
            }
            catch (Exception ex2)
            {
                _log.LogWarning(ex2, "ffmpeg sin tweaks también falló — uso input raw como fallback");
                ogg = input;
            }
        }
        var dur = await ProbeDurationSecondsAsync(ogg, ct);
        return new PreparedVoiceNote(ogg, dur);
    }

    public async Task<bool> SendPreparedVoiceNoteAsync(string instanceName, string jid, byte[] ogg, CancellationToken ct = default)
    {
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

    private static async Task<int> ProbeDurationSecondsAsync(byte[] ogg, CancellationToken ct)
    {
        // ffprobe retorna duración en segundos (float) en stdout.
        var tempIn = Path.Combine(Path.GetTempPath(), $"saleshub-probe-{Guid.NewGuid():N}.ogg");
        await File.WriteAllBytesAsync(tempIn, ogg, ct);
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "ffprobe",
                ArgumentList = {
                    "-v", "error",
                    "-show_entries", "format=duration",
                    "-of", "default=noprint_wrappers=1:nokey=1",
                    tempIn
                },
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var proc = Process.Start(psi)
                ?? throw new InvalidOperationException("No se pudo iniciar ffprobe");
            var stdout = await proc.StandardOutput.ReadToEndAsync(ct);
            await proc.WaitForExitAsync(ct);
            if (proc.ExitCode != 0) return 0;
            if (double.TryParse(stdout.Trim(), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var d))
                return (int)Math.Ceiling(d);
            return 0;
        }
        finally
        {
            try { File.Delete(tempIn); } catch { /* best-effort */ }
        }
    }

    private static async Task<byte[]> ConvertToOggOpusAsync(byte[] input, bool withTweaks, CancellationToken ct)
    {
        // Pasamos input por archivo temporal en vez de stdin: containers MP4/M4A
        // tienen el moov atom al final y ffmpeg necesita poder hacer seek para
        // leerlos. Por pipe el demuxer falla y exit != 0.
        var tempIn = Path.Combine(Path.GetTempPath(), $"saleshub-audio-{Guid.NewGuid():N}");
        await File.WriteAllBytesAsync(tempIn, input, ct);
        try
        {
            // Tweaks anti-hash: bitrate aleatorio, micro silencio al final,
            // atempo casi-1 (perceptiblemente igual). Cada envío genera bytes
            // distintos. Solo los aplicamos si withTweaks=true; en el retry
            // por error ffmpeg los sacamos para volver al baseline conocido.
            string bitrate = "32k";
            string? afFilter = null;
            if (withTweaks)
            {
                var rng = Random.Shared;
                bitrate = new[] { "24k", "28k", "32k", "36k", "40k" }[rng.Next(5)];
                var padMs = 50 + rng.Next(0, 200);                  // 50–250ms silencio al final
                var atempo = 0.99 + (rng.NextDouble() * 0.02);      // 0.99–1.01 (±1% velocidad)
                afFilter = string.Format(
                    System.Globalization.CultureInfo.InvariantCulture,
                    "apad=pad_dur=0.{0},atempo={1:0.000}",
                    padMs.ToString("D3"),
                    atempo);
            }

            var args = new List<string>
            {
                "-hide_banner", "-loglevel", "error",
                "-i", tempIn,
                "-vn",
                // Strippeamos metadata del input — los Voice Memos de iOS arrastran
                // M4A major_brand, voice-memo-uuid, title, etc. Eso confunde a
                // Baileys/WhatsApp al parsear el OGG y termina mostrando "no se
                // puede abrir" aunque el codec sea opus. +bitexact además evita
                // metadata de timestamp.
                "-map_metadata", "-1",
                "-fflags", "+bitexact"
            };
            if (afFilter is not null)
            {
                args.Add("-af");
                args.Add(afFilter);
            }
            args.AddRange(new[]
            {
                "-c:a", "libopus",
                "-b:a", bitrate,
                "-ac", "1",
                // libopus opera internamente a 48kHz; pedirle 16k no sirve y
                // genera ambigüedad en el OpusHead. Forzamos 48k mono.
                "-ar", "48000",
                "-application", "voip",
                "-f", "ogg",
                "pipe:1"
            });

            var psi = new ProcessStartInfo
            {
                FileName = "ffmpeg",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            foreach (var a in args) psi.ArgumentList.Add(a);

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
