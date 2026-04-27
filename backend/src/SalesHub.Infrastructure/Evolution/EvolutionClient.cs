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

    private class CheckResponseItem
    {
        [JsonPropertyName("number")] public string? number { get; set; }
        [JsonPropertyName("exists")] public bool? exists { get; set; }
        [JsonPropertyName("jid")] public string? jid { get; set; }
    }
}
