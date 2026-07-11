using SalesHub.Core.Domain.Enums;

namespace SalesHub.Core.Domain.Entities;

/// <summary>
/// "Problema abierto hasta resolverlo": mientras un lead tenga un case no-Resolved, sus
/// inbounds entran al pipeline de SOPORTE (FAQ → LLM+KB → escalación) en vez del de venta.
/// El case cierra cuando el cliente confirma que se resolvió, cuando un humano lo marca
/// en la UI, o por auto-resolve tras días sin novedades.
/// </summary>
public class SupportCase
{
    public Guid Id { get; set; }

    public Guid LeadId { get; set; }
    public Lead? Lead { get; set; }

    public string ProductKey { get; set; } = string.Empty;

    public SupportCaseStatus Status { get; set; } = SupportCaseStatus.Open;

    /// <summary>Turnos de bot consumidos en este case (tope Support:MaxBotTurns → escala).</summary>
    public int BotTurns { get; set; }

    /// <summary>Resumen corto del problema (el primer mensaje que abrió el case, truncado).</summary>
    public string? Summary { get; set; }

    public DateTimeOffset OpenedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LastBotReplyAt { get; set; }
    public DateTimeOffset? EscalatedAt { get; set; }
    public DateTimeOffset? ResolvedAt { get; set; }
}
