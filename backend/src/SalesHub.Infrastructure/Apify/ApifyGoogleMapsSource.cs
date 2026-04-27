using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SalesHub.Core.Abstractions;
using SalesHub.Core.Domain.Entities;
using SalesHub.Core.Domain.Enums;
using SalesHub.Infrastructure.Options;

namespace SalesHub.Infrastructure.Apify;

public class ApifyGoogleMapsSource : IApifySource
{
    private readonly ApifyHttpClient _client;
    private readonly ApifyOptions _opts;
    private readonly ILogger<ApifyGoogleMapsSource> _log;

    public LeadSource Source => LeadSource.ApifyGoogleMaps;

    public ApifyGoogleMapsSource(ApifyHttpClient client, IOptions<ApifyOptions> opts, ILogger<ApifyGoogleMapsSource> log)
    {
        _client = client;
        _opts = opts.Value;
        _log = log;
    }

    public async Task<SourceRunResult> RunAsync(SourceRunRequest request, CancellationToken ct = default)
    {
        var actor = _opts.GoogleMaps.ActorId;
        if (!_opts.GoogleMaps.Enabled) return new SourceRunResult(Source, Array.Empty<Lead>(), null, 0);

        var searchStrings = new List<string>();
        if (!string.IsNullOrWhiteSpace(request.Category))
        {
            var loc = request.City is null ? request.Product.CountryName : $"{request.City}, {request.Province ?? string.Empty}, {request.Product.CountryName}";
            searchStrings.Add($"{request.Category} en {loc}".Trim().TrimEnd(','));
        }
        else
        {
            foreach (var cat in request.Product.Categories)
            {
                var loc = request.City is null ? request.Product.CountryName : $"{request.City}, {request.Province ?? string.Empty}, {request.Product.CountryName}";
                searchStrings.Add($"{cat} en {loc}".Trim().TrimEnd(','));
            }
        }

        var input = new
        {
            searchStringsArray = searchStrings,
            maxCrawledPlacesPerSearch = Math.Min(request.MaxResults, _opts.GoogleMaps.MaxResults),
            language = request.Product.Language,
            countryCode = request.Product.RegionCode,
            scrapeContacts = true,
            scrapeDirectories = false,
            scrapeImageAuthors = false,
            scrapeReviewsPersonalData = false
        };

        var items = await _client.RunActorSyncAsync(actor, input, _opts.RunTimeoutSeconds, ct);
        _log.LogInformation("Apify Google Maps returned {Count} items for product {Key}", items.Length, request.Product.ProductKey);

        var leads = new List<Lead>();
        foreach (var item in items)
        {
            var lead = MapItem(item, request);
            if (lead != null) leads.Add(lead);
        }
        return new SourceRunResult(Source, leads, null, items.Length);
    }

    private static Lead? MapItem(JsonElement item, SourceRunRequest req)
    {
        string? GetStr(string name) => item.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        double? GetDbl(string name) => item.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;
        int? GetInt(string name) => item.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : null;

        var name = GetStr("title") ?? GetStr("name");
        if (string.IsNullOrWhiteSpace(name)) return null;

        var phone = GetStr("phone") ?? GetStr("phoneUnformatted");
        var website = GetStr("website") ?? GetStr("url");

        string? ig = null;
        if (website?.Contains("instagram.com/", StringComparison.OrdinalIgnoreCase) == true)
        {
            var uri = new Uri(website);
            ig = uri.AbsolutePath.Trim('/').Split('/').FirstOrDefault();
        }

        double? lat = null, lng = null;
        if (item.TryGetProperty("location", out var loc) && loc.ValueKind == JsonValueKind.Object)
        {
            if (loc.TryGetProperty("lat", out var la) && la.ValueKind == JsonValueKind.Number) lat = la.GetDouble();
            if (loc.TryGetProperty("lng", out var lo) && lo.ValueKind == JsonValueKind.Number) lng = lo.GetDouble();
        }

        return new Lead
        {
            ProductKey = req.Product.ProductKey,
            Source = LeadSource.ApifyGoogleMaps,
            PlaceId = GetStr("placeId") ?? GetStr("cid"),
            Name = name!,
            Address = GetStr("address"),
            City = GetStr("city") ?? req.City,
            Province = GetStr("state") ?? req.Province,
            Country = req.Product.Country,
            RawPhone = phone,
            Website = website,
            InstagramHandle = ig,
            Latitude = lat,
            Longitude = lng,
            Rating = GetDbl("totalScore"),
            TotalReviews = GetInt("reviewsCount"),
            BusinessStatus = GetStr("permanentlyClosed") == "true" ? "closed" : "operational",
            SearchQuery = req.Category ?? string.Join("|", req.Product.Categories),
            RawDataJson = item.GetRawText()
        };
    }
}
