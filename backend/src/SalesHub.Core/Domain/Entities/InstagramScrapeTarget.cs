namespace SalesHub.Core.Domain.Entities;

/// <summary>
/// Configuración de qué scrapear en Instagram para un producto.
/// El admin define targets (cuentas competidoras, hashtags, ubicaciones)
/// y el scraper las procesa periódicamente.
/// </summary>
public class InstagramScrapeTarget
{
    public Guid Id { get; set; }

    /// <summary>Producto al que pertenece este target (gymhero, unistock, etc).</summary>
    public string ProductKey { get; set; } = string.Empty;
    public Product? Product { get; set; }

    /// <summary>
    /// Tipo de target: "competitor_followers" | "hashtag_commenters" | "location_posts"
    /// </summary>
    public string TargetType { get; set; } = string.Empty;

    /// <summary>
    /// Valor del target:
    /// - Para competitor_followers: el handle de la cuenta (ej: "gimnasioX")
    /// - Para hashtag_commenters: el hashtag sin # (ej: "gimnasioCordoba")
    /// - Para location_posts: el location ID o nombre
    /// </summary>
    public string TargetValue { get; set; } = string.Empty;

    /// <summary>Nombre descriptivo para la UI.</summary>
    public string? DisplayName { get; set; }

    public bool IsActive { get; set; } = true;

    /// <summary>Máximo de perfiles a scrapear por ejecución.</summary>
    public int MaxPerRun { get; set; } = 50;

    /// <summary>Cuenta de Instagram asignada para scrapear este target (null = cualquiera disponible).</summary>
    public Guid? AssignedAccountId { get; set; }
    public InstagramAccount? AssignedAccount { get; set; }

    public DateTimeOffset? LastScrapedAt { get; set; }
    public int TotalLeadsCreated { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
