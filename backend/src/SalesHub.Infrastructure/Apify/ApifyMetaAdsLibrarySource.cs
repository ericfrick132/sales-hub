using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SalesHub.Core.Abstractions;
using SalesHub.Core.Domain.Entities;
using SalesHub.Core.Domain.Enums;
using SalesHub.Infrastructure.Options;

namespace SalesHub.Infrastructure.Apify;

public class ApifyMetaAdsLibrarySource : IApifySource
{
    private readonly ApifyHttpClient _client;
    private readonly ApifyOptions _opts;
    private readonly ILogger<ApifyMetaAdsLibrarySource> _log;

    public LeadSource Source => LeadSource.ApifyMetaAdsLibrary;

    public ApifyMetaAdsLibrarySource(ApifyHttpClient client, IOptions<ApifyOptions> opts, ILogger<ApifyMetaAdsLibrarySource> log)
    {
        _client = client;
        _opts = opts.Value;
        _log = log;
    }

    public async Task<SourceRunResult> RunAsync(SourceRunRequest request, CancellationToken ct = default)
    {
        if (!_opts.MetaAdsLibrary.Enabled) return new SourceRunResult(Source, Array.Empty<Lead>(), null, 0);

        var searchQuery = request.Category ?? request.Product.Categories.FirstOrDefault() ?? "gimnasio";

        // actor-schema-agnostic — different actors accept slightly different inputs; we pass
        // the common ones. Override via appsettings if the chosen actor requires specific fields.
        var input = new
        {
            searchTerms = new[] { searchQuery },
            country = request.Product.Country,
            countries = new[] { request.Product.Country },
            adActiveStatus = "active",
            adType = "all",
            maxResults = Math.Min(request.MaxResults, _opts.MetaAdsLibrary.MaxResults),
            urls = Array.Empty<string>()
        };

        var items = await _client.RunActorSyncAsync(_opts.MetaAdsLibrary.ActorId, input, _opts.RunTimeoutSeconds, ct);
        _log.LogInformation("Apify Meta Ads returned {Count} items", items.Length);

        var leads = new List<Lead>();
        var seen = new HashSet<string>();
        foreach (var item in items)
        {
            var pageName = GetStr(item, "pageName") ?? GetStr(item, "page_name") ?? GetStr(item, "advertiserName");
            var pageId = GetStr(item, "pageId") ?? GetStr(item, "page_id");
            if (string.IsNullOrWhiteSpace(pageName) || string.IsNullOrWhiteSpace(pageId)) continue;
            if (!seen.Add(pageId)) continue;

            leads.Add(new Lead
            {
                ProductKey = request.Product.ProductKey,
                Source = LeadSource.ApifyMetaAdsLibrary,
                ExternalId = pageId,
                Name = pageName!,
                City = request.City,
                Province = request.Province,
                Country = request.Product.Country,
                FacebookUrl = $"https://facebook.com/{pageId}",
                Website = GetStr(item, "linkUrl") ?? GetStr(item, "pageProfileUri"),
                SearchQuery = searchQuery,
                RawDataJson = item.GetRawText(),
                Score = 20 // ya gasta en ads = lead caliente
            });
        }
        return new SourceRunResult(Source, leads, null, items.Length);
    }

    private static string? GetStr(JsonElement item, string name)
        => item.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
