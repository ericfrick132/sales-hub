namespace SalesHub.Infrastructure.Options;

/// <summary>
/// Config del motor de SEO/GEO. Los artículos usan un modelo de mayor calidad
/// (Sonnet) y más tokens; la investigación de keywords usa Haiku (barato). Todo
/// configurable por env: Seo__ArticleModel, Seo__ArticleMaxTokens, Seo__AutoStart, etc.
/// </summary>
public class SeoOptions
{
    /// <summary>Modelo para generar/optimizar artículos. Calidad importa para el ranking.</summary>
    public string ArticleModel { get; set; } = "claude-sonnet-4-6";
    public int ArticleMaxTokens { get; set; } = 8000;

    /// <summary>Modelo para clusterizar/priorizar keywords. Tarea liviana → Haiku.</summary>
    public string ResearchModel { get; set; } = "claude-haiku-4-5-20251001";
    public int ResearchMaxTokens { get; set; } = 3000;

    /// <summary>Si el worker de fondo (agente 24/7) corre. Las piezas siempre quedan en revisión.</summary>
    public bool AutoStart { get; set; } = false;

    /// <summary>Cada cuánto despierta el worker a chequear backlog por sitio.</summary>
    public int WorkerIntervalMinutes { get; set; } = 360;
}
