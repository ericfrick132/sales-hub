namespace SalesHub.Core.Domain.Entities;

/// <summary>
/// Una "persona" (variante) del onboarding de una app multi-perfil. Cuando una app tiene más de un
/// tipo de cliente que entra por el mismo anuncio (ej. bunker: entrenador vs dueño de gym; playcrew:
/// dueño de club vs jugador B2C), el motor primero pregunta cuál es (<see cref="OnboardingConfig.PersonaQuestion"/>),
/// detecta la persona por <see cref="Keywords"/>, y a partir de ahí usa LAS PREGUNTAS Y EL ENDPOINT DE
/// PROVISIÓN DE ESTA PERSONA (no los del config general). Así cada perfil crea la cuenta correcta.
/// </summary>
public class OnboardingPersona
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>ProductKey de la app dueña de esta persona (gymhero, bunker, playcrew, …).</summary>
    public string ProductKey { get; set; } = string.Empty;

    /// <summary>Clave estable de la persona (trainer, gymowner, club, player, …).</summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>Etiqueta legible que se usa al presentar las opciones ("Soy entrenador/coach").</summary>
    public string Label { get; set; } = string.Empty;

    /// <summary>Palabras clave para detectar esta persona en la respuesta del lead (coach, dueño, jugador, …).</summary>
    public List<string> Keywords { get; set; } = new();

    /// <summary>Preguntas de esta persona, en orden. La PRIMERA es el nombre del negocio/persona.</summary>
    public List<string> Questions { get; set; } = new();

    /// <summary>Mensaje que pide el mail antes de crear la cuenta de esta persona.</summary>
    public string EmailPrompt { get; set; } = string.Empty;

    /// <summary>Endpoint de provisión (bot-register) de esta persona — puede diferir del config general
    /// (ej. playcrew: club bot-register vs player bot-register).</summary>
    public string ProvisionUrl { get; set; } = string.Empty;

    /// <summary>Campo del body al que mapea el nombre (ej. "gymName", "fullName").</summary>
    public string ProvisionNameField { get; set; } = "name";

    /// <summary>Params extra a mergear en el body de provisión, como JSON. Ej: {"accountType":"gymowner"}.</summary>
    public string ProvisionExtra { get; set; } = string.Empty;

    /// <summary>Mensaje de éxito de esta persona (puede tener [NUEVO_MENSAJE] y {accessUrl}).</summary>
    public string SuccessMessage { get; set; } = string.Empty;

    public int SortOrder { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
