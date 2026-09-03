using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SalesHub.Core.Domain.Entities;
using SalesHub.Core.Domain.Enums;
using SalesHub.Infrastructure.Persistence;
using SalesHub.Infrastructure.Services;

namespace SalesHub.Infrastructure.Seed;

/// <summary>
/// Importa al blog central los artículos SEO escritos a mano como archivos del repo:
/// <c>SeoSeed/&lt;siteKey&gt;/&lt;slug&gt;.json</c> (metadata) + <c>&lt;slug&gt;.md</c> (cuerpo).
/// Corre al arrancar la API, después de las migraciones. Es idempotente (busca por
/// SiteId + Slug), solo pisa artículos que él mismo creó (<see cref="GeneratedByMarker"/>)
/// y nunca lanza: cada archivo se procesa en su propio try/catch y se loguea un resumen.
/// El formato está documentado en <c>SalesHub.Api/SeoSeed/README.md</c>.
/// </summary>
public static class SeoSeedImporter
{
    /// <summary>Valor de <see cref="SeoArticle.GeneratedBy"/> que marca un artículo como "dueño" de este importador.</summary>
    public const string GeneratedByMarker = "seed-import";

    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private enum Outcome { Created, Updated, Unchanged, Skipped }

    public static async Task ImportAsync(ApplicationDbContext db, string folder, ILogger log, CancellationToken ct = default)
    {
        if (!Directory.Exists(folder))
        {
            log.LogInformation("SeoSeed: la carpeta {Folder} no existe, nada que importar", folder);
            return;
        }

        var files = Directory.EnumerateFiles(folder, "*.json", SearchOption.AllDirectories)
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToList();
        if (files.Count == 0)
        {
            log.LogInformation("SeoSeed: sin archivos .json en {Folder}, nada que importar", folder);
            return;
        }

        // AsNoTracking: así ChangeTracker.Clear() tras un archivo roto no nos deja sin sitios.
        var sites = await db.SeoSites.AsNoTracking()
            .Where(s => s.IsActive && s.ProductKey != null && s.ProductKey != "")
            .ToListAsync(ct);

        int created = 0, updated = 0, unchanged = 0, skipped = 0, failed = 0;
        foreach (var jsonPath in files)
        {
            var rel = Path.GetRelativePath(folder, jsonPath);
            try
            {
                switch (await ImportOneAsync(db, sites, jsonPath, rel, log, ct))
                {
                    case Outcome.Created: created++; break;
                    case Outcome.Updated: updated++; break;
                    case Outcome.Unchanged: unchanged++; break;
                    default: skipped++; break;
                }
            }
            catch (Exception ex)
            {
                failed++;
                log.LogError(ex, "SeoSeed: error importando {File}", rel);
                // Que un archivo roto no contamine el SaveChanges del siguiente.
                db.ChangeTracker.Clear();
            }
        }

        log.LogInformation(
            "SeoSeed: {Files} archivo(s) en {Folder} — creados {Created}, actualizados {Updated}, sin cambios {Unchanged}, omitidos {Skipped}, con error {Failed}",
            files.Count, folder, created, updated, unchanged, skipped, failed);
    }

    private static async Task<Outcome> ImportOneAsync(
        ApplicationDbContext db, List<SeoSite> sites, string jsonPath, string rel, ILogger log, CancellationToken ct)
    {
        SeedArticle? seed;
        await using (var fs = File.OpenRead(jsonPath))
            seed = await JsonSerializer.DeserializeAsync<SeedArticle>(fs, ReadOptions, ct);
        if (seed is null)
        {
            log.LogWarning("SeoSeed: {File}: JSON vacío o nulo, se omite", rel);
            return Outcome.Skipped;
        }

        var siteKey = FirstNonEmpty(seed.SiteKey, Path.GetFileName(Path.GetDirectoryName(jsonPath)));
        var slug = FirstNonEmpty(seed.Slug, Path.GetFileNameWithoutExtension(jsonPath)).Trim().Trim('/');
        if (slug.Length == 0)
        {
            log.LogWarning("SeoSeed: {File}: sin slug, se omite", rel);
            return Outcome.Skipped;
        }

        var site = sites.FirstOrDefault(s => string.Equals(s.ProductKey, siteKey, StringComparison.OrdinalIgnoreCase));
        if (site is null)
        {
            log.LogWarning("SeoSeed: {File}: no hay SeoSite activo con ProductKey '{SiteKey}', se omite", rel, siteKey);
            return Outcome.Skipped;
        }

        var mdPath = Path.ChangeExtension(jsonPath, ".md");
        if (!File.Exists(mdPath))
        {
            log.LogWarning("SeoSeed: {File}: falta el cuerpo {Md}, se omite", rel, Path.GetFileName(mdPath));
            return Outcome.Skipped;
        }
        var body = (await File.ReadAllTextAsync(mdPath, ct)).Replace("\r\n", "\n").Trim();
        if (body.Length == 0)
        {
            log.LogWarning("SeoSeed: {File}: el cuerpo {Md} está vacío, se omite", rel, Path.GetFileName(mdPath));
            return Outcome.Skipped;
        }

        var title = Truncate(FirstNonEmpty(seed.Title, TitleFromBody(body), slug), 512);
        // La plantilla del blog no agrega el <h1> desde Title: el cuerpo debe traerlo.
        if (!body.StartsWith("# ", StringComparison.Ordinal))
            body = $"# {title}\n\n{body}";

        var contentType = Enum.TryParse<SeoContentType>(seed.ContentType?.Trim(), ignoreCase: true, out var parsedType)
            ? parsedType : SeoContentType.Article;
        var metaDescription = Truncate(seed.MetaDescription?.Trim() ?? string.Empty, 1024);
        var targetKeyword = Truncate(seed.TargetKeyword?.Trim() ?? string.Empty, 256);
        var faqJson = SerializeFaq(seed.Faq);
        var jsonLd = seed.JsonLdText();
        var wordCount = CountWords(body);
        var publishedUrl = Truncate(BlogPublisher.ArticleUrl(site, slug), 1024);
        var now = DateTimeOffset.UtcNow;

        var existing = await db.SeoArticles.FirstOrDefaultAsync(a => a.SiteId == site.Id && a.Slug == slug, ct);
        if (existing is null)
        {
            db.SeoArticles.Add(new SeoArticle
            {
                Id = Guid.NewGuid(),
                SiteId = site.Id,
                TargetKeyword = targetKeyword,
                Title = title,
                Slug = Truncate(slug, 512),
                MetaDescription = metaDescription,
                BodyMarkdown = body,
                FaqJson = faqJson,
                JsonLd = jsonLd,
                ContentType = contentType,
                Status = SeoArticleStatus.Published,
                WordCount = wordCount,
                GeneratedBy = GeneratedByMarker,
                PublishedUrl = publishedUrl,
                CreatedAt = now,
                UpdatedAt = now,
                PublishedAt = ParseUtc(seed.PublishedAt) ?? now,
            });
            await db.SaveChangesAsync(ct);
            log.LogInformation("SeoSeed: creado {SiteKey}/{Slug} → {Url}", site.ProductKey, slug, publishedUrl);
            return Outcome.Created;
        }

        if (!string.Equals(existing.GeneratedBy, GeneratedByMarker, StringComparison.Ordinal))
        {
            log.LogInformation("SeoSeed: {SiteKey}/{Slug} ya existe con GeneratedBy='{GeneratedBy}', no se toca",
                site.ProductKey, slug, existing.GeneratedBy);
            return Outcome.Skipped;
        }

        // Nuestro: pisamos el contenido, conservamos Id/CreatedAt/PublishedAt/Status.
        existing.TargetKeyword = targetKeyword;
        existing.Title = title;
        existing.MetaDescription = metaDescription;
        existing.BodyMarkdown = body;
        existing.FaqJson = faqJson;
        existing.JsonLd = jsonLd;
        existing.ContentType = contentType;
        existing.WordCount = wordCount;
        existing.PublishedUrl = publishedUrl;

        if (!db.ChangeTracker.HasChanges())
        {
            db.Entry(existing).State = EntityState.Detached;
            return Outcome.Unchanged;
        }

        existing.UpdatedAt = now;
        await db.SaveChangesAsync(ct);
        log.LogInformation("SeoSeed: actualizado {SiteKey}/{Slug}", site.ProductKey, slug);
        return Outcome.Updated;
    }

    // ------------------------------------------------------------ helpers

    /// <summary>
    /// Serializa con el mismo shape que <c>SeoContentService</c> (record Question/Answer, opciones
    /// default → PascalCase); <c>BlogPublisher.RenderFaqVisible</c> lo lee case-insensitive.
    /// </summary>
    private static string SerializeFaq(List<SeedFaq>? faq)
    {
        var items = (faq ?? new())
            .Where(f => !string.IsNullOrWhiteSpace(f.Question) && !string.IsNullOrWhiteSpace(f.Answer))
            .Select(f => new FaqItem(f.Question!.Trim(), f.Answer!.Trim()))
            .ToList();
        return items.Count == 0 ? "[]" : JsonSerializer.Serialize(items);
    }

    private static DateTimeOffset? ParseUtc(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        return DateTimeOffset.TryParse(s.Trim(), CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var d)
            ? d : null;
    }

    private static string? TitleFromBody(string body)
    {
        var first = body.Split('\n', 2)[0].Trim();
        return first.StartsWith("# ", StringComparison.Ordinal) ? first[2..].Trim() : null;
    }

    private static int CountWords(string text)
        => text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;

    private static string FirstNonEmpty(params string?[] values)
        => values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v))?.Trim() ?? string.Empty;

    private static string Truncate(string s, int max)
        => s.Length <= max ? s : s[..max];

    // ------------------------------------------------------------ shape del .json

    private sealed class SeedArticle
    {
        public string? SiteKey { get; set; }
        public string? Slug { get; set; }
        public string? Title { get; set; }
        public string? MetaDescription { get; set; }
        public string? TargetKeyword { get; set; }
        public string? ContentType { get; set; }
        public string? PublishedAt { get; set; }
        public List<SeedFaq>? Faq { get; set; }
        /// <summary>String con el JSON-LD, o directamente el objeto JSON (se guarda su texto crudo).</summary>
        public JsonElement? JsonLd { get; set; }

        public string JsonLdText() => JsonLd switch
        {
            null => string.Empty,
            { ValueKind: JsonValueKind.String } e => e.GetString()?.Trim() ?? string.Empty,
            { ValueKind: JsonValueKind.Null or JsonValueKind.Undefined } => string.Empty,
            { } e => e.GetRawText(),
        };
    }

    private sealed record SeedFaq(string? Question, string? Answer);

    /// <summary>Mismo shape que <c>SeoContentService.FaqItem</c> / <c>BlogPublisher.FaqItem</c>.</summary>
    private sealed record FaqItem(string Question, string Answer);
}
