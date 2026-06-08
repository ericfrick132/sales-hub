namespace SalesHub.Core.Domain.Entities;

/// <summary>
/// Un sitio web sobre el que el motor de SEO/GEO trabaja. Hay uno por cada app
/// (GymHero, TurnosPro, etc.). Concentra el contexto que el modelo necesita para
/// generar contenido alineado con el nicho, la audiencia y la voz de marca, y la
/// configuración del "agente 24/7" que arma el plan de contenido.
/// </summary>
public class SeoSite
{
    public Guid Id { get; set; }

    /// <summary>Clave opcional del Product asociado en el CRM (ej. "gymhero"). Sirve para enlazar ventas ↔ contenido.</summary>
    public string? ProductKey { get; set; }

    public string Name { get; set; } = string.Empty;           // "GymHero"
    public string Domain { get; set; } = string.Empty;         // "gymhero.app"
    public string Language { get; set; } = "es";               // idioma del contenido

    /// <summary>Nicho/sector. Ej. "software de gestión para gimnasios".</summary>
    public string Sector { get; set; } = string.Empty;

    /// <summary>A quién le hablamos. Ej. "dueños de gimnasios y entrenadores que gestionan socios".</summary>
    public string Audience { get; set; } = string.Empty;

    /// <summary>Resumen del producto / propuesta de valor — se inyecta en los prompts.</summary>
    public string ProductSummary { get; set; } = string.Empty;

    /// <summary>Tono y guías de estilo de la marca.</summary>
    public string BrandVoice { get; set; } = string.Empty;

    /// <summary>Mercados objetivo para la estrategia multi-geográfica. Ej. ["Uruguay","Colombia","Argentina"].</summary>
    public List<string> TargetCountries { get; set; } = new();

    /// <summary>URL base del blog para construir slugs/links internos. Ej. "https://gymhero.app/blog".</summary>
    public string BlogBaseUrl { get; set; } = string.Empty;

    /// <summary>
    /// Modo autónomo: si está activo, el agente genera Y publica artículos solo, sin
    /// revisión humana, según <see cref="PublishCadenceHours"/>. El humano solo configura la cadencia.
    /// </summary>
    public bool AutoPublish { get; set; } = false;

    /// <summary>Cadencia de publicación en HORAS (ej. 24 = uno por día, 72 = cada 3 días). 0 = pausado.</summary>
    public int PublishCadenceHours { get; set; } = 0;

    /// <summary>(Legacy) Objetivo semanal del modo anterior. Se conserva; el modo autónomo usa la cadencia en horas.</summary>
    public int WeeklyTarget { get; set; } = 3;

    /// <summary>Cuántos artículos genera+publica en cada disparo de la cadencia (normalmente 1).</summary>
    public int PostsPerRun { get; set; } = 1;

    // --- Publicación al repo de la app (distribuidor de blog) ---
    /// <summary>Repo GitHub de la app, formato "owner/name" (ej. "ericfrick132/gymhero"). Vacío = no se publica.</summary>
    public string RepoFullName { get; set; } = string.Empty;
    /// <summary>Branch donde commitear (main/master).</summary>
    public string RepoBranch { get; set; } = "main";
    /// <summary>Carpeta servida estáticamente por el front; los artículos van a {dir}/blog/. Ej. "src/frontend/public".</summary>
    public string RepoPublicDir { get; set; } = "src/frontend/public";

    public bool IsActive { get; set; } = true;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    public ICollection<SeoKeyword> Keywords { get; set; } = new List<SeoKeyword>();
    public ICollection<SeoArticle> Articles { get; set; } = new List<SeoArticle>();
}
