namespace SalesHub.Core.Domain.Entities.Social;

public enum SocialPlatform
{
    Instagram = 1,
    TikTok = 2,
    YouTube = 3,
    Facebook = 4,
    Twitter = 5,
    LinkedIn = 6
}

/// <summary>Tipo de publicación (mapea al PostType de Buffer para Instagram).</summary>
public enum SocialPostFormat
{
    Post = 1,
    Story = 2,
    Reel = 3,
    Carousel = 4
}

/// <summary>Qué asset lleva → decide el generador (imagen=Canva, video=Higgsfield).</summary>
public enum SocialAssetKind
{
    Image = 1,
    Video = 2
}

/// <summary>
/// Ciclo de vida de un posteo:
/// Idea → (genera asset) GeneratingAsset → DraftReady → (sube a Buffer) PushedToBuffer
/// → Scheduled/Posted. Rejected/Error son terminales. Empezamos siempre como draft
/// en Buffer; el humano aprueba/agenda allá.
/// </summary>
public enum SocialPostStatus
{
    Idea = 1,
    GeneratingAsset = 2,
    DraftReady = 3,
    PushedToBuffer = 4,
    Scheduled = 5,
    Posted = 6,
    Rejected = 7,
    Error = 8
}

/// <summary>
/// Un posteo individual generado por el módulo de Posteos. Lo crea el worker
/// (o a mano desde la UI), avanza por estados y termina como draft en Buffer.
/// </summary>
public class SocialPost
{
    public Guid Id { get; set; }

    public string ProductKey { get; set; } = string.Empty;
    public SocialPlatform Platform { get; set; }
    public string BufferChannelId { get; set; } = string.Empty;
    public SocialPostFormat Format { get; set; } = SocialPostFormat.Post;
    public SocialAssetKind AssetKind { get; set; } = SocialAssetKind.Image;
    public SocialPostStatus Status { get; set; } = SocialPostStatus.Idea;

    // ── Contenido generado ────────────────────────────────────────────────
    public string ContentPillar { get; set; } = string.Empty;
    /// <summary>Concepto/idea del posteo (lo que comunica).</summary>
    public string Concept { get; set; } = string.Empty;
    /// <summary>Prompt para el generador de asset (Higgsfield/Canva).</summary>
    public string Prompt { get; set; } = string.Empty;
    public string Caption { get; set; } = string.Empty;
    public List<string> Hashtags { get; set; } = new();

    // ── Asset + publicación ───────────────────────────────────────────────
    /// <summary>URL pública del asset (lo que Buffer consume).</summary>
    public string? AssetUrl { get; set; }
    public string? ThumbnailUrl { get; set; }
    public string? BufferPostId { get; set; }
    public DateTimeOffset? ScheduledAt { get; set; }
    public DateTimeOffset? PostedAt { get; set; }

    public string? Error { get; set; }
    public string GenerationModel { get; set; } = string.Empty;
    /// <summary>Dump crudo de la generación/respuesta para auditoría.</summary>
    public string? RawJson { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
