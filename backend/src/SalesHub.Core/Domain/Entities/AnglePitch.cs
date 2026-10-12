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
}
