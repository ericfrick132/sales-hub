using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SalesHub.Core.Abstractions;
using SalesHub.Infrastructure.Options;

namespace SalesHub.Infrastructure.Apify;

public class GooglePlacesEnricher : IGooglePlacesEnricher
{
    private readonly HttpClient _http;
    private readonly GoogleOptions _google;
    private readonly ILogger<GooglePlacesEnricher> _log;

    public GooglePlacesEnricher(HttpClient http, IOptions<GoogleOptions> google, ILogger<GooglePlacesEnricher> log)
    {
        _http = http;
        _google = google.Value;
        _log = log;
    }

    public async Task<EnrichedPlace?> EnrichAsync(
        string name, string? address, string? city, string country, string language,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_google.PlacesApiKey))
        {
            _log.LogDebug("Places API key missing — skipping enrichment for {Name}", name);
            return null;
        }
        if (string.IsNullOrWhiteSpace(name)) return null;

        var queryParts = new[] { name, address, city, country }
            .Where(s => !string.IsNullOrWhiteSpace(s));
        var query = string.Join(" ", queryParts);

        // 1) Find Place from Text → place_id
        var findUrl = "https://maps.googleapis.com/maps/api/place/findplacefromtext/json"
            + $"?input={Uri.EscapeDataString(query)}"
            + "&inputtype=textquery"
            + "&fields=place_id"
            + $"&key={_google.PlacesApiKey}"
            + $"&language={language}";

        string? placeId = null;
        try
        {
            using var findResp = await _http.GetAsync(findUrl, ct);
            if (!findResp.IsSuccessStatusCode) return null;
            using var findDoc = await JsonDocument.ParseAsync(await findResp.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            if (!findDoc.RootElement.TryGetProperty("candidates", out var cands) || cands.ValueKind != JsonValueKind.Array || cands.GetArrayLength() == 0)
                return null;
            placeId = cands[0].TryGetProperty("place_id", out var pidEl) ? pidEl.GetString() : null;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Find Place failed for {Name}", name);
            return null;
        }

        if (string.IsNullOrWhiteSpace(placeId)) return null;

        // 2) Place Details → phone + website + rating + geometry
        var detailsUrl = "https://maps.googleapis.com/maps/api/place/details/json"
            + $"?place_id={Uri.EscapeDataString(placeId)}"
            + "&fields=formatted_address,website,formatted_phone_number,international_phone_number,rating,business_status,user_ratings_total,geometry"
            + $"&key={_google.PlacesApiKey}"
            + $"&language={language}";

        try
        {
            using var dResp = await _http.GetAsync(detailsUrl, ct);
            if (!dResp.IsSuccessStatusCode) return null;
            using var dDoc = await JsonDocument.ParseAsync(await dResp.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            if (!dDoc.RootElement.TryGetProperty("result", out var result) || result.ValueKind != JsonValueKind.Object)
                return null;

            string? Get(JsonElement e, string prop) =>
                e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

            double? lat = null, lng = null;
            if (result.TryGetProperty("geometry", out var geo)
                && geo.TryGetProperty("location", out var loc))
            {
                if (loc.TryGetProperty("lat", out var latEl) && latEl.ValueKind == JsonValueKind.Number) lat = latEl.GetDouble();
                if (loc.TryGetProperty("lng", out var lngEl) && lngEl.ValueKind == JsonValueKind.Number) lng = lngEl.GetDouble();
            }
            double? rating = null;
            if (result.TryGetProperty("rating", out var rEl) && rEl.ValueKind == JsonValueKind.Number) rating = rEl.GetDouble();
            int? reviews = null;
            if (result.TryGetProperty("user_ratings_total", out var rtEl) && rtEl.ValueKind == JsonValueKind.Number) reviews = rtEl.GetInt32();

            // Prefer international phone (already in +CC format) when present.
            var phone = Get(result, "international_phone_number") ?? Get(result, "formatted_phone_number");
            var website = Get(result, "website");
            var formattedAddress = Get(result, "formatted_address");
            var bizStatus = Get(result, "business_status");

            return new EnrichedPlace(placeId, formattedAddress, phone, website, rating, reviews, bizStatus, lat, lng);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Place Details failed for {PlaceId}", placeId);
            return null;
        }
    }
}
