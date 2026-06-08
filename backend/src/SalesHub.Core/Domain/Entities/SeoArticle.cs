using SalesHub.Core.Domain.Enums;

namespace SalesHub.Core.Domain.Entities;

/// <summary>
/// Una pieza de contenido generada por el motor. Incluye todo lo que un CMS
/// necesita (título, slug, meta, cuerpo en markdown) más los extras de GEO:
/// un bloque de FAQ y el JSON-LD schema.org para que los buscadores y las IAs
/// puedan citar el contenido como fuente. Nada se publica solo: pasa por revisión.
/// </summary>
public class SeoArticle
{
    public Guid Id { get; set; }

    public Guid SiteId { get; set; }
    public SeoSite? Site { get; set; }

    public Guid? KeywordId { get; set; }
    public SeoKeyword? Keyword { get; set; }

    /// <summary>Keyword principal que ataca el artículo (copia textual para no depender del FK).</summary>
    public string TargetKeyword { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;
    public string Slug { get; set; } = string.Empty;
    public string MetaDescription { get; set; } = string.Empty;

    /// <summary>Cuerpo completo en Markdown (con H2/H3, listas, etc.).</summary>
    public string BodyMarkdown { get; set; } = string.Empty;

    /// <summary>FAQ para GEO: array JSON de { question, answer }. Alimenta el FAQPage schema.</summary>
    public string FaqJson { get; set; } = "[]";

    /// <summary>JSON-LD schema.org (Article + FAQPage) listo para inyectar en el &lt;head&gt;.</summary>
    public string JsonLd { get; set; } = string.Empty;

    public SeoContentType ContentType { get; set; } = SeoContentType.Article;
    public SeoArticleStatus Status { get; set; } = SeoArticleStatus.Draft;

    /// <summary>Auto-evaluación SEO 0-100 que hace el modelo sobre su propia salida.</summary>
    public int? SeoScore { get; set; }

    /// <summary>Sugerencias de mejora (optimización en tiempo real).</summary>
    public string OptimizationNotes { get; set; } = string.Empty;

    public int WordCount { get; set; }

    /// <summary>Modelo que lo generó. Ej. "claude-sonnet-4-6".</summary>
    public string GeneratedBy { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? PublishedAt { get; set; }
}
