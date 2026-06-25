namespace SalesHub.Core.Domain.Entities;

/// <summary>
/// Estado del onboarding scripteado de un lead de anuncio de GymHero (reemplaza el
/// chatbot de n8n). Máquina de 5 pasos: 0=alta, 1=nombre del gym, 2=socios, 3=cobros,
/// 4=email → provisiona la cuenta, 5=hecho.
/// </summary>
public class LeadOnboarding
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid LeadId { get; set; }

    public int Step { get; set; }

    public string? ContactName { get; set; }   // nombre de la persona (pushName)
    public string? GymName { get; set; }
    public string? MemberCount { get; set; }
    public string? PaymentMethod { get; set; }
    public string? Email { get; set; }

    public int GymRetries { get; set; }

    public DateTimeOffset? ProvisionedAt { get; set; }
    public string? AccessUrl { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
