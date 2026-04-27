using Microsoft.EntityFrameworkCore;
using SalesHub.Core.Domain.Enums;
using SalesHub.Infrastructure.Persistence;
using SalesHub.Infrastructure.Services;

namespace SalesHub.Workers;

/// <summary>
/// Runs the Apify pipeline on a cadence. Each product has its own trigger_hours set;
/// this service wakes every 5 min and dispatches runs for products that match the current hour.
/// Disabled by default: set Workers:PipelineAutoStart=true in config to enable.
/// </summary>
public class PipelineSchedulerService : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly IConfiguration _config;
    private readonly ILogger<PipelineSchedulerService> _log;
    private readonly HashSet<string> _ranThisHour = new();
    private int _lastHour = -1;

    public PipelineSchedulerService(IServiceScopeFactory scopes, IConfiguration config, ILogger<PipelineSchedulerService> log)
    {
        _scopes = scopes; _config = config; _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var enabled = _config.GetValue<bool>("Workers:PipelineAutoStart", false);
        if (!enabled)
        {
            _log.LogInformation("PipelineSchedulerService disabled (set Workers:PipelineAutoStart=true to enable). Admin triggers runs from /pipeline.");
            return;
        }
        _log.LogInformation("PipelineSchedulerService started (autoStart=true)");
        await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try { await TickAsync(stoppingToken); }
            catch (Exception ex) { _log.LogError(ex, "Pipeline scheduler tick failed"); }
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

            _log.LogInformation("Pipeline triggered for {Product} at hour {H}", p.ProductKey, localNow.Hour);
            var opts = new PipelineRunOptions(
                ProductKey: p.ProductKey,
                Sources: new[] { LeadSource.GooglePlaces },
                City: null, Province: null, Category: null,
                MaxPerSource: 20, AutoQueueMessages: true);
            var created = await pipeline.RunAsync(opts, ct);
            _log.LogInformation("Pipeline created {N} leads for {Product}", created, p.ProductKey);
        }
    }
}
