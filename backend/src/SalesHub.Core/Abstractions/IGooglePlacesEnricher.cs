namespace SalesHub.Core.Abstractions;

public record EnrichedPlace(
    string PlaceId,
    string? FormattedAddress,
    string? Phone,
    string? Website,
    double? Rating,
    int? TotalReviews,
    string? BusinessStatus,
    double? Latitude,
    double? Longitude);

public interface IGooglePlacesEnricher
{
    /// <summary>
    /// Busca un lugar en Google Places por nombre + dirección y devuelve los datos
    /// que normalmente faltan en un copy/paste del listado (teléfono, website, lat/lng).
    /// Devuelve null si no encuentra match o si la API key falta.
    /// </summary>
    Task<EnrichedPlace?> EnrichAsync(
        string name,
        string? address,
        string? city,
        string country,
        string language,
        CancellationToken ct = default);
}
