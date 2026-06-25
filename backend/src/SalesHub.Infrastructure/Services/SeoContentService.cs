using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SalesHub.Core.Domain.Entities;
using SalesHub.Core.Domain.Enums;
using SalesHub.Infrastructure.Options;
using SalesHub.Infrastructure.Persistence;

namespace SalesHub.Infrastructure.Services;

/// <summary>
/// Motor de SEO/GEO casero. Reemplaza lo central de un SaaS tipo SEO.ai:
///  - Investigación de keywords: autocompletado real de Google + clusterización/priorización con IA.
///  - Generación de contenido: artículos optimizados (markdown) + bloque FAQ y JSON-LD para GEO.
///  - Optimización: auto-evaluación y sugerencias sobre un borrador existente.
/// Todo lo que produce queda en estado de revisión: nada se publica solo.
/// </summary>
public class SeoContentService
{
    private readonly ApplicationDbContext _db;
    private readonly ClaudeClient _claude;
    private readonly GoogleAutocompleteClient _autocomplete;
    private readonly SeoOptions _opts;
    private readonly ILogger<SeoContentService> _log;

    public SeoContentService(
        ApplicationDbContext db,
        ClaudeClient claude,
        GoogleAutocompleteClient autocomplete,
        IOptions<SeoOptions> opts,
        ILogger<SeoContentService> log)
    {
        _db = db;
        _claude = claude;
        _autocomplete = autocomplete;
        _opts = opts.Value;
        _log = log;
    }

    public bool IsConfigured => _claude.IsConfigured;

    // ---------------------------------------------------------------- KEYWORDS

    /// <summary>
    /// Investiga keywords para un sitio a partir de un tema semilla. Mezcla las
    /// sugerencias reales de Google (lo que se busca) con la clusterización del
    /// modelo (intención + prioridad + long-tail/preguntas). Persiste las nuevas.
    /// </summary>
    public async Task<List<SeoKeyword>> ResearchKeywordsAsync(Guid siteId, string seedTopic, CancellationToken ct = default)
    {
        var site = await _db.SeoSites.FirstOrDefaultAsync(s => s.Id == siteId, ct);
        if (site is null) throw new InvalidOperationException("Sitio no encontrado");
        if (!_claude.IsConfigured) throw new InvalidOperationException("Claude no está configurado (falta API key)");

        var country = site.TargetCountries.FirstOrDefault() is { } c ? CountryCode(c) : "ar";
        var seed = string.IsNullOrWhiteSpace(seedTopic) ? site.Sector : seedTopic;

        // 1) Datos reales y gratis: lo que la gente tipea en Google.
        var suggestions = await _autocomplete.SuggestAsync(seed, site.Language, country, ct);

        // 2) El modelo agrupa, le pone intención/prioridad y agrega long-tail/preguntas (GEO).
        var system = BuildKeywordSystemPrompt(site);
        var user = new StringBuilder();
        user.AppendLine($"TEMA SEMILLA: {seed}");
        if (suggestions.Count > 0)
        {
            user.AppendLine();
            user.AppendLine("SUGERENCIAS REALES DE GOOGLE (priorizá estas, son búsquedas reales):");
            foreach (var s in suggestions.Take(60)) user.AppendLine($"- {s}");
        }
        user.AppendLine();
        user.AppendLine("Devolvé entre 25 y 40 keywords. Incluí preguntas que un usuario le haría a ChatGPT (para GEO).");

        var raw = await _claude.CompleteAsync(system.ToString(), user.ToString(), _opts.ResearchMaxTokens, _opts.ResearchModel, "seo", ct);
        if (string.IsNullOrWhiteSpace(raw)) return new();

        var parsed = ParseKeywordArray(raw);
        if (parsed.Count == 0)
        {
            _log.LogWarning("ResearchKeywords: no se pudo parsear JSON de keywords para sitio {Site}", site.Name);
            return new();
        }

        // 3) Dedup contra lo ya guardado y persistir las nuevas.
        var existing = await _db.SeoKeywords
            .Where(k => k.SiteId == siteId)
            .Select(k => k.Term.ToLower())
            .ToListAsync(ct);
        var existingSet = new HashSet<string>(existing, StringComparer.OrdinalIgnoreCase);

        var created = new List<SeoKeyword>();
        foreach (var p in parsed)
        {
            if (string.IsNullOrWhiteSpace(p.Term)) continue;
            var term = p.Term.Trim();
            if (existingSet.Contains(term)) continue;
            existingSet.Add(term);

            var kw = new SeoKeyword
            {
                Id = Guid.NewGuid(),
                SiteId = siteId,
                Term = term,
                Cluster = p.Cluster ?? string.Empty,
                Intent = ParseIntent(p.Intent),
                Source = suggestions.Any(s => string.Equals(s, term, StringComparison.OrdinalIgnoreCase)) ? "google-autocomplete" : "ai",
                Priority = Math.Clamp(p.Priority, 0, 100),
                Status = SeoKeywordStatus.Idea
            };
            _db.SeoKeywords.Add(kw);
            created.Add(kw);
        }

        await _db.SaveChangesAsync(ct);
        _log.LogInformation("ResearchKeywords: {New} keywords nuevas para {Site} (semilla '{Seed}')", created.Count, site.Name, seed);
        return created.OrderByDescending(k => k.Priority).ToList();
    }

    // ---------------------------------------------------------------- ARTICLES

    /// <summary>
    /// Genera una pieza completa (título, slug, meta, cuerpo markdown, FAQ + JSON-LD)
    /// para una keyword. Queda persistida en el estado indicado (por defecto NeedsReview).
    /// </summary>
    public async Task<SeoArticle?> GenerateArticleAsync(
        Guid siteId, string keyword, SeoContentType type, Guid? keywordId = null,
        SeoArticleStatus status = SeoArticleStatus.NeedsReview, CancellationToken ct = default)
    {
        var site = await _db.SeoSites.FirstOrDefaultAsync(s => s.Id == siteId, ct);
        if (site is null) throw new InvalidOperationException("Sitio no encontrado");
        if (!_claude.IsConfigured) throw new InvalidOperationException("Claude no está configurado (falta API key)");
        if (string.IsNullOrWhiteSpace(keyword)) throw new InvalidOperationException("Falta la keyword");

        var system = BuildArticleSystemPrompt(site);
        var user = BuildArticleUserPrompt(site, keyword, type);

        var raw = await _claude.CompleteAsync(system, user, _opts.ArticleMaxTokens, _opts.ArticleModel, "seo", ct);
        if (string.IsNullOrWhiteSpace(raw)) return null;

        if (!TryExtractJsonObject(raw, out var json))
        {
            _log.LogWarning("GenerateArticle: respuesta sin JSON parseable para '{Keyword}'", keyword);
            return null;
        }

        using var doc = json;
        var root = doc.RootElement;
        var title = Str(root, "title");
        var body = Str(root, "bodyMarkdown");
        if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(body)) return null;

        var slug = Str(root, "slug");
        if (string.IsNullOrWhiteSpace(slug)) slug = Slugify(title);

        var faq = ExtractFaq(root);
        var article = new SeoArticle
        {
            Id = Guid.NewGuid(),
            SiteId = siteId,
            KeywordId = keywordId,
            TargetKeyword = keyword,
            Title = Truncate(title, 500),
            Slug = Truncate(slug, 500),
            MetaDescription = Truncate(Str(root, "metaDescription"), 1000),
            BodyMarkdown = body,
            FaqJson = JsonSerializer.Serialize(faq),
            ContentType = type,
            Status = status,
            SeoScore = IntOrNull(root, "seoScore"),
            OptimizationNotes = Str(root, "optimizationNotes"),
            WordCount = CountWords(body),
            GeneratedBy = _opts.ArticleModel
        };
        article.JsonLd = BuildJsonLd(site, article, faq);

        _db.SeoArticles.Add(article);

        if (keywordId is { } kid)
        {
            var kw = await _db.SeoKeywords.FirstOrDefaultAsync(k => k.Id == kid, ct);
            if (kw is not null) kw.Status = SeoKeywordStatus.Used;
        }

        await _db.SaveChangesAsync(ct);
        _log.LogInformation("GenerateArticle: '{Title}' ({Words} palabras) para {Site}", article.Title, article.WordCount, site.Name);
        return article;
    }

    /// <summary>
    /// Auto-evalúa un borrador contra su keyword y devuelve score + sugerencias de
    /// mejora (no reescribe el cuerpo: el humano decide qué aplicar).
    /// </summary>
    public async Task<SeoArticle?> OptimizeAsync(Guid articleId, CancellationToken ct = default)
    {
        var article = await _db.SeoArticles.Include(a => a.Site).FirstOrDefaultAsync(a => a.Id == articleId, ct);
        if (article is null) throw new InvalidOperationException("Artículo no encontrado");
        if (!_claude.IsConfigured) throw new InvalidOperationException("Claude no está configurado (falta API key)");

        var system = "Sos un auditor SEO/GEO experto. Evaluás un borrador contra su keyword objetivo y devolvés " +
                     "ÚNICAMENTE un objeto JSON válido con esta forma: " +
                     "{\"seoScore\": number 0-100, \"optimizationNotes\": \"bullets concretos de mejora (estructura, " +
                     "keyword, headings, enlazado interno, snippets/AEO, citabilidad por IAs)\"}. Sin texto adicional.";
        var user = new StringBuilder();
        user.AppendLine($"KEYWORD OBJETIVO: {article.TargetKeyword}");
        user.AppendLine($"TÍTULO: {article.Title}");
        user.AppendLine($"META: {article.MetaDescription}");
        user.AppendLine();
        user.AppendLine("CUERPO (markdown):");
        user.AppendLine(article.BodyMarkdown);

        var raw = await _claude.CompleteAsync(system, user.ToString(), 1500, _opts.ResearchModel, "seo", ct);
        if (string.IsNullOrWhiteSpace(raw) || !TryExtractJsonObject(raw, out var json)) return article;

        using var doc = json;
        var root = doc.RootElement;
        var score = IntOrNull(root, "seoScore");
        if (score is not null) article.SeoScore = Math.Clamp(score.Value, 0, 100);
        var notes = Str(root, "optimizationNotes");
        if (!string.IsNullOrWhiteSpace(notes)) article.OptimizationNotes = notes;
        article.UpdatedAt = DateTimeOffset.UtcNow;

        await _db.SaveChangesAsync(ct);
        return article;
    }

    // ---------------------------------------------------------------- PROMPTS

    private static StringBuilder BuildKeywordSystemPrompt(SeoSite site)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Sos un estratega de SEO y GEO (Generative Engine Optimization).");
        sb.AppendLine("Tu tarea: a partir de un tema y sugerencias reales de Google, armar una lista de keywords");
        sb.AppendLine("priorizadas para construir autoridad temática en el nicho del sitio.");
        AppendSiteContext(sb, site);
        sb.AppendLine();
        sb.AppendLine("REGLAS:");
        sb.AppendLine("- Mezclá head terms con long-tail y, sobre todo, PREGUNTAS que la audiencia le haría a una IA (clave para GEO).");
        sb.AppendLine("- Agrupá en clusters temáticos coherentes.");
        sb.AppendLine("- Asigná intención: informational | commercial | transactional | navigational.");
        sb.AppendLine("- Priority 0-100: relevancia para el negocio × intención de compra × probabilidad de rankear.");
        sb.AppendLine();
        sb.AppendLine("Respondé ÚNICAMENTE con un array JSON, sin texto ni fences:");
        sb.AppendLine("[{\"term\":\"...\",\"cluster\":\"...\",\"intent\":\"informational\",\"priority\":80}, ...]");
        return sb;
    }

    private static string BuildArticleSystemPrompt(SeoSite site)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Sos un redactor SEO/GEO senior. Escribís contenido que rankea en Google Y que las IAs (ChatGPT,");
        sb.AppendLine("Gemini, Perplexity) citan como fuente confiable. Escribís en el idioma del sitio.");
        AppendSiteContext(sb, site);
        sb.AppendLine();
        sb.AppendLine("PRINCIPIOS:");
        sb.AppendLine("- E-E-A-T: demostrá experiencia y autoridad concreta del nicho, sin relleno.");
        sb.AppendLine("- Estructura escaneable: H2/H3 claros, párrafos cortos, listas, tablas cuando sumen.");
        sb.AppendLine("- AEO/GEO: respondé la pregunta principal en las primeras 2-3 líneas (answer-first) para que");
        sb.AppendLine("  sea citable por IAs y gane featured snippets. Incluí una sección de FAQ.");
        sb.AppendLine("- Usá la keyword objetivo de forma natural (título, primer párrafo, algún H2), sin keyword stuffing.");
        sb.AppendLine("- Mencioná el producto del sitio donde aporte valor real, sin sonar a publicidad agresiva.");
        sb.AppendLine();
        sb.AppendLine("Respondé ÚNICAMENTE con un objeto JSON válido (sin fences ni texto extra) con esta forma:");
        sb.AppendLine("{");
        sb.AppendLine("  \"title\": \"título atractivo y optimizado (<=60 chars ideal)\",");
        sb.AppendLine("  \"slug\": \"slug-en-kebab-case\",");
        sb.AppendLine("  \"metaDescription\": \"meta description persuasiva (<=155 chars)\",");
        sb.AppendLine("  \"bodyMarkdown\": \"el artículo completo en Markdown con H2/H3, listas, etc.\",");
        sb.AppendLine("  \"faq\": [{\"question\":\"...\",\"answer\":\"...\"}, ...],");
        sb.AppendLine("  \"seoScore\": number 0-100,");
        sb.AppendLine("  \"optimizationNotes\": \"qué se podría mejorar en una próxima iteración\"");
        sb.AppendLine("}");
        return sb.ToString();
    }

    private static string BuildArticleUserPrompt(SeoSite site, string keyword, SeoContentType type)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"KEYWORD OBJETIVO: {keyword}");
        sb.AppendLine($"TIPO DE CONTENIDO: {ContentTypeHint(type)}");
        if (site.TargetCountries.Count > 0)
            sb.AppendLine($"MERCADOS: {string.Join(", ", site.TargetCountries)} (usá español neutro pero con ejemplos locales si ayuda).");
        sb.AppendLine();
        var (min, max) = WordRange(type);
        sb.AppendLine($"EXTENSIÓN: entre {min} y {max} palabras.");
        sb.AppendLine("Generá la pieza ahora.");
        return sb.ToString();
    }

    private static void AppendSiteContext(StringBuilder sb, SeoSite site)
    {
        sb.AppendLine();
        sb.AppendLine($"SITIO: {site.Name} ({site.Domain})");
        sb.AppendLine($"IDIOMA: {site.Language}");
        if (!string.IsNullOrWhiteSpace(site.Sector)) sb.AppendLine($"NICHO: {site.Sector}");
        if (!string.IsNullOrWhiteSpace(site.Audience)) sb.AppendLine($"AUDIENCIA: {site.Audience}");
        if (!string.IsNullOrWhiteSpace(site.ProductSummary)) sb.AppendLine($"PRODUCTO: {site.ProductSummary}");
        if (!string.IsNullOrWhiteSpace(site.BrandVoice)) sb.AppendLine($"VOZ DE MARCA: {site.BrandVoice}");
    }

    private static string ContentTypeHint(SeoContentType t) => t switch
    {
        SeoContentType.Guide => "guía extensa / pillar page que cubre el tema en profundidad",
        SeoContentType.Faq => "página de preguntas frecuentes (muchas Q&A, fuerte para GEO)",
        SeoContentType.Comparison => "comparativa comercial (X vs Y), tono objetivo con tabla comparativa",
        SeoContentType.Landing => "landing orientada a conversión con CTA claros",
        _ => "artículo de blog informativo"
    };

    private static (int, int) WordRange(SeoContentType t) => t switch
    {
        SeoContentType.Guide => (1800, 2600),
        SeoContentType.Faq => (900, 1400),
        SeoContentType.Comparison => (1200, 1800),
        SeoContentType.Landing => (600, 1000),
        _ => (1100, 1600)
    };

    // ---------------------------------------------------------------- JSON-LD

    private static string BuildJsonLd(SeoSite site, SeoArticle article, List<FaqItem> faq)
    {
        var url = BuildArticleUrl(site, article.Slug);
        var graph = new List<object>
        {
            new Dictionary<string, object?>
            {
                ["@type"] = "Article",
                ["headline"] = article.Title,
                ["description"] = article.MetaDescription,
                ["inLanguage"] = site.Language,
                ["url"] = url,
                ["author"] = new Dictionary<string, object?> { ["@type"] = "Organization", ["name"] = site.Name },
                ["publisher"] = new Dictionary<string, object?> { ["@type"] = "Organization", ["name"] = site.Name }
            }
        };

        if (faq.Count > 0)
        {
            graph.Add(new Dictionary<string, object?>
            {
                ["@type"] = "FAQPage",
                ["mainEntity"] = faq.Select(f => new Dictionary<string, object?>
                {
                    ["@type"] = "Question",
                    ["name"] = f.Question,
                    ["acceptedAnswer"] = new Dictionary<string, object?> { ["@type"] = "Answer", ["text"] = f.Answer }
                }).ToList()
            });
        }

        var doc = new Dictionary<string, object?> { ["@context"] = "https://schema.org", ["@graph"] = graph };
        return JsonSerializer.Serialize(doc, new JsonSerializerOptions { WriteIndented = true });
    }

    private static string BuildArticleUrl(SeoSite site, string slug)
    {
        var baseUrl = !string.IsNullOrWhiteSpace(site.BlogBaseUrl)
            ? site.BlogBaseUrl
            : (!string.IsNullOrWhiteSpace(site.Domain) ? $"https://{site.Domain.Replace("https://", "").Replace("http://", "").TrimEnd('/')}/blog" : "");
        if (string.IsNullOrWhiteSpace(baseUrl)) return slug;
        return $"{baseUrl.TrimEnd('/')}/{slug}";
    }

    // ---------------------------------------------------------------- PARSING

    private record KwDto(string Term, string? Cluster, string? Intent, int Priority);
    public record FaqItem(string Question, string Answer);

    private List<KwDto> ParseKeywordArray(string raw)
    {
        if (!TryExtractJsonArray(raw, out var doc)) return new();
        using var _ = doc;
        var list = new List<KwDto>();
        foreach (var el in doc.RootElement.EnumerateArray())
        {
            if (el.ValueKind != JsonValueKind.Object) continue;
            var term = Str(el, "term");
            if (string.IsNullOrWhiteSpace(term)) continue;
            list.Add(new KwDto(term, Str(el, "cluster"), Str(el, "intent"), IntOrNull(el, "priority") ?? 50));
        }
        return list;
    }

    private static List<FaqItem> ExtractFaq(JsonElement root)
    {
        var list = new List<FaqItem>();
        if (root.TryGetProperty("faq", out var faq) && faq.ValueKind == JsonValueKind.Array)
        {
            foreach (var el in faq.EnumerateArray())
            {
                if (el.ValueKind != JsonValueKind.Object) continue;
                var q = Str(el, "question");
                var a = Str(el, "answer");
                if (!string.IsNullOrWhiteSpace(q) && !string.IsNullOrWhiteSpace(a))
                    list.Add(new FaqItem(q, a));
            }
        }
        return list;
    }

    private static bool TryExtractJsonObject(string raw, out JsonDocument doc)
        => TryExtractJson(raw, '{', '}', out doc);

    private static bool TryExtractJsonArray(string raw, out JsonDocument doc)
        => TryExtractJson(raw, '[', ']', out doc);

    private static bool TryExtractJson(string raw, char open, char close, out JsonDocument doc)
    {
        doc = null!;
        if (string.IsNullOrWhiteSpace(raw)) return false;
        var start = raw.IndexOf(open);
        var end = raw.LastIndexOf(close);
        if (start < 0 || end <= start) return false;
        var slice = raw.Substring(start, end - start + 1);
        try { doc = JsonDocument.Parse(slice); return true; }
        catch { return false; }
    }

    private static string Str(JsonElement el, string prop)
        => el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? (v.GetString() ?? "") : "";

    private static int? IntOrNull(JsonElement el, string prop)
    {
        if (!el.TryGetProperty(prop, out var v)) return null;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i)) return i;
        if (v.ValueKind == JsonValueKind.String && int.TryParse(v.GetString(), out var j)) return j;
        return null;
    }

    private static SeoIntent ParseIntent(string? s) => (s ?? "").Trim().ToLowerInvariant() switch
    {
        "commercial" => SeoIntent.Commercial,
        "transactional" => SeoIntent.Transactional,
        "navigational" => SeoIntent.Navigational,
        _ => SeoIntent.Informational
    };

    // ---------------------------------------------------------------- HELPERS

    private static int CountWords(string s)
        => string.IsNullOrWhiteSpace(s) ? 0 : s.Split(new[] { ' ', '\n', '\r', '\t' }, StringSplitOptions.RemoveEmptyEntries).Length;

    private static string Truncate(string s, int max)
        => string.IsNullOrEmpty(s) || s.Length <= max ? s : s.Substring(0, max);

    private static string Slugify(string title)
    {
        var lower = title.ToLowerInvariant().Trim();
        var sb = new StringBuilder();
        foreach (var ch in lower)
        {
            if (char.IsLetterOrDigit(ch)) sb.Append(StripAccent(ch));
            else if (ch is ' ' or '-' or '_') sb.Append('-');
        }
        var slug = sb.ToString();
        while (slug.Contains("--")) slug = slug.Replace("--", "-");
        return slug.Trim('-');
    }

    private static char StripAccent(char c) => c switch
    {
        'á' => 'a', 'é' => 'e', 'í' => 'i', 'ó' => 'o', 'ú' => 'u', 'ñ' => 'n', 'ü' => 'u',
        _ => c
    };

    private static string CountryCode(string country)
    {
        var c = country.Trim().ToLowerInvariant();
        return c switch
        {
            "argentina" or "ar" => "ar",
            "uruguay" or "uy" => "uy",
            "colombia" or "co" => "co",
            "chile" or "cl" => "cl",
            "méxico" or "mexico" or "mx" => "mx",
            "españa" or "espana" or "es" => "es",
            "perú" or "peru" or "pe" => "pe",
            _ => c.Length == 2 ? c : "ar"
        };
    }
}
