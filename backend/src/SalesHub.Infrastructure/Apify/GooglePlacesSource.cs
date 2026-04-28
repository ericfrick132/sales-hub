using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SalesHub.Core.Abstractions;
using SalesHub.Core.Domain.Entities;
using SalesHub.Core.Domain.Enums;
using SalesHub.Infrastructure.Options;

namespace SalesHub.Infrastructure.Apify;

public class GooglePlacesSource : IApifySource
{
    private readonly HttpClient _http;
    private readonly GoogleOptions _google;
    private readonly ILogger<GooglePlacesSource> _log;

    public LeadSource Source => LeadSource.GooglePlaces;

    public GooglePlacesSource(HttpClient http, IOptions<GoogleOptions> google, ILogger<GooglePlacesSource> log)
    {
        _http = http;
        _google = google.Value;
        _log = log;
    }

    public async Task<SourceRunResult> RunAsync(SourceRunRequest request, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_google.PlacesApiKey))
        {
            _log.LogWarning("Google Places API key missing — skipping");
            return new SourceRunResult(Source, Array.Empty<Lead>(), null, 0);
        }

        var loc = request.City is null ? request.Product.CountryName : $"{request.City}, {request.Province ?? string.Empty}, {request.Product.CountryName}";
        // Manual override (admin passes ?category=...) → search just that. Otherwise spread the
        // run across all configured Categories so every search term in /products gets used.
        var categories = request.Category is not null
            ? new[] { request.Category }
            : (request.Product.Categories.Count > 0 ? request.Product.Categories.ToArray() : new[] { "negocio" });

        var leads = new List<Lead>();
        var totalRaw = 0;
        var seenPlaceIds = new HashSet<string>();

        foreach (var category in categories)
        {
            if (leads.Count >= request.MaxResults) break;
            var query = $"{category} en {loc}".TrimEnd(',');
            string? pageToken = null;
            int pages = 0;

            while (pages < 3 && leads.Count < request.MaxResults)
            {
                var url = "https://maps.googleapis.com/maps/api/place/textsearch/json"
                    + $"?query={Uri.EscapeDataString(query)}"
                    + $"&key={_google.PlacesApiKey}"
                    + $"&region={request.Product.RegionCode}"
                    + $"&language={request.Product.Language}"
                    + (pageToken is null ? "" : $"&pagetoken={pageToken}");

                if (pageToken is not null) await Task.Delay(2000, ct);
                var resp = await _http.GetAsync(url, ct);
                resp.EnsureSuccessStatusCode();
                var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync(ct), cancellationToken: ct);

                if (!doc.RootElement.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array) break;
                foreach (var place in results.EnumerateArray())
                {
                    totalRaw++;
                    var name = place.TryGetProperty("name", out var nEl) ? nEl.GetString() : null;
                    if (name is null) continue;
                    var placeId = place.TryGetProperty("place_id", out var pEl) ? pEl.GetString() : null;
                    if (placeId is not null && !seenPlaceIds.Add(placeId)) continue;

                    var details = placeId is null ? null : await GetPlaceDetailsAsync(placeId, request.Product.Language, ct);

                    double? lat = null, lng = null;
                    if (place.TryGetProperty("geometry", out var geom)
                        && geom.TryGetProperty("location", out var locObj))
                    {
                        if (locObj.TryGetProperty("lat", out var latEl) && latEl.ValueKind == JsonValueKind.Number) lat = latEl.GetDouble();
                        if (locObj.TryGetProperty("lng", out var lngEl) && lngEl.ValueKind == JsonValueKind.Number) lng = lngEl.GetDouble();
                    }

                    leads.Add(new Lead
                    {
                        ProductKey = request.Product.ProductKey,
                        Source = LeadSource.GooglePlaces,
                        PlaceId = placeId,
                        Name = name,
                        Address = place.TryGetProperty("formatted_address", out var a) ? a.GetString() : null,
                        Latitude = lat,
                        Longitude = lng,
                        City = request.City,
                        Province = request.Province,
                        Country = request.Product.Country,
                        RawPhone = details?.RootElement.TryGetProperty("formatted_phone_number", out var ph) == true ? ph.GetString() : null,
                        Website = details?.RootElement.TryGetProperty("website", out var ws) == true ? ws.GetString() : null,
                        Rating = place.TryGetProperty("rating", out var r) && r.ValueKind == JsonValueKind.Number ? r.GetDouble() : null,
                        TotalReviews = place.TryGetProperty("user_ratings_total", out var rt) && rt.ValueKind == JsonValueKind.Number ? rt.GetInt32() : null,
                        BusinessStatus = place.TryGetProperty("business_status", out var bs) ? bs.GetString() : null,
                        SearchQuery = query,
                        SearchCategory = category,
                        RawDataJson = place.GetRawText()
                    });

                    if (leads.Count >= request.MaxResults) break;
                }

                pageToken = doc.RootElement.TryGetProperty("next_page_token", out var tok) ? tok.GetString() : null;
                pages++;
                if (pageToken is null) break;
            }
        }

        return new SourceRunResult(Source, leads, null, totalRaw);
    }

    private async Task<JsonDocument?> GetPlaceDetailsAsync(string placeId, string language, CancellationToken ct)
    {
        var url = "https://maps.googleapis.com/maps/api/place/details/json"
            + $"?place_id={Uri.EscapeDataString(placeId)}"
            + "&fields=name,formatted_address,website,formatted_phone_number,rating,business_status,user_ratings_total"
            + $"&key={_google.PlacesApiKey}"
            + $"&language={language}";
        var resp = await _http.GetAsync(url, ct);
        if (!resp.IsSuccessStatusCode) return null;
        var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        return doc.RootElement.TryGetProperty("result", out var result)
            ? JsonDocument.Parse(result.GetRawText())
            : null;
    }
}
