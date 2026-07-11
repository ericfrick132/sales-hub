namespace SalesHub.Core.Domain.Entities;

/// <summary>
/// Override de cadencia por ORIGEN del lead (LeadSource), ej. "MetaLeadAd".
/// Un lead que dejó sus datos en un form de Meta ya nos conoce — mandarle el
/// opener frío ("me pasaron tu número...") es incoherente. Si el origen del
/// lead tiene override con steps configurados, esos steps se usan SIEMPRE,
/// por encima del override por categoría y del default del producto.
/// </summary>
public class SourceCadence
{
    /// <summary>Nombre exacto del enum LeadSource (ej. "MetaLeadAd", "WhatsAppAd").</summary>
    public string Source { get; set; } = string.Empty;
    public List<MessageStep> Steps { get; set; } = new();
}
