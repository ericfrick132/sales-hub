namespace SalesHub.Core.Domain.Entities;

/// <summary>
/// Knowledge base de SOPORTE por producto — la capa 2 del bot. Documento generado desde
/// el REPO de cada app en el deploy (features, pantallas y rutas, cómo se hace cada cosa,
/// errores comunes, precios) y subido vía POST /api/hub/knowledge. NO se genera en runtime:
/// el system prompt lo inyecta tal cual (y se beneficia del prompt caching porque solo
/// cambia con cada deploy).
/// </summary>
public class ProductKnowledge
{
    public Guid Id { get; set; }

    /// <summary>Único por producto: upsert por productKey.</summary>
    public string ProductKey { get; set; } = string.Empty;

    public string Content { get; set; } = string.Empty;

    /// <summary>Versión de origen (sha del commit del repo que generó el doc).</summary>
    public string? SourceVersion { get; set; }

    public DateTimeOffset UploadedAt { get; set; } = DateTimeOffset.UtcNow;
}
