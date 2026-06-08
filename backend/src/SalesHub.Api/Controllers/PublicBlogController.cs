using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SalesHub.Core.Domain.Enums;
using SalesHub.Infrastructure.Persistence;
using SalesHub.Infrastructure.Services;

namespace SalesHub.Api.Controllers;

/// <summary>
/// Fuente común del blog: sirve el contenido ya renderizado (HTML + sitemap) por
/// sitio, sin auth. Lo consume el backend de cada app, que lo expone en su propio
/// dominio bajo /blog (así el contenido queda en gymhero.fitness/blog/... pero se
/// genera y administra de forma central acá). siteKey = ProductKey del sitio.
/// </summary>
[ApiController]
[AllowAnonymous]
[Route("blog-feed")]
public class PublicBlogController : ControllerBase
{
    private readonly ApplicationDbContext _db;
    public PublicBlogController(ApplicationDbContext db) { _db = db; }

    private async Task<Core.Domain.Entities.SeoSite?> SiteAsync(string siteKey, CancellationToken ct)
        => await _db.SeoSites.AsNoTracking()
            .FirstOrDefaultAsync(s => s.IsActive && s.ProductKey != null && s.ProductKey.ToLower() == siteKey.ToLower(), ct);

    private async Task<List<Core.Domain.Entities.SeoArticle>> PublishedAsync(Guid siteId, CancellationToken ct)
        => await _db.SeoArticles.AsNoTracking()
            .Where(a => a.SiteId == siteId && a.Status == SeoArticleStatus.Published)
            .OrderByDescending(a => a.PublishedAt)
            .ToListAsync(ct);

    [HttpGet("{siteKey}")]
    public async Task<IActionResult> Index(string siteKey, CancellationToken ct)
    {
        var site = await SiteAsync(siteKey, ct);
        if (site is null) return NotFound();
        var arts = await PublishedAsync(site.Id, ct);
        return Html(BlogPublisher.RenderIndexHtml(site, arts));
    }

    [HttpGet("{siteKey}/sitemap.xml")]
    public async Task<IActionResult> Sitemap(string siteKey, CancellationToken ct)
    {
        var site = await SiteAsync(siteKey, ct);
        if (site is null) return NotFound();
        var arts = await PublishedAsync(site.Id, ct);
        return Content(BlogPublisher.RenderSitemap(site, arts), "application/xml; charset=utf-8");
    }

    [HttpGet("{siteKey}/{slug}")]
    public async Task<IActionResult> Article(string siteKey, string slug, CancellationToken ct)
    {
        var site = await SiteAsync(siteKey, ct);
        if (site is null) return NotFound();
        var a = await _db.SeoArticles.AsNoTracking()
            .FirstOrDefaultAsync(x => x.SiteId == site.Id && x.Slug == slug && x.Status == SeoArticleStatus.Published, ct);
        if (a is null) return NotFound();
        var url = BlogPublisher.ArticleUrl(site, a.Slug);
        return Html(BlogPublisher.RenderArticleHtml(site, a, url));
    }

    private ContentResult Html(string html)
    {
        Response.Headers["Cache-Control"] = "public, max-age=300";
        return Content(html, "text/html; charset=utf-8");
    }
}
