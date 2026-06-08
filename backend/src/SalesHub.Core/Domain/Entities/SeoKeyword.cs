using SalesHub.Core.Domain.Enums;

namespace SalesHub.Core.Domain.Entities;

/// <summary>
/// Una keyword/consulta detectada para un sitio. Las sugerencias reales salen del
/// autocompletado de Google (lo que la gente efectivamente busca) y el modelo las
/// agrupa en clusters, les asigna intención y un score de prioridad. Volume/Difficulty
/// quedan nulos salvo que se enchufe un proveedor de datos (DataForSEO/Ahrefs).
/// </summary>
public class SeoKeyword
{
    public Guid Id { get; set; }

    public Guid SiteId { get; set; }
    public SeoSite? Site { get; set; }

    public string Term { get; set; } = string.Empty;

    /// <summary>Cluster temático al que pertenece (para construir autoridad por tema).</summary>
    public string Cluster { get; set; } = string.Empty;

    public SeoIntent Intent { get; set; } = SeoIntent.Informational;

    /// <summary>De dónde salió: "google-autocomplete", "ai", "manual".</summary>
    public string Source { get; set; } = "ai";

    /// <summary>Volumen mensual estimado. Null = no hay proveedor de datos conectado.</summary>
    public int? Volume { get; set; }

    /// <summary>Dificultad 0-100. Null = no hay proveedor de datos conectado.</summary>
    public int? Difficulty { get; set; }

    /// <summary>Score de prioridad 0-100 asignado por el modelo (relevancia × intención × oportunidad).</summary>
    public int Priority { get; set; }

    public SeoKeywordStatus Status { get; set; } = SeoKeywordStatus.Idea;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
