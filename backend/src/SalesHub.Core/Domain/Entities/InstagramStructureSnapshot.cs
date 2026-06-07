namespace SalesHub.Core.Domain.Entities;

/// <summary>
/// Almacena un snapshot de la estructura HTML de Instagram y los selectores
/// generados por el LLM. Se actualiza una vez por día.
/// </summary>
public class InstagramStructureSnapshot
{
    public Guid Id { get; set; }

    /// <summary>Fecha del snapshot (solo fecha, sin hora).</summary>
    public DateOnly SnapshotDate { get; set; }

    /// <summary>Selectores CSS generados por el LLM en formato JSON.</summary>
    public string SelectorsJson { get; set; } = string.Empty;

    /// <summary>HTML crudo de cada página analizada (JSON).</summary>
    public string RawHtmlJson { get; set; } = string.Empty;

    /// <summary>Modelo de LLM usado para el análisis.</summary>
    public string LlmModel { get; set; } = string.Empty;

    /// <summary>Si el análisis fue exitoso.</summary>
    public bool IsValid { get; set; }

    /// <summary>Mensaje de error si falló.</summary>
    public string? ErrorMessage { get; set; }

    /// <summary>Cuenta de Instagram usada para el análisis.</summary>
    public Guid? InstagramAccountId { get; set; }
    public InstagramAccount? InstagramAccount { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
