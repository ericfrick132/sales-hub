using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SalesHub.Core.Domain.Entities;
using SalesHub.Core.Domain.Enums;
using SalesHub.Infrastructure.Persistence;
using SalesHub.Infrastructure.Services;

namespace SalesHub.Api.Controllers;

/// <summary>
/// Motor de SEO/GEO casero. Todo es admin-only (operación de marketing).
/// Gestiona sitios (uno por app), investiga keywords y genera/optimiza contenido.
/// </summary>
[ApiController]
[Route("api/seo")]
[Authorize]
public class SeoController : ControllerBase
{
    private readonly ApplicationDbContext _db;
    private readonly SeoContentService _seo;
    private readonly BlogPublisher _publisher;

    public SeoController(ApplicationDbContext db, SeoContentService seo, BlogPublisher publisher)
    {
        _db = db;
        _seo = seo;
        _publisher = publisher;
    }

    // ---------------------------------------------------------------- SITES

    public record SiteSummary(
        Guid Id, string? ProductKey, string Name, string Domain, string Language,
        string Sector, string Audience, string ProductSummary, string BrandVoice,
        List<string> TargetCountries, string BlogBaseUrl, bool AutoPublish, int PublishCadenceHours, int PostsPerRun,
        string RepoFullName, string RepoBranch, string RepoPublicDir,
        bool IsActive, int KeywordCount, int ArticleCount, int PublishedCount);

    public record UpsertSite(
        string? ProductKey, string Name, string? Domain, string? Language, string? Sector,
        string? Audience, string? ProductSummary, string? BrandVoice, List<string>? TargetCountries,
        string? BlogBaseUrl, bool? AutoPublish, int? PublishCadenceHours, int? PostsPerRun,
        string? RepoFullName, string? RepoBranch, string? RepoPublicDir);

    [HttpGet("sites")]
    public async Task<IActionResult> ListSites(CancellationToken ct)
    {
        var sites = await _db.SeoSites.AsNoTracking().OrderBy(s => s.Name).ToListAsync(ct);
        var ids = sites.Select(s => s.Id).ToList();
        var kwCounts = await _db.SeoKeywords.Where(k => ids.Contains(k.SiteId))
            .GroupBy(k => k.SiteId).Select(g => new { g.Key, C = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.C, ct);
        var artCounts = await _db.SeoArticles.Where(a => ids.Contains(a.SiteId))
            .GroupBy(a => a.SiteId).Select(g => new { g.Key, C = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.C, ct);
        var pubCounts = await _db.SeoArticles.Where(a => ids.Contains(a.SiteId) && a.Status == SeoArticleStatus.Published)
            .GroupBy(a => a.SiteId).Select(g => new { g.Key, C = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.C, ct);

        var result = sites.Select(s => new SiteSummary(
            s.Id, s.ProductKey, s.Name, s.Domain, s.Language, s.Sector, s.Audience, s.ProductSummary,
            s.BrandVoice, s.TargetCountries, s.BlogBaseUrl, s.AutoPublish, s.PublishCadenceHours, s.PostsPerRun,
            s.RepoFullName, s.RepoBranch, s.RepoPublicDir, s.IsActive,
            kwCounts.GetValueOrDefault(s.Id), artCounts.GetValueOrDefault(s.Id), pubCounts.GetValueOrDefault(s.Id)));
        return Ok(result);
    }

    [HttpPost("sites")]
    public async Task<IActionResult> CreateSite([FromBody] UpsertSite req, CancellationToken ct)
    {
        if (!CurrentUser.IsAdmin(User)) return Forbid();
        if (string.IsNullOrWhiteSpace(req.Name)) return BadRequest(new { error = "Falta el nombre" });

        var site = new SeoSite
        {
            Id = Guid.NewGuid(),
            ProductKey = req.ProductKey?.Trim(),
            Name = req.Name.Trim(),
            Domain = req.Domain?.Trim() ?? "",
            Language = string.IsNullOrWhiteSpace(req.Language) ? "es" : req.Language!.Trim(),
            Sector = req.Sector?.Trim() ?? "",
            Audience = req.Audience?.Trim() ?? "",
            ProductSummary = req.ProductSummary?.Trim() ?? "",
            BrandVoice = req.BrandVoice?.Trim() ?? "",
            TargetCountries = req.TargetCountries ?? new(),
            BlogBaseUrl = req.BlogBaseUrl?.Trim() ?? "",
            AutoPublish = req.AutoPublish ?? false,
            PublishCadenceHours = req.PublishCadenceHours ?? 0,
            PostsPerRun = req.PostsPerRun ?? 1,
            RepoFullName = req.RepoFullName?.Trim() ?? "",
            RepoBranch = string.IsNullOrWhiteSpace(req.RepoBranch) ? "main" : req.RepoBranch!.Trim(),
            RepoPublicDir = string.IsNullOrWhiteSpace(req.RepoPublicDir) ? "src/frontend/public" : req.RepoPublicDir!.Trim()
        };
        _db.SeoSites.Add(site);
        await _db.SaveChangesAsync(ct);
        return Ok(new { site.Id });
    }

    [HttpPut("sites/{id:guid}")]
    public async Task<IActionResult> UpdateSite(Guid id, [FromBody] UpsertSite req, CancellationToken ct)
    {
        if (!CurrentUser.IsAdmin(User)) return Forbid();
        var site = await _db.SeoSites.FirstOrDefaultAsync(s => s.Id == id, ct);
        if (site is null) return NotFound();

        site.ProductKey = req.ProductKey?.Trim();
        if (!string.IsNullOrWhiteSpace(req.Name)) site.Name = req.Name.Trim();
        site.Domain = req.Domain?.Trim() ?? site.Domain;
        if (!string.IsNullOrWhiteSpace(req.Language)) site.Language = req.Language!.Trim();
        site.Sector = req.Sector?.Trim() ?? site.Sector;
        site.Audience = req.Audience?.Trim() ?? site.Audience;
        site.ProductSummary = req.ProductSummary?.Trim() ?? site.ProductSummary;
        site.BrandVoice = req.BrandVoice?.Trim() ?? site.BrandVoice;
        if (req.TargetCountries is not null) site.TargetCountries = req.TargetCountries;
        site.BlogBaseUrl = req.BlogBaseUrl?.Trim() ?? site.BlogBaseUrl;
        if (req.AutoPublish is not null) site.AutoPublish = req.AutoPublish.Value;
        if (req.PublishCadenceHours is not null) site.PublishCadenceHours = req.PublishCadenceHours.Value;
        if (req.PostsPerRun is not null) site.PostsPerRun = req.PostsPerRun.Value;
        if (req.RepoFullName is not null) site.RepoFullName = req.RepoFullName.Trim();
        if (!string.IsNullOrWhiteSpace(req.RepoBranch)) site.RepoBranch = req.RepoBranch.Trim();
        if (!string.IsNullOrWhiteSpace(req.RepoPublicDir)) site.RepoPublicDir = req.RepoPublicDir.Trim();
        site.UpdatedAt = DateTimeOffset.UtcNow;

        await _db.SaveChangesAsync(ct);
        return Ok(new { site.Id });
    }

    [HttpDelete("sites/{id:guid}")]
    public async Task<IActionResult> DeleteSite(Guid id, CancellationToken ct)
    {
        if (!CurrentUser.IsAdmin(User)) return Forbid();
        var site = await _db.SeoSites.FirstOrDefaultAsync(s => s.Id == id, ct);
        if (site is null) return NotFound();
        site.IsActive = false;
        site.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }

    /// <summary>Crea (idempotente) un sitio por cada una de las 6 apps con defaults razonables que se editan después.</summary>
    [HttpPost("sites/seed-apps")]
    public async Task<IActionResult> SeedApps(CancellationToken ct)
    {
        if (!CurrentUser.IsAdmin(User)) return Forbid();

        var defaults = new (string Key, string Name, string Sector, string Audience, string Summary, string Domain, string Repo, string Branch)[]
        {
            ("gymhero", "GymHero", "software de gestión para gimnasios y estudios fitness",
                "dueños de gimnasios, boxes de crossfit y entrenadores que gestionan socios, cobros y rutinas",
                "App para gestionar socios, cobros, accesos y rutinas de un gimnasio desde el celular.",
                "gymhero.fitness", "ericfrick132/gymhero", "main"),
            ("turnospro", "TurnosPro", "software de turnos y reservas online",
                "profesionales y comercios que manejan turnos: peluquerías, consultorios, estética, talleres",
                "App de agenda y reservas online con recordatorios para reducir ausencias.",
                "turnos-pro.com", "ericfrick132/beauty-salon", "master"),
            ("bunker", "Bunker", "",
                "", "",
                "bunker-app.com", "ericfrick132/conquerapp", "main"),
            ("unistock", "UniStock", "software de inventario y control de stock",
                "comercios y pymes que necesitan controlar stock, compras y reportes",
                "App de inventario multi-depósito con reportes y alertas de stock.",
                "", "ericfrick132/UniStock---AR", "main"),
            ("archicloud", "ArchiCloud", "software de gestión de obras y construcción",
                "estudios de arquitectura, constructoras y directores de obra",
                "App para gestionar obras: avances, costos, documentación y equipos por obra.",
                "archicloud.tech", "ericfrick132/ConstructionManager", "main"),
            ("playcrew", "PlayCrew", "app de pádel para encontrar partidos y reservar canchas",
                "jugadores de pádel que buscan partidos de su nivel y reservar canchas",
                "App de pádel para armar partidos por nivel, reservar canchas y conocer jugadores.",
                "playcrewpadel.com", "ericfrick132/PlayCrew", "main"),
        };

        var existing = await _db.SeoSites.Select(s => s.ProductKey).ToListAsync(ct);
        var existingSet = new HashSet<string>(existing.Where(x => x != null)!, StringComparer.OrdinalIgnoreCase);

        var created = new List<string>();
        foreach (var d in defaults)
        {
            if (existingSet.Contains(d.Key)) continue;
            _db.SeoSites.Add(new SeoSite
            {
                Id = Guid.NewGuid(),
                ProductKey = d.Key,
                Name = d.Name,
                Domain = d.Domain,
                Language = "es",
                Sector = d.Sector,
                Audience = d.Audience,
                ProductSummary = d.Summary,
                TargetCountries = new() { "Argentina", "Uruguay", "Colombia" },
                AutoPublish = false,          // arranca pausado; el usuario fija la cadencia y lo prende
                PublishCadenceHours = 0,
                PostsPerRun = 1,
                RepoFullName = d.Repo,
                RepoBranch = d.Branch,
                RepoPublicDir = "src/frontend/public"
            });
            created.Add(d.Name);
        }
        await _db.SaveChangesAsync(ct);
        return Ok(new { created, message = created.Count == 0 ? "Las 6 apps ya existían" : $"Creados: {string.Join(", ", created)}" });
    }

    // ---------------------------------------------------------------- KEYWORDS

    public record KeywordDto(
        Guid Id, Guid SiteId, string Term, string Cluster, SeoIntent Intent, string Source,
        int? Volume, int? Difficulty, int Priority, SeoKeywordStatus Status, DateTimeOffset CreatedAt);

    [HttpGet("sites/{siteId:guid}/keywords")]
    public async Task<IActionResult> ListKeywords(Guid siteId, [FromQuery] string? status, CancellationToken ct)
    {
        var q = _db.SeoKeywords.AsNoTracking().Where(k => k.SiteId == siteId);
        if (Enum.TryParse<SeoKeywordStatus>(status, true, out var st)) q = q.Where(k => k.Status == st);
        var kws = await q.OrderByDescending(k => k.Priority).ThenBy(k => k.Term).ToListAsync(ct);
        return Ok(kws.Select(Map));
    }

    public record ResearchRequest(string? SeedTopic);

    [HttpPost("sites/{siteId:guid}/keywords/research")]
    public async Task<IActionResult> Research(Guid siteId, [FromBody] ResearchRequest req, CancellationToken ct)
    {
        if (!CurrentUser.IsAdmin(User)) return Forbid();
        try
        {
            var created = await _seo.ResearchKeywordsAsync(siteId, req.SeedTopic ?? "", ct);
            return Ok(new { count = created.Count, keywords = created.Select(Map) });
        }
        catch (InvalidOperationException ex) { return BadRequest(new { error = ex.Message }); }
    }

    public record AddKeywordRequest(string Term, string? Cluster, SeoIntent? Intent, int? Priority);

    [HttpPost("sites/{siteId:guid}/keywords")]
    public async Task<IActionResult> AddKeyword(Guid siteId, [FromBody] AddKeywordRequest req, CancellationToken ct)
    {
        if (!CurrentUser.IsAdmin(User)) return Forbid();
        if (string.IsNullOrWhiteSpace(req.Term)) return BadRequest(new { error = "Falta el término" });
        var exists = await _db.SeoKeywords.AnyAsync(k => k.SiteId == siteId && k.Term.ToLower() == req.Term.ToLower(), ct);
        if (exists) return BadRequest(new { error = "Esa keyword ya existe" });

        var kw = new SeoKeyword
        {
            Id = Guid.NewGuid(),
            SiteId = siteId,
            Term = req.Term.Trim(),
            Cluster = req.Cluster?.Trim() ?? "",
            Intent = req.Intent ?? SeoIntent.Informational,
            Source = "manual",
            Priority = Math.Clamp(req.Priority ?? 50, 0, 100),
            Status = SeoKeywordStatus.Idea
        };
        _db.SeoKeywords.Add(kw);
        await _db.SaveChangesAsync(ct);
        return Ok(Map(kw));
    }

    public record PatchKeywordRequest(SeoKeywordStatus? Status, int? Priority);

    [HttpPatch("keywords/{id:guid}")]
    public async Task<IActionResult> PatchKeyword(Guid id, [FromBody] PatchKeywordRequest req, CancellationToken ct)
    {
        if (!CurrentUser.IsAdmin(User)) return Forbid();
        var kw = await _db.SeoKeywords.FirstOrDefaultAsync(k => k.Id == id, ct);
        if (kw is null) return NotFound();
        if (req.Status is not null) kw.Status = req.Status.Value;
        if (req.Priority is not null) kw.Priority = Math.Clamp(req.Priority.Value, 0, 100);
        await _db.SaveChangesAsync(ct);
        return Ok(Map(kw));
    }

    [HttpDelete("keywords/{id:guid}")]
    public async Task<IActionResult> DeleteKeyword(Guid id, CancellationToken ct)
    {
        if (!CurrentUser.IsAdmin(User)) return Forbid();
        var kw = await _db.SeoKeywords.FirstOrDefaultAsync(k => k.Id == id, ct);
        if (kw is null) return NotFound();
        _db.SeoKeywords.Remove(kw);
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }

    // ---------------------------------------------------------------- ARTICLES

    public record ArticleSummary(
        Guid Id, Guid SiteId, string TargetKeyword, string Title, string Slug,
        SeoContentType ContentType, SeoArticleStatus Status, int? SeoScore, int WordCount,
        string GeneratedBy, string PublishedUrl, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, DateTimeOffset? PublishedAt);

    public record ArticleFull(
        Guid Id, Guid SiteId, Guid? KeywordId, string TargetKeyword, string Title, string Slug,
        string MetaDescription, string BodyMarkdown, string FaqJson, string JsonLd,
        SeoContentType ContentType, SeoArticleStatus Status, int? SeoScore, string OptimizationNotes,
        int WordCount, string GeneratedBy, string PublishedUrl, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, DateTimeOffset? PublishedAt);

    [HttpGet("sites/{siteId:guid}/articles")]
    public async Task<IActionResult> ListArticles(Guid siteId, [FromQuery] string? status, CancellationToken ct)
    {
        var q = _db.SeoArticles.AsNoTracking().Where(a => a.SiteId == siteId);
        if (Enum.TryParse<SeoArticleStatus>(status, true, out var st)) q = q.Where(a => a.Status == st);
        var arts = await q.OrderByDescending(a => a.UpdatedAt).ToListAsync(ct);
        return Ok(arts.Select(a => new ArticleSummary(
            a.Id, a.SiteId, a.TargetKeyword, a.Title, a.Slug, a.ContentType, a.Status, a.SeoScore,
            a.WordCount, a.GeneratedBy, a.PublishedUrl, a.CreatedAt, a.UpdatedAt, a.PublishedAt)));
    }

    [HttpGet("articles/{id:guid}")]
    public async Task<IActionResult> GetArticle(Guid id, CancellationToken ct)
    {
        var a = await _db.SeoArticles.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (a is null) return NotFound();
        return Ok(MapFull(a));
    }

    public record GenerateRequest(string Keyword, SeoContentType? ContentType, Guid? KeywordId);

    [HttpPost("sites/{siteId:guid}/articles/generate")]
    public async Task<IActionResult> Generate(Guid siteId, [FromBody] GenerateRequest req, CancellationToken ct)
    {
        if (!CurrentUser.IsAdmin(User)) return Forbid();
        try
        {
            var article = await _seo.GenerateArticleAsync(
                siteId, req.Keyword, req.ContentType ?? SeoContentType.Article, req.KeywordId,
                SeoArticleStatus.NeedsReview, ct);
            if (article is null) return StatusCode(502, new { error = "El modelo no devolvió un artículo válido. Reintentá." });
            return Ok(MapFull(article));
        }
        catch (InvalidOperationException ex) { return BadRequest(new { error = ex.Message }); }
    }

    [HttpPost("articles/{id:guid}/optimize")]
    public async Task<IActionResult> Optimize(Guid id, CancellationToken ct)
    {
        if (!CurrentUser.IsAdmin(User)) return Forbid();
        try
        {
            var a = await _seo.OptimizeAsync(id, ct);
            return a is null ? NotFound() : Ok(MapFull(a));
        }
        catch (InvalidOperationException ex) { return BadRequest(new { error = ex.Message }); }
    }

    public record PatchArticleRequest(
        string? Title, string? Slug, string? MetaDescription, string? BodyMarkdown, SeoArticleStatus? Status);

    [HttpPatch("articles/{id:guid}")]
    public async Task<IActionResult> PatchArticle(Guid id, [FromBody] PatchArticleRequest req, CancellationToken ct)
    {
        if (!CurrentUser.IsAdmin(User)) return Forbid();
        var a = await _db.SeoArticles.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (a is null) return NotFound();

        if (req.Title is not null) a.Title = req.Title.Trim();
        if (req.Slug is not null) a.Slug = req.Slug.Trim();
        if (req.MetaDescription is not null) a.MetaDescription = req.MetaDescription.Trim();
        if (req.BodyMarkdown is not null)
        {
            a.BodyMarkdown = req.BodyMarkdown;
            a.WordCount = req.BodyMarkdown.Split(new[] { ' ', '\n', '\r', '\t' }, StringSplitOptions.RemoveEmptyEntries).Length;
        }
        if (req.Status is not null)
        {
            a.Status = req.Status.Value;
            if (req.Status.Value == SeoArticleStatus.Published && a.PublishedAt is null)
                a.PublishedAt = DateTimeOffset.UtcNow;
        }
        a.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);
        return Ok(MapFull(a));
    }

    /// <summary>Publica el artículo al repo del sitio (commit HTML → DO redeploya → live en /blog).</summary>
    [HttpPost("articles/{id:guid}/publish")]
    public async Task<IActionResult> Publish(Guid id, CancellationToken ct)
    {
        if (!CurrentUser.IsAdmin(User)) return Forbid();
        try
        {
            var a = await _publisher.PublishArticleAsync(id, ct);
            return Ok(MapFull(a));
        }
        catch (InvalidOperationException ex) { return BadRequest(new { error = ex.Message }); }
    }

    [HttpDelete("articles/{id:guid}")]
    public async Task<IActionResult> DeleteArticle(Guid id, CancellationToken ct)
    {
        if (!CurrentUser.IsAdmin(User)) return Forbid();
        var a = await _db.SeoArticles.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (a is null) return NotFound();
        _db.SeoArticles.Remove(a);
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }

    // ---------------------------------------------------------------- MAPPERS

    private static KeywordDto Map(SeoKeyword k) => new(
        k.Id, k.SiteId, k.Term, k.Cluster, k.Intent, k.Source, k.Volume, k.Difficulty, k.Priority, k.Status, k.CreatedAt);

    private static ArticleFull MapFull(SeoArticle a) => new(
        a.Id, a.SiteId, a.KeywordId, a.TargetKeyword, a.Title, a.Slug, a.MetaDescription, a.BodyMarkdown,
        a.FaqJson, a.JsonLd, a.ContentType, a.Status, a.SeoScore, a.OptimizationNotes, a.WordCount,
        a.GeneratedBy, a.PublishedUrl, a.CreatedAt, a.UpdatedAt, a.PublishedAt);
}
