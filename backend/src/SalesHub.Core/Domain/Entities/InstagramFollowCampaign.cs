namespace SalesHub.Core.Domain.Entities;

/// <summary>
/// Campaña de auto-follow: toma los followers de <see cref="SourceHandle"/>
/// y los va siguiendo desde <see cref="InstagramAccountId"/> a un ritmo
/// de <see cref="DailyRate"/> follows por día.
/// </summary>
public class InstagramFollowCampaign
{
    public Guid Id { get; set; }

    /// <summary>Cuenta de Instagram que ejecuta los follows.</summary>
    public Guid InstagramAccountId { get; set; }
    public InstagramAccount? InstagramAccount { get; set; }

    /// <summary>Handle del perfil cuyos followers vamos a seguir (sin @).</summary>
    public string SourceHandle { get; set; } = string.Empty;

    /// <summary>Follows máximos por día. Recomendado &lt; 50 para evitar bloqueos.</summary>
    public int DailyRate { get; set; } = 30;

    /// <summary>
    /// Tope total de follows para esta campaña. 0 = sin tope (sigue mientras
    /// haya followers nuevos en el origen).
    /// </summary>
    public int MaxTotalFollows { get; set; }

    /// <summary>
    /// Tamaño del lote a scrapear cuando la cola se vacía. El worker hace top-up
    /// cuando quedan menos de <see cref="MinQueuedThreshold"/> pendientes.
    /// </summary>
    public int ScrapeBatchSize { get; set; } = 100;
    public int MinQueuedThreshold { get; set; } = 20;

    public bool IsActive { get; set; } = true;

    // Stats
    public int TotalEnqueued { get; set; }
    public int TotalFollowed { get; set; }
    public int TotalFailed { get; set; }
    public int TotalSkipped { get; set; }

    public DateTimeOffset? LastScrapeAt { get; set; }
    public DateTimeOffset? LastFollowAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    public ICollection<InstagramFollowAction> Actions { get; set; } = new List<InstagramFollowAction>();
}
