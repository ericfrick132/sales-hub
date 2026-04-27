using Microsoft.EntityFrameworkCore;
using SalesHub.Infrastructure.Apify;
using SalesHub.Infrastructure.Persistence;

namespace SalesHub.Workers;

public class CompetitorIngestWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly IConfiguration _config;
    private readonly ILogger<CompetitorIngestWorker> _log;

    public CompetitorIngestWorker(IServiceScopeFactory scopes, IConfiguration config, ILogger<CompetitorIngestWorker> log)
    {
        _scopes = scopes; _config = config; _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var enabled = _config.GetValue<bool>("Workers:CompetitorsAutoStart", false);
        if (!enabled)
        {
            _log.LogInformation("CompetitorIngestWorker disabled (on-demand only). Set Workers:CompetitorsAutoStart=true to enable cron.");
            return;
        }
        _log.LogInformation("CompetitorIngestWorker started");
        await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                var ig = scope.ServiceProvider.GetRequiredService<InstagramCompetitorScraper>();
                var stale = DateTimeOffset.UtcNow.AddHours(-12);
                var competitors = await db.Competitors
                    .Where(c => c.IsActive && c.Platform == "instagram"
                             && !c.Handle.StartsWith("__tiktok_trends_")
                             && (c.LastScrapedAt == null || c.LastScrapedAt < stale))
                    .Take(5)
                    .ToListAsync(stoppingToken);

                foreach (var c in competitors)
                {
                    try
                    {
                        await ig.ScrapeAsync(c.Id, 30, stoppingToken);
                        await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
                    }
                    catch (Exception ex)
                    {
                        _log.LogWarning(ex, "Competitor scrape failed for @{Handle}", c.Handle);
                    }
                }
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Competitor ingest tick failed");
            }
            await Task.Delay(TimeSpan.FromMinutes(60), stoppingToken);
        }
    }
}
