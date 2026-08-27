namespace SalesHub.Core.Domain.Entities;

/// <summary>
/// Estado de un lead dentro de un <see cref="Pitch"/> (equivale al "enrollment" de GHL).
/// Un lead tiene como máximo un estado (el pitch en el que entró).
/// </summary>
public class LeadPitchState
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid LeadId { get; set; }
    public Lead? Lead { get; set; }
    public Guid PitchId { get; set; }
    public Pitch? Pitch { get; set; }

    /// <summary>Índice del último paso ENVIADO (-1 = todavía ninguno).</summary>
    public int StepIndex { get; set; } = -1;
    /// <summary>Cuándo salió el último paso.</summary>
    public DateTimeOffset? StepSentAt { get; set; }
    /// <summary>Cuándo corresponde mandar el próximo paso (null = no hay pendiente).</summary>
    public DateTimeOffset? NextStepDueAt { get; set; }
    /// <summary>Follow-ups ya enviados del paso actual.</summary>
    public int FollowupsSent { get; set; }
    /// <summary>Cuándo salió el último follow-up del paso actual.</summary>
    public DateTimeOffset? LastFollowupAt { get; set; }
    /// <summary>El lead respondió al menos una vez DESPUÉS de recibir algún paso.</summary>
    public DateTimeOffset? FirstReplyAfterPitchAt { get; set; }
    /// <summary>Cantidad de respuestas del lead durante el pitch.</summary>
    public int Replies { get; set; }
    /// <summary>Todos los pasos enviados y respondidos → el guion terminó.</summary>
    public DateTimeOffset? CompletedAt { get; set; }
    /// <summary>Se agotaron los follow-ups sin respuesta.</summary>
    public DateTimeOffset? GaveUpAt { get; set; }
    public DateTimeOffset EnrolledAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
