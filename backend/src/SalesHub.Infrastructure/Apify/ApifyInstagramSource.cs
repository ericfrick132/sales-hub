using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SalesHub.Core.Abstractions;
using SalesHub.Core.Domain.Entities;
using SalesHub.Core.Domain.Enums;
using SalesHub.Infrastructure.Options;

namespace SalesHub.Infrastructure.Apify;

public class ApifyInstagramSource : IApifySource
{
    private readonly ApifyHttpClient _client;
    private readonly ApifyOptions _opts;
    private readonly ILogger<ApifyInstagramSource> _log;

    public LeadSource Source => LeadSource.ApifyInstagram;

    public ApifyInstagramSource(ApifyHttpClient client, IOptions<ApifyOptions> opts, ILogger<ApifyInstagramSource> log)
    {
        _client = client;
        _opts = opts.Value;
        _log = log;
    }

    public async Task<SourceRunResult> RunAsync(SourceRunRequest request, CancellationToken ct = default)
    {
        if (!_opts.Instagram.Enabled) return new SourceRunResult(Source, Array.Empty<Lead>(), null, 0);

        // Hashtag search: "gimnasio{city}" + "crossfit{city}" etc.
        var hashtags = new List<string>();
        var baseCategories = request.Category is null
            ? request.Product.Categories.Take(3).ToList()
            : new List<string> { request.Category };
        var city = (request.City ?? string.Empty).ToLowerInvariant().Replace(" ", "");
        foreach (var cat in baseCategories)
        {
            var tagCat = Regex.Replace(cat.ToLowerInvariant(), "[^a-z0-9]", "");
            hashtags.Add(string.IsNullOrWhiteSpace(city) ? tagCat : tagCat + city);
        }

        var input = new
        {
            search = hashtags.FirstOrDefault() ?? request.Product.Categories.FirstOrDefault() ?? "gym",
            searchType = "hashtag",
            resultsType = "posts",
            resultsLimit = Math.Min(request.MaxResults, _opts.Instagram.MaxResults),
            addParentData = true,
            extendOutputFunction = "async ({ data }) => { return data; }"
        };

        var items = await _client.RunActorSyncAsync(_opts.Instagram.ActorId, input, _opts.RunTimeoutSeconds, ct);
        _log.LogInformation("Apify IG returned {Count} items", items.Length);

        var leads = new List<Lead>();
        var seenOwners = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in items)
        {
            var owner = ExtractOwnerUsername(item);
            if (owner is null || !seenOwners.Add(owner)) continue;
            var bio = ExtractOwnerBio(item);
            var phone = bio is null ? null : ExtractPhone(bio);

            leads.Add(new Lead
            {
                ProductKey = request.Product.ProductKey,
                Source = LeadSource.ApifyInstagram,
                ExternalId = owner,
                Name = ExtractOwnerFullName(item) ?? owner,
                City = request.City,
                Province = request.Province,
                Country = request.Product.Country,
                RawPhone = phone,
                InstagramHandle = owner,
                Website = $"https://instagram.com/{owner}",
                SearchQuery = input.search,
                RawDataJson = item.GetRawText(),
                Score = bio is null ? 0 : 10
            });
        }

        return new SourceRunResult(Source, leads, null, items.Length);
    }

    private static string? ExtractOwnerUsername(JsonElement item)
    {
        if (item.TryGetProperty("ownerUsername", out var v) && v.ValueKind == JsonValueKind.String) return v.GetString();
        if (item.TryGetProperty("owner", out var owner) && owner.TryGetProperty("username", out var u) && u.ValueKind == JsonValueKind.String) return u.GetString();
        return null;
    }

    private static string? ExtractOwnerFullName(JsonElement item)
    {
        if (item.TryGetProperty("ownerFullName", out var v) && v.ValueKind == JsonValueKind.String) return v.GetString();
        if (item.TryGetProperty("owner", out var owner) && owner.TryGetProperty("full_name", out var u) && u.ValueKind == JsonValueKind.String) return u.GetString();
        return null;
    }

    private static string? ExtractOwnerBio(JsonElement item)
    {
        if (item.TryGetProperty("ownerBio", out var v) && v.ValueKind == JsonValueKind.String) return v.GetString();
        if (item.TryGetProperty("owner", out var owner) && owner.TryGetProperty("biography", out var u) && u.ValueKind == JsonValueKind.String) return u.GetString();
        return null;
    }

    private static readonly Regex PhoneRx = new(@"(\+?\d[\d\s\-\(\)]{7,}\d)", RegexOptions.Compiled);
    private static string? ExtractPhone(string text)
    {
        var m = PhoneRx.Match(text);
        return m.Success ? m.Value : null;
    }
}
