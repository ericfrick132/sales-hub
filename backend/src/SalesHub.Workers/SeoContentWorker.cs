using Microsoft.EntityFrameworkCore;
using SalesHub.Core.Domain.Enums;
using SalesHub.Infrastructure.Persistence;
using SalesHub.Infrastructure.Services;

namespace SalesHub.Workers;

/// <summary>
/// El "agente 24/7" del motor de SEO/GEO. Por cada sitio con AutoPublish activo,
/// chequea el backlog de la semana y genera borradores hasta llegar al objetivo
/// semanal. NUNCA publica: todo queda en NeedsReview para que un humano lo apruebe.
/// Se enciende con Seo:AutoStart=true (además de SALESHUB_RUN_WORKERS=true).
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
            _log.LogInformation("SeoContentWorker disabled (on-demand only). Set Seo:AutoStart=true to enable the 24/7 agent.");
            return;
        }

        var intervalMin = _config.GetValue<int?>("Seo:WorkerIntervalMinutes") ?? 360;
        _log.LogInformation("SeoContentWorker started (interval {Min} min)", intervalMin);
        await Task.Delay(TimeSpan.FromMinutes(2), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunTickAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                _log.LogError(ex, "SeoContentWorker tick failed");
            }
            await Task.Delay(TimeSpan.FromMinutes(intervalMin), stoppingToken);
        }
    }

    private async Task RunTickAsync(CancellationToken ct)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var seo = scope.ServiceProvider.GetRequiredService<SeoContentService>();

        if (!seo.IsConfigured)
        {
            _log.LogWarning("SeoContentWorker: Claude sin configurar, salteando tick");
            return;
        }

        var sites = await db.SeoSites
            .Where(s => s.IsActive && s.AutoPublish && s.WeeklyTarget > 0)
            .ToListAsync(ct);

        var weekAgo = DateTimeOffset.UtcNow.AddDays(-7);

        foreach (var site in sites)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var madeThisWeek = await db.SeoArticles
                    .CountAsync(a => a.SiteId == site.Id && a.CreatedAt >= weekAgo, ct);
                if (madeThisWeek >= site.WeeklyTarget) continue;

                // Mejor keyword disponible: primero las planificadas, luego ideas, por prioridad.
                var kw = await db.SeoKeywords
                    .Where(k => k.SiteId == site.Id && (k.Status == SeoKeywordStatus.Planned || k.Status == SeoKeywordStatus.Idea))
                    .OrderBy(k => k.Status == SeoKeywordStatus.Planned ? 0 : 1)
                    .ThenByDescending(k => k.Priority)
                    .FirstOrDefaultAsync(ct);

                if (kw is null)
                {
                    _log.LogInformation("SeoContentWorker: {Site} sin keywords disponibles para generar", site.Name);
                    continue;
                }

                var type = kw.Intent == SeoIntent.Commercial ? SeoContentType.Comparison
                         : kw.Intent == SeoIntent.Transactional ? SeoContentType.Landing
                         : SeoContentType.Article;

                var article = await seo.GenerateArticleAsync(site.Id, kw.Term, type, kw.Id, SeoArticleStatus.NeedsReview, ct);
                if (article is not null)
                    _log.LogInformation("SeoContentWorker: generado '{Title}' para {Site} ({Done}/{Target} esta semana)",
                        article.Title, site.Name, madeThisWeek + 1, site.WeeklyTarget);

                // Espaciar llamadas entre sitios para no golpear la API de golpe.
                await Task.Delay(TimeSpan.FromSeconds(10), ct);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "SeoContentWorker: falló generar para {Site}", site.Name);
            }
        }
    }
}
