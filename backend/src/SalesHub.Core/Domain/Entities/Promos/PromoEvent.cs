namespace SalesHub.Core.Domain.Entities.Promos;

/// <summary>
/// Una fecha del calendario de promos (Cyber Monday, Día del Padre, CrossFit Open...).
/// <see cref="LeadDays"/> días antes de <see cref="Date"/> el motor (PromoEngine) crea el
/// cupón + la landing en cada app de <see cref="Apps"/> y fuerza posteos de promo.
/// Se cura desde Marketing → Promos; el seed sale de docs/promo-calendar.
/// </summary>
public class PromoEvent
{
    public Guid Id { get; set; }

    /// <summary>Único, con año (ej. "black-friday-2026"). Es el utm_campaign y el id de la promo en la app.</summary>
    public string Slug { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    /// <summary>Qué es la fecha — pista para el copy de la landing y de los posteos.</summary>
    public string Description { get; set; } = string.Empty;

    public DateOnly Date { get; set; }
    /// <summary>Último día del evento (= Date si dura un día). El cupón vence al terminar este día (hora AR).</summary>
    public DateOnly EndDate { get; set; }

    /// <summary>ProductKeys de sales-hub (gymhero, turnospro, playcrew, construction, bunker, unistock). Vacío = todas.</summary>
    public List<string> Apps { get; set; } = new();

    /// <summary>null = Promos:DefaultDiscountPercent.</summary>
    public int? DiscountPercent { get; set; }
    /// <summary>Meses con descuento. null = Promos:DefaultDurationMonths.</summary>
    public int? DurationMonths { get; set; }
    /// <summary>Código del cupón. null = se arma del slug (ej. CYBER20).</summary>
    public string? CouponCode { get; set; }
    /// <summary>Cuántos días antes del evento arranca la promo.</summary>
    public int LeadDays { get; set; } = 7;

    public bool Enabled { get; set; } = true;
    /// <summary>"confirmada" | "estimada" (fecha sin anunciar oficialmente).</summary>
    public string Confidence { get; set; } = "confirmada";
    public string? Source { get; set; }
    public string? Note { get; set; }

    /// <summary>Foto pública (CC0/dominio público) de donde se baja la imagen la primera vez.</summary>
    public string? ImageSourceUrl { get; set; }
    public string? ImageCredit { get; set; }
    /// <summary>La imagen ya bajada y guardada (SocialPostAsset), servida en /api/posteos/assets/{id}.jpg.</summary>
    public Guid? ImageAssetId { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    public ICollection<PromoCampaign> Campaigns { get; set; } = new List<PromoCampaign>();
}
