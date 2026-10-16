namespace SalesHub.Core.Domain.Entities.Promos;

public enum PromoCampaignStatus
{
    /// <summary>Cupón + landing creados en la app; los posteos de promo salen mientras dure.</summary>
    Live,
    /// <summary>Terminó: la promo y el cupón se desactivaron en la app.</summary>
    Ended,
    /// <summary>Falló el alta en la app; el motor reintenta en el próximo tick.</summary>
    Error,
}

/// <summary>
/// La promo de UN evento en UNA app: el cupón que se creó, el copy que se mandó y el link
/// con tracking. Único por (evento, app).
/// </summary>
public class PromoCampaign
{
    public Guid Id { get; set; }

    public Guid PromoEventId { get; set; }
    public PromoEvent? Event { get; set; }

    public string ProductKey { get; set; } = string.Empty;
    public PromoCampaignStatus Status { get; set; } = PromoCampaignStatus.Live;

    public string CouponCode { get; set; } = string.Empty;
    public int DiscountPercent { get; set; }
    public int DurationMonths { get; set; }

    public string Headline { get; set; } = string.Empty;
    public string Subheadline { get; set; } = string.Empty;
    public List<string> Bullets { get; set; } = new();

    /// <summary>/promo de la app tal cual la devolvió (sin query).</summary>
    public string? LandingUrl { get; set; }
    /// <summary>Link con tracking de ESTA campaña (utm_campaign + cupón).</summary>
    public string? TrackedUrl { get; set; }

    public DateTimeOffset StartsAt { get; set; }
    public DateTimeOffset EndsAt { get; set; }

    public string? Error { get; set; }
    public int Attempts { get; set; }

    public DateTimeOffset? LaunchedAt { get; set; }
    public DateTimeOffset? EndedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
