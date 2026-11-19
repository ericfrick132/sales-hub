namespace SalesHub.Core.Domain.Entities;

/// <summary>
/// Un contacto nuevo que abrió una línea outbound. Es el CONTADOR del tope de por vida de la línea
/// (<see cref="EvolutionInstance.OutboundCap"/>) y el registro de a quién se le escribió, con qué
/// puntaje y qué contestó. La fila se crea ANTES de mandar (Reserved): si el proceso se cae en el
/// medio, el lugar queda gastado y nunca se pasa del tope.
/// </summary>
public class OutboundContact
{
    public const string Reserved = "reserved";
    public const string Sent = "sent";
    public const string Failed = "failed";

    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Instancia de Evolution de la línea outbound (evolution_instances.instance_name).</summary>
    public string InstanceName { get; set; } = string.Empty;

    public Guid LeadId { get; set; }
    public Lead? Lead { get; set; }

    /// <summary>Puntaje de prospecto que tenía el lead cuando se lo eligió.</summary>
    public int Score { get; set; }

    public string Message { get; set; } = string.Empty;

    /// <summary>reserved / sent / failed. Los tres cuentan para el tope.</summary>
    public string Status { get; set; } = Reserved;
    public string? Error { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? SentAt { get; set; }

    /// <summary>Primera respuesta del lead (dispara el pase a la persona).</summary>
    public DateTimeOffset? RepliedAt { get; set; }
}
