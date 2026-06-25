namespace SalesHub.Core.Domain.Entities;

/// <summary>
/// Registro de uso de la API de Claude (Anthropic): tokens + costo USD calculado, por
/// feature (conversación de ventas / SEO / social). Alimenta el widget de gasto del dashboard.
/// </summary>
public class AiUsageLog
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Dónde se gastó: "conversacion" | "seo" | "social" | "unknown".</summary>
    public string Feature { get; set; } = "unknown";
    public string Model { get; set; } = string.Empty;

    public int InputTokens { get; set; }
    public int OutputTokens { get; set; }
    public int CacheReadTokens { get; set; }
    public int CacheCreationTokens { get; set; }

    public decimal CostUsd { get; set; }
}
