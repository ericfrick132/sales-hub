using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SalesHub.Infrastructure.Options;

namespace SalesHub.Infrastructure.Apify;

public class ApifyHttpClient
{
    private readonly HttpClient _http;
    private readonly ApifyOptions _opts;
    private readonly ILogger<ApifyHttpClient> _log;

    public ApifyHttpClient(HttpClient http, IOptions<ApifyOptions> opts, ILogger<ApifyHttpClient> log)
    {
        _http = http;
        _opts = opts.Value;
        _log = log;
        _http.BaseAddress = new Uri(_opts.BaseUrl.TrimEnd('/') + "/");
        _http.Timeout = TimeSpan.FromSeconds(_opts.RunTimeoutSeconds + 30);
    }

    public async Task<JsonElement[]> RunActorSyncAsync(string actorId, object input, int timeoutSeconds, CancellationToken ct = default)
    {
        var actorPath = actorId.Replace("/", "~");
        var url = $"acts/{actorPath}/run-sync-get-dataset-items?token={_opts.Token}&timeout={timeoutSeconds}&format=json";
        _log.LogInformation("Apify run-sync {Actor}", actorId);
        var resp = await _http.PostAsJsonAsync(url, input, ct);
        if (!resp.IsSuccessStatusCode)
        {
            var txt = await resp.Content.ReadAsStringAsync(ct);
            throw new ApifyException($"Apify run-sync {actorId} failed: {resp.StatusCode} {txt}");
        }
        var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        if (doc.RootElement.ValueKind != JsonValueKind.Array) return Array.Empty<JsonElement>();
        return doc.RootElement.EnumerateArray().Select(e => e.Clone()).ToArray();
    }
}

public class ApifyException : Exception
{
    public ApifyException(string message) : base(message) { }
}
