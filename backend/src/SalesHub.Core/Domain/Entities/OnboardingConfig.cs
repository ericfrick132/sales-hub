namespace SalesHub.Core.Domain.Entities;

/// <summary>
/// Config del onboarding de ads POR APP (multi-app). El motor de onboarding es genérico y lee
/// esto: cada producto define su intro, sus preguntas, su endpoint de provisión y su mensaje de
/// éxito. Si <see cref="Enabled"/> es false, los ad leads de esa app NO arrancan el bot.
/// </summary>
public class OnboardingConfig
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>ProductKey de la app (gymhero, turnospro, …).</summary>
    public string ProductKey { get; set; } = string.Empty;

    public bool Enabled { get; set; }

    /// <summary>Saludo inicial (puede tener [NUEVO_MENSAJE] para splittear). Ej: "buenas! soy Eric de GymHero."</summary>
    public string Intro { get; set; } = string.Empty;

    /// <summary>Preguntas del alta, en orden. La PRIMERA es el nombre del negocio.</summary>
    public List<string> Questions { get; set; } = new();

    /// <summary>Mensaje que pide el mail antes de crear la cuenta.</summary>
    public string EmailPrompt { get; set; } = string.Empty;

    /// <summary>Endpoint de provisión (bot-register) de la app.</summary>
    public string ProvisionUrl { get; set; } = string.Empty;

    /// <summary>Nombre del campo del body al que mapea el nombre del negocio (ej. "gymName", "businessName").</summary>
    public string ProvisionNameField { get; set; } = "name";

    /// <summary>Mensaje de éxito (puede tener [NUEVO_MENSAJE] y el placeholder {accessUrl}).</summary>
    public string SuccessMessage { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
