namespace SalesHub.Core.Domain.Entities;

/// <summary>
/// Lo que dice el bot al terminar la pre-calificación según el ángulo del anuncio del que vino el
/// lead (ver <see cref="MetaAd.Angle"/>). Angle vacío = el pitch por defecto (lead sin anuncio o
/// ángulo sin pitch propio). Admite {a|b} y [NUEVO_MENSAJE].
/// </summary>
public class AnglePitch
{
    public string Angle { get; set; } = string.Empty;
    public string Text { get; set; } = string.Empty;
    /// <summary>Adjuntos (imágenes, video) que van después del texto.</summary>
    public List<Guid> MediaAssetIds { get; set; } = new();

    /// <summary>
    /// Guion propio del anuncio: si tiene preguntas, reemplazan a las de la config (un mismo producto con
    /// anuncios de servicios distintos, ej. EFCloud web vs sistemas a medida). Vacío = las de la config.
    /// </summary>
    public List<string> Questions { get; set; } = new();
    /// <summary>Saludo propio del anuncio (reemplaza <see cref="OnboardingConfig.Intro"/>). Vacío = el de la config.</summary>
    public string Intro { get; set; } = string.Empty;
    /// <summary>
    /// Lo que se le contesta si pregunta el precio, antes de pasar a la persona (ej. "sale 300 usd").
    /// Vacío = pasa a la persona sin decir nada, como siempre.
    /// </summary>
    public string PriceReply { get; set; } = string.Empty;
}
