using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SalesHub.Core.Abstractions;
using SalesHub.Core.Domain.Entities;
using SalesHub.Core.Domain.Enums;
using SalesHub.Infrastructure.Options;

namespace SalesHub.Infrastructure.Apify;

/// <summary>
/// Uses apify/facebook-posts-scraper to find business pages by keyword+location, extracting
/// contact info (phone, website, IG) from page About sections and captions.
/// </summary>
public class ApifyFacebookPostsSource : IApifySource
{
    private static readonly Regex PhoneRx = new(@"(\+?\d[\d\s\-\(\)]{7,}\d)", RegexOptions.Compiled);
    private static readonly Regex IgRx = new(@"instagram\.com/([A-Za-z0-9_.]+)", RegexOptions.Compiled);
    private readonly ApifyHttpClient _client;
    private readonly ApifyOptions _opts;
    private readonly ILogger<ApifyFacebookPostsSource> _log;

    public LeadSource Source => LeadSource.ApifyFacebookPages;

    public ApifyFacebookPostsSource(ApifyHttpClient client, IOptions<ApifyOptions> opts, ILogger<ApifyFacebookPostsSource> log)
    {
        _client = client;
        _opts = opts.Value;
        _log = log;
    }

    public async Task<SourceRunResult> RunAsync(SourceRunRequest request, CancellationToken ct = default)
    {
        if (!_opts.FacebookPosts.Enabled) return new SourceRunResult(Source, Array.Empty<Lead>(), null, 0);

        var q = request.Category ?? request.Product.Categories.FirstOrDefault() ?? "gym";
        var loc = request.City ?? request.Product.CountryName;

        var input = new
        {
            searchQueries = new[] { $"{q} {loc}" },
            resultsLimit = Math.Min(request.MaxResults, _opts.FacebookPosts.MaxResults),
            onlyPosts = false,
            language = request.Product.Language
        };

        var items = await _client.RunActorSyncAsync(_opts.FacebookPosts.ActorId, input, _opts.RunTimeoutSeconds, ct);
        _log.LogInformation("Apify FB Posts returned {Count} items", items.Length);

        var leads = new List<Lead>();
        var seen = new HashSet<string>();
        foreach (var item in items)
        {
            var pageUrl = GetStr(item, "pageUrl") ?? GetStr(item, "url");
            var pageName = GetStr(item, "pageName") ?? GetStr(item, "user", "name") ?? GetStr(item, "title");
            if (string.IsNullOrWhiteSpace(pageName)) continue;
            var pageKey = pageUrl ?? pageName;
            if (!seen.Add(pageKey!)) continue;

            var caption = GetStr(item, "text") ?? GetStr(item, "message") ?? string.Empty;
            var about = GetStr(item, "pageInfo", "about") ?? GetStr(item, "about");
            var textBlob = $"{caption} {about}";
            var phone = GetStr(item, "phone") ?? PhoneRx.Match(textBlob).Value;
            var ig = IgRx.Match(textBlob).Groups[1].Value;

            leads.Add(new Lead
            {
                ProductKey = request.Product.ProductKey,
                Source = LeadSource.ApifyFacebookPages,
                ExternalId = pageKey,
                Name = pageName!,
                City = request.City,
                Province = request.Province,
                Country = request.Product.Country,
                RawPhone = string.IsNullOrWhiteSpace(phone) ? null : phone,
                Website = GetStr(item, "website") ?? GetStr(item, "pageInfo", "website"),
                FacebookUrl = pageUrl,
                InstagramHandle = string.IsNullOrWhiteSpace(ig) ? null : ig,
                SearchQuery = input.searchQueries[0],
                RawDataJson = item.GetRawText(),
                Score = 3
            });
        }
        return new SourceRunResult(Source, leads, null, items.Length);
    }

    private static string? GetStr(JsonElement item, params string[] path)
    {
        JsonElement current = item;
        foreach (var p in path)
        {
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(p, out var next)) return null;
            current = next;
        }
        return current.ValueKind == JsonValueKind.String ? current.GetString() : null;
    }
}
