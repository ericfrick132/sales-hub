using Microsoft.EntityFrameworkCore;
using SalesHub.Core.Domain.Enums;
using SalesHub.Infrastructure.Persistence;
using SalesHub.Infrastructure.Services;

namespace SalesHub.Workers;

/// <summary>
/// Dispatches the Google Places-only pipeline. The per-day cap (Google:PlacesDailyCap)
/// is enforced inside PipelineService; this service just wakes up at each product's
/// TriggerHours and asks the pipeline to do one run.
/// Enabled by default: set Workers:GooglePlacesAutoStart=false to disable.
/// </summary>
public class GooglePlacesSchedulerService : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly IConfiguration _config;
    private readonly ILogger<GooglePlacesSchedulerService> _log;
    private readonly HashSet<string> _ranThisHour = new();
    private int _lastHour = -1;

    public GooglePlacesSchedulerService(IServiceScopeFactory scopes, IConfiguration config, ILogger<GooglePlacesSchedulerService> log)
    {
        _scopes = scopes; _config = config; _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var enabled = _config.GetValue<bool>("Workers:GooglePlacesAutoStart", true);
        if (!enabled)
        {
            _log.LogInformation("GooglePlacesScheduler disabled (Workers:GooglePlacesAutoStart=false).");
            return;
        }
        _log.LogInformation("GooglePlacesScheduler started");
        await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try { await TickAsync(stoppingToken); }
            catch (Exception ex) { _log.LogError(ex, "GooglePlacesScheduler tick failed"); }
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

            _log.LogInformation("GooglePlaces run for {Product} at hour {H}", p.ProductKey, localNow.Hour);
            var opts = new PipelineRunOptions(
                ProductKey: p.ProductKey,
                Sources: new[] { LeadSource.GooglePlaces },
                City: null, Province: null, Category: null,
                MaxPerSource: 20, AutoQueueMessages: true);
            try
            {
                var created = await pipeline.RunAsync(opts, ct);
                _log.LogInformation("GooglePlaces created {N} leads for {Product}", created, p.ProductKey);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "GooglePlaces run failed for {Product}", p.ProductKey);
            }
        }
    }
}
