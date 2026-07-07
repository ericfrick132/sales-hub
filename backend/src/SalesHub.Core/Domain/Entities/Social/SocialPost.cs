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
    Carousel = 4,
    Video = 5
}

/// <summary>Qué asset lleva → decide el generador (imagen=AiImageGenerator, video=fal.ai).</summary>
public enum SocialAssetKind
{
    Image = 1,
    Video = 2
}

/// <summary>
/// Por dónde se distribuye el posteo. Buffer = API oficial (cuentas business).
/// Warmr = posteo nativo por device real (cuentas frescas/multi-cuenta, video corto).
/// Warmr no tiene API → el tramo final es una cola de handoff (subida manual a Cloud Drop).
/// </summary>
public enum SocialDistribution
{
    Buffer = 1,
    Warmr = 2
}

/// <summary>
/// Ciclo de vida de un posteo:
/// Idea → (genera asset) GeneratingAsset → DraftReady → distribución.
/// Buffer: → PushedToBuffer → Scheduled/Posted.
/// Warmr (sin API): → ReadyForWarmr (cola de handoff) → WarmrUploaded (el humano lo subió a Cloud Drop).
/// Rejected/Error son terminales.
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
    Error = 8,
    ReadyForWarmr = 9,
    WarmrUploaded = 10
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

    // ── Distribución ──────────────────────────────────────────────────────
    /// <summary>Por dónde sale: Buffer (API) o Warmr (device real, handoff).</summary>
    public SocialDistribution Target { get; set; } = SocialDistribution.Buffer;
    /// <summary>Cuenta/handle de Warmr destino (cuando Target = Warmr).</summary>
    public string WarmrAccount { get; set; } = string.Empty;

    // ── Inspiración ───────────────────────────────────────────────────────
    /// <summary>CompetitorPost que inspiró este posteo (si se generó por replicación).</summary>
    public Guid? InspirationPostId { get; set; }

    /// <summary>InspirationItem propio que inspiró este posteo (si se generó desde "Mis ideas").</summary>
    public Guid? InspirationItemId { get; set; }

    // ── Contenido generado ────────────────────────────────────────────────
    /// <summary>Tipo/modo del posteo (emocional, educativo, venta, precio, …) — para variar el mix.</summary>
    public string PostType { get; set; } = string.Empty;
    public string ContentPillar { get; set; } = string.Empty;
    /// <summary>Concepto/idea del posteo (lo que comunica).</summary>
    public string Concept { get; set; } = string.Empty;
    /// <summary>Prompt para el generador de asset (fal.ai para video / AiImageGenerator para imagen).</summary>
    public string Prompt { get; set; } = string.Empty;
    /// <summary>Gancho CORTO (máx ~8 palabras) para estampar en la imagen. Vacío = imagen sin texto.</summary>
    public string OverlayText { get; set; } = string.Empty;
    /// <summary>Guion de narración (voz en off) para VIDEO. ~25 palabras rioplatense. Vacío = video mudo.</summary>
    public string NarrationText { get; set; } = string.Empty;
    public string Caption { get; set; } = string.Empty;
    public List<string> Hashtags { get; set; } = new();

    /// <summary>
    /// Slides de un posteo multi-slide (carrusel de feed / combo de stories), serializadas
    /// como JSON de <see cref="PostSlide"/>[]. Vacío = posteo de una sola imagen/video.
    /// </summary>
    public string SlidesJson { get; set; } = string.Empty;

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
