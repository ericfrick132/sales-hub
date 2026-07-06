using SalesHub.Core.Domain.Enums;

namespace SalesHub.Core.Domain.Entities;

/// <summary>
/// Campaña de auto-follow: toma una lista de cuentas del <see cref="SourceHandle"/>
/// (sus <c>followers</c> o las cuentas que ese perfil <c>sigue</c>, según
/// <see cref="SourceMode"/>) y las va siguiendo desde <see cref="InstagramAccountId"/>
/// a un ritmo de <see cref="DailyRate"/> follows por día.
/// </summary>
public class InstagramFollowCampaign
{
    public Guid Id { get; set; }

    /// <summary>Cuenta de Instagram que ejecuta los follows.</summary>
    public Guid InstagramAccountId { get; set; }
    public InstagramAccount? InstagramAccount { get; set; }

    /// <summary>Handle del perfil del que tomamos las cuentas a seguir (sin @).</summary>
    public string SourceHandle { get; set; } = string.Empty;

    /// <summary>
    /// Qué lista del <see cref="SourceHandle"/> seguimos: sus seguidores
    /// (<see cref="InstagramFollowSourceMode.Followers"/>) o las cuentas que
    /// ese perfil sigue (<see cref="InstagramFollowSourceMode.Following"/>).
    /// </summary>
    public InstagramFollowSourceMode SourceMode { get; set; } = InstagramFollowSourceMode.Followers;

    /// <summary>Follows máximos por día. Recomendado &lt; 50 para evitar bloqueos.</summary>
    public int DailyRate { get; set; } = 30;

    // ── Ventana horaria (hora local AR) — que la cuenta actúe en horario humano ──
    /// <summary>Hora local (0-23) desde la que la campaña puede actuar. null = sin restricción.</summary>
    public int? ActiveHourStart { get; set; } = 10;
    /// <summary>Hora local (0-23) hasta la que actúa (exclusiva). null = sin restricción.</summary>
    public int? ActiveHourEnd { get; set; } = 22;

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
