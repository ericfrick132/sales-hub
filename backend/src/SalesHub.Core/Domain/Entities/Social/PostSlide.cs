namespace SalesHub.Core.Domain.Entities.Social;

/// <summary>
/// Una diapositiva de un posteo multi-slide (carrusel de feed o combo de stories).
/// Se guardan serializadas en <see cref="SocialPost.SlidesJson"/>. Cada slide tiene
/// su propio rol dentro de la narrativa (ej. gancho / info / cierre), su texto
/// estampado (overlay), su prompt visual y su imagen generada.
/// </summary>
public class PostSlide
{
    public int Order { get; set; }
    /// <summary>Rol en la narrativa: hook, encuesta, info, dato, cierre, link, etc.</summary>
    public string Role { get; set; } = string.Empty;
    public string Concept { get; set; } = string.Empty;
    /// <summary>Texto corto estampado en la imagen de esta slide.</summary>
    public string Overlay { get; set; } = string.Empty;
    /// <summary>Prompt visual (INGLÉS) para generar la imagen de esta slide.</summary>
    public string Prompt { get; set; } = string.Empty;
    /// <summary>URL pública de la imagen generada para esta slide.</summary>
    public string? AssetUrl { get; set; }
}
