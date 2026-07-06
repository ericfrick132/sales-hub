namespace SalesHub.Core.Domain.Entities.Social;

/// <summary>
/// Inspiración propia del usuario: una imagen y/o una nota que le gustó,
/// agrupada por tema. Alimenta la generación de posteos en dos frentes:
/// el TEMA (ángulo/concepto del caption) y el VISUAL (estilo para el prompt
/// de imagen/video). La imagen vive en la DB (bytea) igual que
/// <see cref="SocialPostAsset"/> y se sirve por endpoint público.
/// </summary>
public class InspirationItem
{
    public Guid Id { get; set; }

    /// <summary>App destino sugerida (null = sirve para cualquiera).</summary>
    public string? ProductKey { get; set; }

    /// <summary>Tema que agrupa las inspiraciones (ej. "motivación gym", "humor padel").</summary>
    public string Topic { get; set; } = string.Empty;

    /// <summary>Nota del usuario: qué le gustó / qué idea dispara.</summary>
    public string? Note { get; set; }

    /// <summary>Link de origen si vino de una URL (post, artículo, etc.).</summary>
    public string? SourceUrl { get; set; }

    // ── Imagen (opcional: puede ser una idea solo de texto) ────────────────
    public string? MimeType { get; set; }
    public byte[]? ImageContent { get; set; }
    public long SizeBytes { get; set; }

    /// <summary>
    /// true mientras el bot espera que el usuario conteste "para qué es"
    /// (tema/app). Las que quedan pendientes se pueden re-etiquetar desde la web.
    /// </summary>
    public bool PendingTopic { get; set; }

    // ── Uso ────────────────────────────────────────────────────────────────
    public int TimesUsed { get; set; }
    public DateTimeOffset? LastUsedAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
