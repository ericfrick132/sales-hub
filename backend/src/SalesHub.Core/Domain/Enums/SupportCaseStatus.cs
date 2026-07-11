namespace SalesHub.Core.Domain.Enums;

public enum SupportCaseStatus
{
    /// <summary>Problema reportado, el bot está conversando para resolverlo.</summary>
    Open = 0,
    /// <summary>El bot no pudo/no debió seguir: avisado el humano, espera un vendedor.</summary>
    Escalated = 1,
    /// <summary>Cerrado (el cliente confirmó, un humano lo marcó, o auto-resolve por silencio).</summary>
    Resolved = 2,
}
