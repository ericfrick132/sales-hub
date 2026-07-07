using System.Text.Json;
using SalesHub.Core.Domain.Entities.Social;

namespace SalesHub.Infrastructure.Services.Social;

/// <summary>Helpers de (de)serialización de las slides de un posteo multi-slide.</summary>
public static class SocialPostSlides
{
    private static readonly JsonSerializerOptions Opts = new() { PropertyNameCaseInsensitive = true };

    /// <summary>GeneratedSlide[] (de Claude, sin imagen) → JSON de PostSlide[] para guardar.</summary>
    public static string FromGenerated(IEnumerable<GeneratedSlide> slides)
    {
        var list = slides
            .OrderBy(s => s.Order)
            .Select(s => new PostSlide { Order = s.Order, Role = s.Role, Overlay = s.Overlay, Prompt = s.Prompt })
            .ToList();
        return list.Count > 0 ? JsonSerializer.Serialize(list, Opts) : string.Empty;
    }

    /// <summary>JSON → PostSlide[] (vacío si no hay slides).</summary>
    public static List<PostSlide> Parse(string? slidesJson)
    {
        if (string.IsNullOrWhiteSpace(slidesJson)) return new();
        try { return JsonSerializer.Deserialize<List<PostSlide>>(slidesJson, Opts) ?? new(); }
        catch { return new(); }
    }

    public static string Serialize(IEnumerable<PostSlide> slides) => JsonSerializer.Serialize(slides.ToList(), Opts);

    /// <summary>URLs de las imágenes de las slides (para publicar el carrusel/combo en Buffer).</summary>
    public static List<string> AssetUrls(string? slidesJson) =>
        Parse(slidesJson).Where(s => !string.IsNullOrWhiteSpace(s.AssetUrl)).Select(s => s.AssetUrl!).ToList();

    public static bool HasSlides(string? slidesJson) => !string.IsNullOrWhiteSpace(slidesJson);
}
