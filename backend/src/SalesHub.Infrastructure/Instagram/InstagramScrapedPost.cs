namespace SalesHub.Infrastructure.Instagram;

/// <summary>
/// Un post de competidor bajado con la sesión logueada (browser), listo para mapear a
/// CompetitorPost. Reemplazo del item de Apify. VideoUrl puede venir null en el grid del
/// perfil (IG a veces no lo expone sin abrir el post) — ahí se usa el DisplayUrl.
/// </summary>
public record InstagramScrapedPost(
    string ExternalId,
    string? Shortcode,
    string? Caption,
    DateTimeOffset? PostedAt,
    int Likes,
    int CommentsCount,
    string? DisplayUrl,
    string? VideoUrl,
    bool IsVideo,
    string RawJson);
