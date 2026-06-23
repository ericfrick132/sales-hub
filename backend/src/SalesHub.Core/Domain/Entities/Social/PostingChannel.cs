namespace SalesHub.Core.Domain.Entities.Social;

/// <summary>
/// Configuración de una red social para un producto (1 fila por app × red).
/// Acá vive lo que varía por red: si está activa, el canal de Buffer, el formato
/// (story / video / post…) y — clave — un PROMPT propio para esa red y esa app.
/// El generador combina la base de marca del PostingProfile con este prompt.
/// </summary>
public class PostingChannel
{
    public Guid Id { get; set; }

    public string ProductKey { get; set; } = string.Empty;
    public SocialPlatform Platform { get; set; }

    public bool Enabled { get; set; } = true;

    /// <summary>Id del canal en Buffer para esta red (se completa al conectar).</summary>
    public string BufferChannelId { get; set; } = string.Empty;

    /// <summary>Formato a generar para esta red: Story / Reel / Post / Carousel / Video.</summary>
    public SocialPostFormat Format { get; set; } = SocialPostFormat.Post;

    /// <summary>Imagen o video (decide el generador: Canva vs Higgsfield).</summary>
    public SocialAssetKind AssetKind { get; set; } = SocialAssetKind.Image;

    /// <summary>Prompt/instrucciones específicas de ESTA red para ESTA app.</summary>
    public string PromptTemplate { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
