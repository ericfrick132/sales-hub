using Microsoft.EntityFrameworkCore;
using SalesHub.Core.Domain.Enums;
using SalesHub.Infrastructure.Persistence;
using SalesHub.Infrastructure.Services;

namespace SalesHub.Workers;

/// <summary>
/// Agente SEO/GEO AUTÓNOMO. Por cada sitio con AutoPublish activo y una cadencia
/// configurada (PublishCadenceHours), cuando "le toca" genera Y publica un artículo
/// solo —sin revisión humana— directo al repo de la app (queda live en /blog). El
/// humano solo configura la cadencia (días/horas) y cuántos por disparo (PostsPerRun).
/// Si el sitio se queda sin keywords, investiga más automáticamente.
/// Se enciende con Seo:AutoStart=true (+ SALESHUB_RUN_WORKERS=true).
/// </summary>
public class SeoContentWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly IConfiguration _config;
    private readonly ILogger<SeoContentWorker> _log;

    public SeoContentWorker(IServiceScopeFactory scopes, IConfiguration config, ILogger<SeoContentWorker> log)
    {
        _scopes = scopes; _config = config; _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var enabled = _config.GetValue<bool>("Seo:AutoStart", false);
        if (!enabled)
        {
            _log.LogInformation("SeoContentWorker disabled (on-demand only). Set Seo:AutoStart=true to enable the autonomous agent.");
            return;
        }

        // Cada cuánto DESPIERTA a chequear si algún sitio cumplió su cadencia. La cadencia
        // real la define cada sitio en horas; este intervalo solo es la resolución del chequeo.
        var intervalMin = _config.GetValue<int?>("Seo:WorkerIntervalMinutes") ?? 60;
        _log.LogInformation("SeoContentWorker (autónomo) started, chequea cada {Min} min", intervalMin);
        await Task.Delay(TimeSpan.FromMinutes(2), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try { await RunTickAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { _log.LogError(ex, "SeoContentWorker tick failed"); }
            await Task.Delay(TimeSpan.FromMinutes(intervalMin), stoppingToken);
        }
    }

    private async Task RunTickAsync(CancellationToken ct)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var seo = scope.ServiceProvider.GetRequiredService<SeoContentService>();

        if (!seo.IsConfigured) { _log.LogWarning("SeoContentWorker: Claude sin configurar, salteo tick"); return; }

        // Modo C: el blog se sirve en vivo desde el backend de cada app (sin commits),
        // así que sólo hace falta dominio + ProductKey. No requiere repo ni GitHub.
        var sites = await db.SeoSites
            .Where(s => s.IsActive && s.AutoPublish && s.PublishCadenceHours > 0
                     && s.Domain != "" && s.ProductKey != null)
            .ToListAsync(ct);

        var now = DateTimeOffset.UtcNow;
        foreach (var site in sites)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var lastPub = await db.SeoArticles
                    .Where(a => a.SiteId == site.Id && a.PublishedAt != null)
                    .MaxAsync(a => (DateTimeOffset?)a.PublishedAt, ct);

                var due = lastPub is null || (now - lastPub.Value).TotalHours >= site.PublishCadenceHours;
                if (!due) continue;

                var perRun = Math.Max(1, site.PostsPerRun);
                for (var i = 0; i < perRun; i++)
                {
                    var kw = await NextKeywordAsync(db, seo, site.Id, site.Sector, ct);
                    if (kw is null) { _log.LogInformation("SeoContentWorker: {Site} sin keywords disponibles", site.Name); break; }

                    var type = kw.Intent == SeoIntent.Commercial ? SeoContentType.Comparison
                             : kw.Intent == SeoIntent.Transactional ? SeoContentType.Landing
                             : SeoContentType.Article;

                    // Genera y publica directo (sin revisión humana, sin commit): queda live
                    // vía el endpoint público + el backend de cada app.
                    var article = await seo.GenerateArticleAsync(site.Id, kw.Term, type, kw.Id, SeoArticleStatus.Published, ct);
                    if (article is null) { _log.LogWarning("SeoContentWorker: generación nula para {Site}", site.Name); break; }

                    article.PublishedAt ??= DateTimeOffset.UtcNow;
                    article.PublishedUrl = BlogPublisher.ArticleUrl(site, article.Slug);
                    await db.SaveChangesAsync(ct);
                    _log.LogInformation("SeoContentWorker: AUTO-publicado '{Title}' → {Url}", article.Title, article.PublishedUrl);

                    await Task.Delay(TimeSpan.FromSeconds(8), ct);
                }

                await Task.Delay(TimeSpan.FromSeconds(8), ct); // espaciar entre sitios
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "SeoContentWorker: falló ciclo de {Site}", site.Name);
            }
        }
    }

    /// <summary>Próxima keyword a usar (Planned primero, luego Idea, por prioridad). Si no hay, investiga más.</summary>
    private static async Task<Core.Domain.Entities.SeoKeyword?> NextKeywordAsync(
        ApplicationDbContext db, SeoContentService seo, Guid siteId, string seed, CancellationToken ct)
    {
        var kw = await PickAsync(db, siteId, ct);
        if (kw is not null) return kw;

        // Refill automático: investigar más keywords y reintentar.
        try { await seo.ResearchKeywordsAsync(siteId, seed ?? "", ct); }
        catch { /* si falla la investigación, devolvemos null abajo */ }
        return await PickAsync(db, siteId, ct);
    }

    private static Task<Core.Domain.Entities.SeoKeyword?> PickAsync(ApplicationDbContext db, Guid siteId, CancellationToken ct)
        => db.SeoKeywords
            .Where(k => k.SiteId == siteId && (k.Status == SeoKeywordStatus.Planned || k.Status == SeoKeywordStatus.Idea))
            .OrderBy(k => k.Status == SeoKeywordStatus.Planned ? 0 : 1)
            .ThenByDescending(k => k.Priority)
            .FirstOrDefaultAsync(ct);
}
