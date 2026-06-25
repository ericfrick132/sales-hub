using System.Net.Http.Json;
using Microsoft.Extensions.Logging;
using SalesHub.Core.Abstractions;

namespace SalesHub.Infrastructure.Services;

/// <summary>
/// Cliente del endpoint de estado de cada producto (GET {StateUrl}?externalIds=a,b,c, X-Api-Key).
/// Lo usa el pull-guard antes de seguir un follow-up.
/// </summary>
public class ProductStateClient : IProductStateClient
{
    private readonly HttpClient _http;
    private readonly ILogger<ProductStateClient> _log;

    public ProductStateClient(HttpClient http, ILogger<ProductStateClient> log)
    {
        _http = http; _log = log;
        _http.Timeout = TimeSpan.FromSeconds(15);
    }

    public async Task<IReadOnlyList<LeadStateResult>> GetStatesAsync(
        string stateUrl, string apiKey, IReadOnlyCollection<string> externalIds, CancellationToken ct = default)
    {
        if (externalIds.Count == 0) return Array.Empty<LeadStateResult>();
        var url = $"{stateUrl.TrimEnd('/')}?externalIds={Uri.EscapeDataString(string.Join(",", externalIds))}";
        var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Add("X-Api-Key", apiKey);
        try
        {
            var resp = await _http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode)
            {
                _log.LogWarning("ProductState {Url}: {Code}", stateUrl, (int)resp.StatusCode);
                return Array.Empty<LeadStateResult>();
            }
            var data = await resp.Content.ReadFromJsonAsync<List<StateDto>>(cancellationToken: ct) ?? new();
            return data
                .Where(d => !string.IsNullOrWhiteSpace(d.ExternalId))
                .Select(d => new LeadStateResult(d.ExternalId!, d.StopFollowup, d.Won, d.Status))
                .ToList();
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "ProductState {Url}: falló la consulta", stateUrl);
            return Array.Empty<LeadStateResult>();
        }
    }

    private record StateDto(string? ExternalId, bool StopFollowup, bool Won, string? Status);
}
