using SalesHub.Core.Domain.Enums;

namespace SalesHub.Core.Domain.Entities;

/// <summary>
/// Acción individual encolada: seguir <see cref="TargetHandle"/> desde
/// la cuenta de la campaña.
/// </summary>
public class InstagramFollowAction
{
    public Guid Id { get; set; }

    public Guid CampaignId { get; set; }
    public InstagramFollowCampaign? Campaign { get; set; }

    /// <summary>Cuenta de IG que ejecutará el follow (denormalizado para queries de cap diario).</summary>
    public Guid InstagramAccountId { get; set; }

    public string TargetHandle { get; set; } = string.Empty;

    public InstagramFollowActionStatus Status { get; set; } = InstagramFollowActionStatus.Pending;

    public DateTimeOffset EnqueuedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ExecutedAt { get; set; }

    public int Attempts { get; set; }
    public string? ErrorMessage { get; set; }
}
