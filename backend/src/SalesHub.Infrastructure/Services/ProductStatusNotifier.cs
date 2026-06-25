using System.Net.Http.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using SalesHub.Core.Abstractions;

namespace SalesHub.Infrastructure.Services;

/// <summary>
/// POSTea el cambio de estado de venta al webhook del producto de origen
/// (LeadImport:Sources[].StatusWebhookUrl, auth X-Api-Key con la misma ApiKey del source).
/// Fire-and-forget: nunca tira excepción hacia arriba.
/// </summary>
public class ProductStatusNotifier : IProductStatusNotifier
{
    private readonly HttpClient _http;
    private readonly IConfiguration _config;
    private readonly ILogger<ProductStatusNotifier> _log;

    public ProductStatusNotifier(HttpClient http, IConfiguration config, ILogger<ProductStatusNotifier> log)
    {
        _http = http; _config = config; _log = log;
        _http.Timeout = TimeSpan.FromSeconds(10);
    }

    public async Task NotifyAsync(string productKey, string? externalId, string status, bool won, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(externalId)) return;
        var (url, apiKey) = ResolveSource(productKey);
        if (url is null || apiKey is null) return; // producto sin StatusWebhookUrl → no-op

        try
        {
            var req = new HttpRequestMessage(HttpMethod.Post, url);
            req.Headers.Add("X-Api-Key", apiKey);
            req.Content = JsonContent.Create(new { externalId, status, won });
            var resp = await _http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode)
                _log.LogWarning("StatusNotify {Pk}/{Ext}: {Code}", productKey, externalId, (int)resp.StatusCode);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "StatusNotify {Pk}/{Ext}: falló", productKey, externalId);
        }
    }

    private (string? url, string? apiKey) ResolveSource(string productKey)
    {
        foreach (var c in _config.GetSection("LeadImport:Sources").GetChildren())
        {
            if (string.Equals(c["ProductKey"], productKey, StringComparison.OrdinalIgnoreCase))
            {
                var url = c["StatusWebhookUrl"];
                var key = c["ApiKey"];
                if (!string.IsNullOrWhiteSpace(url) && !string.IsNullOrWhiteSpace(key))
                    return (url!.TrimEnd('/'), key);
            }
        }
        return (null, null);
    }
}
