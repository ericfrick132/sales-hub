namespace SalesHub.Core.Domain.Entities;

/// <summary>
/// Secuencia de follow-up GENÉRICA, identificada por (app, trigger). sales-hub es AGNÓSTICO:
/// el <see cref="Trigger"/> lo define cada app (ej. "otp_abandoned", "trial_ending", "signup_incomplete")
/// y se guarda opaco — sales-hub no sabe qué significa. Una app puede tener N secuencias.
/// Las apps la bajan vía GET /api/hub/followup-config y la ejecutan con su propio motor + canales.
/// Reportes de ejecución → <see cref="FollowupEvent"/>.
/// </summary>
public class FollowupSequence
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>App (gymhero, turnospro, …).</summary>
    public string ProductKey { get; set; } = string.Empty;

    /// <summary>Clave del abandono, definida por la app. Opaca para sales-hub.</summary>
    public string Trigger { get; set; } = string.Empty;

    public bool Enabled { get; set; }

    /// <summary>Pasos de la secuencia (jsonb).</summary>
    public List<FollowupStep> Steps { get; set; } = new();

    /// <summary>
    /// BACKLOG (abandonos VIEJOS): mensaje de "reactivación" frío para los más rancios
    /// (más viejos que <see cref="BacklogColdAfterDays"/>). Opaco para sales-hub; la app lo
    /// renderiza. Null = la app usa su fallback hardcodeado. No es un paso del flujo en vivo.
    /// </summary>
    public string? BacklogColdMessage { get; set; }

    /// <summary>
    /// BACKLOG (abandonos VIEJOS): mensaje "tibio" para los abandonos recientes dentro de la
    /// ventana de backlog. Null = la app usa su fallback hardcodeado.
    /// </summary>
    public string? BacklogWarmMessage { get; set; }

    /// <summary>
    /// Umbral en días para que un abandono del backlog pase de "tibio" a "frío". Null = la app
    /// usa su default (ej. env OtpFollowup:ColdAfterDays o su literal).
    /// </summary>
    public int? BacklogColdAfterDays { get; set; }

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// Paso genérico de una secuencia (serializado a jsonb). <see cref="Channel"/> es un string
/// opaco que cada app mapea a su propio emisor (la app decide qué soporta).
/// </summary>
public class FollowupStep
{
    public int Order { get; set; }

    /// <summary>Minutos desde el abandono (no desde el paso anterior) hasta mandar este paso.</summary>
    public int DelayMinutes { get; set; }

    /// <summary>Canal: la app decide qué significa (ej. "whatsapp", "email", "sms", "push").</summary>
    public string Channel { get; set; } = string.Empty;

    /// <summary>Asunto opcional (lo usa el canal que lo necesite, ej. email).</summary>
    public string? Subject { get; set; }

    /// <summary>Cuerpo/plantilla del mensaje. La app hace el render de placeholders (ej. {code}, {name}).</summary>
    public string Body { get; set; } = string.Empty;
}
