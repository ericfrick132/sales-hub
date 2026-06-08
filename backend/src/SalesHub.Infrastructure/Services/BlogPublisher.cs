using System.Net;
using System.Text;
using System.Text.Json;
using Markdig;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SalesHub.Core.Domain.Entities;
using SalesHub.Core.Domain.Enums;
using SalesHub.Infrastructure.Persistence;

namespace SalesHub.Infrastructure.Services;

/// <summary>
/// Distribuidor del blog: toma un artículo aprobado, lo renderiza a HTML estático
/// (con meta SEO + JSON-LD + FAQ visible para GEO) y lo commitea al repo de la app
/// bajo {publicDir}/blog/{slug}/index.html. Regenera además el índice del blog y el
/// sitemap. DO App Platform redeploya solo al recibir el push → queda live en
/// https://{dominio}/blog/{slug}/. Nada se publica sin pasar por aprobación humana.
/// </summary>
public class BlogPublisher
{
    private readonly ApplicationDbContext _db;
    private readonly GitHubContentClient _gh;
    private readonly ILogger<BlogPublisher> _log;
    private static readonly MarkdownPipeline _md =
        new MarkdownPipelineBuilder().UseAdvancedExtensions().Build();

    public BlogPublisher(ApplicationDbContext db, GitHubContentClient gh, ILogger<BlogPublisher> log)
    {
        _db = db; _gh = gh; _log = log;
    }

    public bool IsConfigured => _gh.IsConfigured;

    /// <summary>
    /// Publica un artículo al repo de su sitio. Marca el artículo como Published con
    /// su URL. Lanza InvalidOperationException con mensaje claro si falta config.
    /// </summary>
    public async Task<SeoArticle> PublishArticleAsync(Guid articleId, CancellationToken ct = default)
    {
        var article = await _db.SeoArticles.Include(a => a.Site).FirstOrDefaultAsync(a => a.Id == articleId, ct)
            ?? throw new InvalidOperationException("Artículo no encontrado");
        var site = article.Site ?? throw new InvalidOperationException("El artículo no tiene sitio");

        if (!_gh.IsConfigured) throw new InvalidOperationException("GitHub no está configurado (falta GitHub__Token)");
        if (string.IsNullOrWhiteSpace(site.RepoFullName)) throw new InvalidOperationException($"{site.Name}: falta configurar el repo (RepoFullName)");
        if (string.IsNullOrWhiteSpace(site.Domain)) throw new InvalidOperationException($"{site.Name}: falta el dominio");
        if (string.IsNullOrWhiteSpace(article.Slug)) throw new InvalidOperationException("El artículo no tiene slug");

        var branch = string.IsNullOrWhiteSpace(site.RepoBranch) ? "main" : site.RepoBranch.Trim();
        var publicDir = (string.IsNullOrWhiteSpace(site.RepoPublicDir) ? "src/frontend/public" : site.RepoPublicDir).Trim().Trim('/');
        var url = ArticleUrl(site, article.Slug);

        // 1) Página del artículo
        var pagePath = $"{publicDir}/blog/{article.Slug}/index.html";
        await _gh.PutFileAsync(site.RepoFullName, branch, pagePath, RenderArticleHtml(site, article, url),
            $"blog: publicar \"{article.Title}\"", ct);

        // Marcar publicado ANTES de regenerar índice/sitemap, para que aparezca incluido.
        article.Status = SeoArticleStatus.Published;
        article.PublishedUrl = url;
        article.PublishedAt ??= DateTimeOffset.UtcNow;
        article.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);

        // 2) Índice del blog + 3) sitemap (con todos los publicados del sitio)
        var published = await _db.SeoArticles.AsNoTracking()
            .Where(a => a.SiteId == site.Id && a.Status == SeoArticleStatus.Published)
            .OrderByDescending(a => a.PublishedAt)
            .ToListAsync(ct);

        await _gh.PutFileAsync(site.RepoFullName, branch, $"{publicDir}/blog/index.html",
            RenderIndexHtml(site, published), "blog: actualizar índice", ct);
        await _gh.PutFileAsync(site.RepoFullName, branch, $"{publicDir}/blog/sitemap.xml",
            RenderSitemap(site, published), "blog: actualizar sitemap", ct);

        _log.LogInformation("BlogPublisher: publicado '{Title}' en {Url}", article.Title, url);
        return article;
    }

    // ------------------------------------------------------------ URLs

    private static string SiteBase(SeoSite s)
    {
        var d = s.Domain.Replace("https://", "").Replace("http://", "").TrimEnd('/');
        return $"https://{d}";
    }

    public static string ArticleUrl(SeoSite s, string slug) => $"{SiteBase(s)}/blog/{slug}/";

    // ------------------------------------------------------------ RENDER

    public static string RenderArticleHtml(SeoSite site, SeoArticle a, string url)
    {
        var bodyHtml = Markdown.ToHtml(a.BodyMarkdown ?? "", _md);
        var faqHtml = RenderFaqVisible(a.FaqJson);
        var jsonLd = FixJsonLdUrl(a.JsonLd, url);
        var siteUrl = SiteBase(site);
        var desc = E(a.MetaDescription);
        var title = E(a.Title);
        var date = (a.PublishedAt ?? a.CreatedAt).ToString("yyyy-MM-dd");

        var sb = new StringBuilder();
        sb.AppendLine("<!doctype html>");
        sb.AppendLine($"<html lang=\"{E(site.Language)}\">");
        sb.AppendLine("<head>");
        sb.AppendLine("<meta charset=\"utf-8\">");
        sb.AppendLine("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">");
        sb.AppendLine($"<title>{title}</title>");
        sb.AppendLine($"<meta name=\"description\" content=\"{desc}\">");
        sb.AppendLine($"<link rel=\"canonical\" href=\"{E(url)}\">");
        sb.AppendLine("<meta property=\"og:type\" content=\"article\">");
        sb.AppendLine($"<meta property=\"og:title\" content=\"{title}\">");
        sb.AppendLine($"<meta property=\"og:description\" content=\"{desc}\">");
        sb.AppendLine($"<meta property=\"og:url\" content=\"{E(url)}\">");
        sb.AppendLine($"<meta property=\"og:site_name\" content=\"{E(site.Name)}\">");
        sb.AppendLine("<meta name=\"twitter:card\" content=\"summary_large_image\">");
        if (!string.IsNullOrWhiteSpace(jsonLd))
            sb.AppendLine($"<script type=\"application/ld+json\">{jsonLd}</script>");
        sb.AppendLine($"<style>{Css}</style>");
        sb.AppendLine("</head><body>");
        sb.AppendLine($"<header class=\"top\"><a href=\"{E(siteUrl)}\">{E(site.Name)}</a> <a class=\"bloglink\" href=\"/blog/\">Blog</a></header>");
        sb.AppendLine("<main class=\"article\">");
        sb.AppendLine($"<p class=\"meta\"><time datetime=\"{date}\">{date}</time></p>");
        sb.AppendLine(bodyHtml);
        if (!string.IsNullOrWhiteSpace(faqHtml)) sb.AppendLine(faqHtml);
        sb.AppendLine($"<p class=\"cta\"><a href=\"{E(siteUrl)}\">Conocé {E(site.Name)} →</a></p>");
        sb.AppendLine("</main>");
        sb.AppendLine($"<footer class=\"foot\">© {E(site.Name)} · <a href=\"{E(siteUrl)}\">{E(site.Domain)}</a></footer>");
        sb.AppendLine("</body></html>");
        return sb.ToString();
    }

    private static string RenderFaqVisible(string faqJson)
    {
        List<FaqItem> items;
        try
        {
            items = JsonSerializer.Deserialize<List<FaqItem>>(faqJson ?? "[]",
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new();
        }
        catch { return ""; }
        if (items.Count == 0) return "";

        var sb = new StringBuilder();
        sb.AppendLine("<section class=\"faq\"><h2>Preguntas frecuentes</h2>");
        foreach (var f in items)
        {
            if (string.IsNullOrWhiteSpace(f.Question) || string.IsNullOrWhiteSpace(f.Answer)) continue;
            sb.AppendLine($"<h3>{E(f.Question)}</h3><p>{E(f.Answer)}</p>");
        }
        sb.AppendLine("</section>");
        return sb.ToString();
    }

    public static string RenderIndexHtml(SeoSite site, List<SeoArticle> arts)
    {
        var siteUrl = SiteBase(site);
        var sb = new StringBuilder();
        sb.AppendLine("<!doctype html>");
        sb.AppendLine($"<html lang=\"{E(site.Language)}\"><head>");
        sb.AppendLine("<meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">");
        sb.AppendLine($"<title>Blog · {E(site.Name)}</title>");
        sb.AppendLine($"<meta name=\"description\" content=\"Guías y artículos sobre {E(site.Sector)} — {E(site.Name)}.\">");
        sb.AppendLine($"<link rel=\"canonical\" href=\"{E(siteUrl)}/blog/\">");
        sb.AppendLine($"<style>{Css}</style></head><body>");
        sb.AppendLine($"<header class=\"top\"><a href=\"{E(siteUrl)}\">{E(site.Name)}</a></header>");
        sb.AppendLine("<main class=\"article\"><h1>Blog</h1><ul class=\"postlist\">");
        foreach (var a in arts)
        {
            sb.AppendLine($"<li><a href=\"/blog/{E(a.Slug)}/\">{E(a.Title)}</a><span class=\"meta\"> — {(a.PublishedAt ?? a.CreatedAt):yyyy-MM-dd}</span><p>{E(a.MetaDescription)}</p></li>");
        }
        sb.AppendLine("</ul></main>");
        sb.AppendLine($"<footer class=\"foot\">© {E(site.Name)} · <a href=\"{E(siteUrl)}\">{E(site.Domain)}</a></footer>");
        sb.AppendLine("</body></html>");
        return sb.ToString();
    }

    public static string RenderSitemap(SeoSite site, List<SeoArticle> arts)
    {
        var sb = new StringBuilder();
        sb.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
        sb.AppendLine("<urlset xmlns=\"http://www.sitemaps.org/schemas/sitemap/0.9\">");
        sb.AppendLine($"  <url><loc>{E(SiteBase(site))}/blog/</loc></url>");
        foreach (var a in arts)
        {
            var lm = (a.UpdatedAt).ToString("yyyy-MM-dd");
            sb.AppendLine($"  <url><loc>{E(ArticleUrl(site, a.Slug))}</loc><lastmod>{lm}</lastmod></url>");
        }
        sb.AppendLine("</urlset>");
        return sb.ToString();
    }

    // ------------------------------------------------------------ helpers

    private record FaqItem(string Question, string Answer);

    /// <summary>El JSON-LD viejo puede tener url relativa; lo corregimos a la URL absoluta.</summary>
    private static string FixJsonLdUrl(string jsonLd, string url)
    {
        if (string.IsNullOrWhiteSpace(jsonLd)) return "";
        try
        {
            using var doc = JsonDocument.Parse(jsonLd);
            // Reescribimos sólo si el "url" del Article no es absoluto.
            if (!jsonLd.Contains("\"url\"") || jsonLd.Contains($"\"{url}\"")) return jsonLd;
            // Reemplazo simple del primer url relativo por el absoluto.
            return new System.Text.RegularExpressions.Regex("\"url\"\\s*:\\s*\"[^\"]*\"")
                .Replace(jsonLd, $"\"url\": \"{url}\"", 1);
        }
        catch { return jsonLd; }
    }

    private static string E(string? s) => WebUtility.HtmlEncode(s ?? "");

    private const string Css =
        "*{box-sizing:border-box}body{margin:0;font-family:-apple-system,BlinkMacSystemFont,'Segoe UI',Roboto,Helvetica,Arial,sans-serif;" +
        "color:#1e293b;line-height:1.7;background:#fff}.top{display:flex;gap:1rem;align-items:center;padding:1rem 1.25rem;border-bottom:1px solid #e2e8f0;font-weight:600}" +
        ".top a{color:#0f172a;text-decoration:none}.top .bloglink{color:#2563eb;font-weight:500}" +
        ".article{max-width:760px;margin:0 auto;padding:1.5rem 1.25rem 3rem}.article h1{font-size:2rem;line-height:1.25;margin:.5rem 0 1rem}" +
        ".article h2{font-size:1.4rem;margin:2rem 0 .75rem}.article h3{font-size:1.12rem;margin:1.5rem 0 .5rem}" +
        ".article p{margin:.75rem 0}.article ul,.article ol{margin:.75rem 0;padding-left:1.4rem}.article li{margin:.3rem 0}" +
        ".article table{border-collapse:collapse;width:100%;margin:1rem 0;font-size:.95rem}.article th,.article td{border:1px solid #e2e8f0;padding:.5rem .7rem;text-align:left}" +
        ".article th{background:#f8fafc}.meta{color:#64748b;font-size:.85rem}.cta{margin-top:2rem;font-weight:600}.cta a{color:#2563eb;text-decoration:none}" +
        ".faq{margin-top:2.5rem;border-top:1px solid #e2e8f0;padding-top:1rem}.postlist{list-style:none;padding:0}.postlist li{margin:1.25rem 0;border-bottom:1px solid #f1f5f9;padding-bottom:1rem}" +
        ".postlist a{font-size:1.1rem;font-weight:600;color:#0f172a;text-decoration:none}.foot{max-width:760px;margin:0 auto;padding:1.5rem 1.25rem;color:#64748b;font-size:.85rem;border-top:1px solid #e2e8f0}";
}
