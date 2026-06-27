namespace SalesHub.Core.Domain.Entities;

/// <summary>
/// Telemetría que cada app reporta sobre su follow-up de abandono, para reportes
/// CENTRALIZADOS por app. NO dispara ningún envío — es solo registro (patrón AiUsageLog).
/// Lo escribe POST /api/hub/followup-events.
/// </summary>
public class FollowupEvent
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>App que reporta (gymhero, turnospro, …).</summary>
    public string ProductKey { get; set; } = string.Empty;

    /// <summary>Secuencia/abandono al que pertenece el evento (definido por la app).</summary>
    public string? Trigger { get; set; }

    /// <summary>Id/teléfono del contacto en la app (para seguir la trayectoria de un mismo lead).</summary>
    public string? ExternalRef { get; set; }

    /// <summary>"triggered" (detectó abandono) | "step_sent" | "converted" (entró) | "gave_up".</summary>
    public string EventType { get; set; } = string.Empty;

    /// <summary>Índice del paso (0-based) si aplica; -1 si no.</summary>
    public int StepIndex { get; set; } = -1;

    /// <summary>"whatsapp" | "email" | null.</summary>
    public string? Channel { get; set; }

    /// <summary>Detalle libre (ej. motivo, error).</summary>
    public string? Detail { get; set; }
}
