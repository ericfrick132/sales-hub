namespace SalesHub.Core.Domain.Enums;

/// <summary>Intención de búsqueda detrás de una keyword (modelo clásico de SEO).</summary>
public enum SeoIntent
{
    Informational = 0,  // "qué es un software de gestión de gimnasios"
    Commercial = 1,     // "mejor software para gimnasios"
    Transactional = 2,  // "contratar software gimnasio precio"
    Navigational = 3    // "gymhero login"
}

/// <summary>Estado de una keyword dentro del plan de contenido.</summary>
public enum SeoKeywordStatus
{
    Idea = 0,      // recién descubierta, sin decidir
    Planned = 1,   // elegida para escribir
    Used = 2,      // ya se generó un artículo para ella
    Ignored = 3    // descartada
}

/// <summary>Tipo de pieza de contenido a generar.</summary>
public enum SeoContentType
{
    Article = 0,      // artículo de blog informativo
    Guide = 1,        // guía larga / pillar page
    Faq = 2,          // página de preguntas frecuentes (fuerte para GEO)
    Comparison = 3,   // "X vs Y", comparativas comerciales
    Landing = 4       // landing orientada a conversión
}

/// <summary>Workflow editorial. Nada se publica solo: el humano aprueba.</summary>
public enum SeoArticleStatus
{
    Draft = 0,        // generado, sin revisar
    NeedsReview = 1,  // generado por el agente, esperando revisión humana
    Approved = 2,     // revisado y aprobado, listo para publicar en el CMS
    Published = 3,    // marcado como publicado en el sitio
    Archived = 4      // dado de baja
}
