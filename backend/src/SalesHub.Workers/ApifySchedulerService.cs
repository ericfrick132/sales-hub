using Microsoft.EntityFrameworkCore;
using SalesHub.Core.Domain.Enums;
using SalesHub.Infrastructure.Persistence;
using SalesHub.Infrastructure.Services;

namespace SalesHub.Workers;

/// <summary>
/// Dispatches the Apify-source pipeline (Google Maps actor, Meta Ads, Instagram, etc.).
/// Disabled by default: set Workers:ApifyAutoStart=true to enable.
/// Per-day cap (Apify:DailyRunCap) is enforced inside PipelineService and the
/// existing ApifyUsageMonitor circuit breaker still trips on platform saturation.
/// </summary>
public class ApifySchedulerService : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly IConfiguration _config;
    private readonly ILogger<ApifySchedulerService> _log;
    private readonly HashSet<string> _ranThisHour = new();
    private int _lastHour = -1;

    public ApifySchedulerService(IServiceScopeFactory scopes, IConfiguration config, ILogger<ApifySchedulerService> log)
    {
        _scopes = scopes; _config = config; _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var enabled = _config.GetValue<bool>("Workers:ApifyAutoStart", false);
        if (!enabled)
        {
            _log.LogInformation("ApifyScheduler disabled (Workers:ApifyAutoStart=false). Admin can still trigger via /pipeline.");
            return;
        }
        _log.LogInformation("ApifyScheduler started");
        await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try { await TickAsync(stoppingToken); }
            catch (Exception ex) { _log.LogError(ex, "ApifyScheduler tick failed"); }
            await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);
        }
    }

    private async Task TickAsync(CancellationToken ct)
    {
        var localNow = DateTime.Now;
        if (localNow.Hour != _lastHour)
        {
            _lastHour = localNow.Hour;
            _ranThisHour.Clear();
        }

        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var pipeline = scope.ServiceProvider.GetRequiredService<PipelineService>();

        var products = await db.Products.Where(p => p.Active).ToListAsync(ct);
        foreach (var p in products)
        {
            if (_ranThisHour.Contains(p.ProductKey)) continue;
            if (!(p.TriggerHours?.Contains(localNow.Hour) ?? false)) continue;
            _ranThisHour.Add(p.ProductKey);

            _log.LogInformation("Apify run for {Product} at hour {H}", p.ProductKey, localNow.Hour);
            var opts = new PipelineRunOptions(
                ProductKey: p.ProductKey,
                Sources: new[] { LeadSource.ApifyGoogleMaps },
                City: null, Province: null, Category: null,
                MaxPerSource: 30, AutoQueueMessages: true);
            try
            {
                var created = await pipeline.RunAsync(opts, ct);
                _log.LogInformation("Apify created {N} leads for {Product}", created, p.ProductKey);
            }
            catch (PipelineService.CircuitBreakerException ex)
            {
                _log.LogWarning("Apify circuit breaker for {Product}: {Reason}", p.ProductKey, ex.Message);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Apify run failed for {Product}", p.ProductKey);
            }
        }
    }
}
