namespace SalesHub.Core.Domain.Entities;

/// <summary>
/// Tarea para Claude Code en la Mac de Eric, dictada por WhatsApp: una nota de voz en el self-chat de la
/// línea de transcripción se transcribe y queda acá (ClaudeTaskRelay). Un LaunchAgent en la Mac la toma
/// con <c>GET api/claude-tasks/next</c>, corre <c>claude -p</c> y devuelve el resumen, que le llega a Eric
/// por WhatsApp. Si la Mac está apagada queda en cola hasta 12 h.
/// </summary>
public class ClaudeTask
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Línea por la que llegó el audio (y por la que se responde).</summary>
    public string InstanceName { get; set; } = string.Empty;
    /// <summary>Número (dígitos) al que se le manda el resultado: el dueño del self-chat.</summary>
    public string ReplyTo { get; set; } = string.Empty;

    /// <summary>Lo que dijo en el audio (transcripción de Whisper).</summary>
    public string Prompt { get; set; } = string.Empty;
    /// <summary>Si el audio arrancó con "seguí": sesión de Claude Code a retomar (la de la última tarea terminada).</summary>
    public string? ResumeSessionId { get; set; }

    /// <summary>"confirm" (esperando el sí de Eric), "pending", "running", "done", "failed", "expired" o "cancelled".</summary>
    public string Status { get; set; } = "confirm";

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ClaimedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }

    /// <summary>Sesión de Claude Code que corrió la tarea (para retomarla con "seguí").</summary>
    public string? SessionId { get; set; }
    /// <summary>Último mensaje de Claude Code (lo que se manda por WhatsApp).</summary>
    public string? Result { get; set; }
    public decimal? CostUsd { get; set; }
    public string? Error { get; set; }
}
