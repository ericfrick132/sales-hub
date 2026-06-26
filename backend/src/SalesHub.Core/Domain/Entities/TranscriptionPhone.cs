namespace SalesHub.Core.Domain.Entities;

/// <summary>
/// Número de WhatsApp autorizado a usar el relay de transcripción: manda (o reenvía)
/// una nota de voz al WhatsApp del bot y recibe el texto transcripto de vuelta.
/// Es una allowlist personal — sólo estos números disparan el módulo. El on/off global
/// vive en RuntimeFlag key "transcription".
/// </summary>
public class TranscriptionPhone
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Sólo dígitos (ej. "541169370050"). El match es tolerante por sufijo,
    /// así que da igual si se guardó con/sin 54, 9, + o separadores.</summary>
    public string Phone { get; set; } = string.Empty;

    /// <summary>Nombre amigable opcional para mostrarlo en la UI.</summary>
    public string? Label { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
