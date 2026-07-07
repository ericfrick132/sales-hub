namespace SalesHub.Core.Domain.Entities.Social;

/// <summary>
/// Perfil de posteo por producto (1×ProductKey). Contiene la "base de marca"
/// fija (colores/fuentes/logo/tono), los pilares de contenido, el mapeo a los
/// canales de Buffer y la cadencia. El generador de contenido (Claude) toma
/// estos datos para producir cada posteo: la marca no varía, el resto sí.
/// </summary>
public class PostingProfile
{
    public Guid Id { get; set; }

    /// <summary>Liga al Product.ProductKey (ej. "gymhero").</summary>
    public string ProductKey { get; set; } = string.Empty;

    /// <summary>Si está en false, el worker no genera contenido para este producto.</summary>
    public bool Enabled { get; set; } = true;

    // ── Base de marca (fija) ──────────────────────────────────────────────
    /// <summary>JSON con la paleta: {"primary":"#DDFD42","bg":"#0A0A0A",...}.</summary>
    public string BrandColorsJson { get; set; } = "{}";
    public string BrandFonts { get; set; } = string.Empty;
    public string BrandLogoUrl { get; set; } = string.Empty;
    /// <summary>Tono/voz de marca (ej. "directo, sin vueltas, rioplatense").</summary>
    public string BrandVoice { get; set; } = string.Empty;
    /// <summary>Guía libre: qué decir / qué NO decir, claims, formato visual.</summary>
    public string BrandGuidelines { get; set; } = string.Empty;
    /// <summary>A quién le habla (ej. "dueños de gimnasios — B2B").</summary>
    public string TargetAudience { get; set; } = string.Empty;
    /// <summary>Pilares de contenido (ej. ahorro de tiempo, cobros, retención…).</summary>
    public List<string> ContentPillars { get; set; } = new();

    // ── Conocimiento de la landing (fuente real de features/precios) ────────
    /// <summary>URL de la landing del producto. De acá se destilan los datos reales.</summary>
    public string LandingUrl { get; set; } = string.Empty;
    /// <summary>Ficha destilada por Claude a partir de la landing (qué hace, features, precios, pruebas). Se cachea.</summary>
    public string LandingKnowledge { get; set; } = string.Empty;
    /// <summary>Cuándo se refrescó la ficha (para re-fetchear cuando está vieja).</summary>
    public DateTimeOffset? LandingKnowledgeAt { get; set; }

    // ── Canales ───────────────────────────────────────────────────────────
    /// <summary>Mapa plataforma→bufferChannelId, ej {"instagram":"68ed..","tiktok":"686f.."}.</summary>
    public string BufferChannelsJson { get; set; } = "{}";

    // ── Cadencia ──────────────────────────────────────────────────────────
    /// <summary>Horas del día (0-23, hora local) en las que se genera contenido.</summary>
    public List<int> PostHours { get; set; } = new();
    /// <summary>Días de la semana en los que postea (0=Domingo … 6=Sábado). Vacío = todos los días.</summary>
    public List<int> PostDays { get; set; } = new();
    /// <summary>Cuántos posteos generar por corrida/día.</summary>
    public int PostsPerDay { get; set; } = 1;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
