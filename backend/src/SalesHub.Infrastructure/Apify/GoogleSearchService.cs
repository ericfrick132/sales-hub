using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SalesHub.Infrastructure.Options;

namespace SalesHub.Infrastructure.Apify;

public class GoogleSearchService
{
    private readonly ApifyHttpClient _client;
    private readonly ApifyOptions _opts;
    private readonly ILogger<GoogleSearchService> _log;

    public GoogleSearchService(ApifyHttpClient client, IOptions<ApifyOptions> opts, ILogger<GoogleSearchService> log)
    {
        _client = client; _opts = opts.Value; _log = log;
    }

    public record SerpResult(string Title, string Url, string? Snippet);

    public async Task<IReadOnlyList<SerpResult>> SearchAsync(string query, int maxResults, string country, string language, CancellationToken ct)
    {
        var input = new
        {
            queries = query,
            countryCode = country,
            languageCode = language,
            resultsPerPage = Math.Min(maxResults, _opts.GoogleSearch.MaxResults)
        };
        var items = await _client.RunActorSyncAsync(_opts.GoogleSearch.ActorId, input, _opts.RunTimeoutSeconds, ct);
        var list = new List<SerpResult>();
        foreach (var item in items)
        {
            if (!item.TryGetProperty("organicResults", out var or) || or.ValueKind != JsonValueKind.Array) continue;
            foreach (var r in or.EnumerateArray())
            {
                list.Add(new SerpResult(
                    r.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "",
                    r.TryGetProperty("url", out var u) ? u.GetString() ?? "" : "",
                    r.TryGetProperty("description", out var d) ? d.GetString() : null));
            }
        }
        _log.LogInformation("Google Search {Q} returned {N}", query, list.Count);
        return list;
    }
}
