namespace SalesHub.Infrastructure.Services;

/// <summary>
/// Intención del prospecto detectada por la IA al analizar la conversación.
/// El <c>ConversationAgentService</c> la mapea a un <c>LeadStatus</c>.
/// </summary>
public enum LeadIntent
{
    /// <summary>No hay señal clara todavía.</summary>
    Unknown = 0,
    /// <summary>Muestra interés → se mantiene abierto y se le sigue ofreciendo.</summary>
    Interested = 1,
    /// <summary>Dijo claramente que no → se da por cerrado (Lost).</summary>
    NotInterested = 2,
    /// <summary>Acordó una demo/llamada/reunión.</summary>
    Scheduled = 3,
    /// <summary>Ya compró/cerró.</summary>
    Won = 4,
    /// <summary>Requiere intervención humana (reclamo, caso delicado).</summary>
    NeedsHuman = 5
}
