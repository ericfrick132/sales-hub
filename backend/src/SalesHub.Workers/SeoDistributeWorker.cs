using Microsoft.EntityFrameworkCore;
using SalesHub.Core.Domain.Enums;
using SalesHub.Infrastructure.Persistence;
using SalesHub.Infrastructure.Services;

namespace SalesHub.Workers;

/// <summary>
/// Vía secundaria de distribución: publica al repo de la app cualquier artículo que
/// haya quedado en estado APROBADO (por si se aprueba algo a mano). El modo autónomo
/// (SeoContentWorker) ya genera+publica solo; esto cubre el caso manual sin borrar
/// la opción de revisar. Se enciende con Seo:AutoDistribute=true (+ SALESHUB_RUN_WORKERS=true).
/// </summary>
public class SeoDistributeWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly IConfiguration _config;
    private readonly ILogger<SeoDistributeWorker> _log;

    public SeoDistributeWorker(IServiceScopeFactory scopes, IConfiguration config, ILogger<SeoDistributeWorker> log)
    {
        _scopes = scopes; _config = config; _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var enabled = _config.GetValue<bool>("Seo:AutoDistribute", false);
        if (!enabled)
        {
            _log.LogInformation("SeoDistributeWorker disabled. Set Seo:AutoDistribute=true to auto-push approved articles.");
            return;
        }
        var intervalMin = _config.GetValue<int?>("Seo:DistributeIntervalMinutes") ?? 30;
        _log.LogInformation("SeoDistributeWorker started (interval {Min} min)", intervalMin);
        await Task.Delay(TimeSpan.FromMinutes(3), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try { await RunTickAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { _log.LogError(ex, "SeoDistributeWorker tick failed"); }
            await Task.Delay(TimeSpan.FromMinutes(intervalMin), stoppingToken);
        }
    }

    private async Task RunTickAsync(CancellationToken ct)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var publisher = scope.ServiceProvider.GetRequiredService<BlogPublisher>();
        if (!publisher.IsConfigured) { _log.LogWarning("SeoDistributeWorker: GitHub sin configurar"); return; }

        var siteIds = await db.SeoSites
            .Where(s => s.IsActive && s.RepoFullName != "" && s.Domain != "")
            .Select(s => s.Id).ToListAsync(ct);
        if (siteIds.Count == 0) return;

        var pending = await db.SeoArticles
            .Where(a => siteIds.Contains(a.SiteId) && a.Status == SeoArticleStatus.Approved)
            .OrderBy(a => a.UpdatedAt).Select(a => a.Id).Take(10).ToListAsync(ct);

        foreach (var id in pending)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var a = await publisher.PublishArticleAsync(id, ct);
                _log.LogInformation("SeoDistributeWorker: publicado '{Title}' → {Url}", a.Title, a.PublishedUrl);
                await Task.Delay(TimeSpan.FromSeconds(5), ct);
            }
            catch (Exception ex) { _log.LogWarning(ex, "SeoDistributeWorker: falló publicar {Id}", id); }
        }
    }
}
