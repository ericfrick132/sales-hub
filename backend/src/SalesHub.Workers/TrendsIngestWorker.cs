using Microsoft.EntityFrameworkCore;
using SalesHub.Infrastructure.Apify;
using SalesHub.Infrastructure.Persistence;

namespace SalesHub.Workers;

public class TrendsIngestWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly IConfiguration _config;
    private readonly ILogger<TrendsIngestWorker> _log;

    private static readonly (string vertical, string[] hashtags)[] Map =
    {
        ("gymhero", new[] { "gimnasio", "crossfit", "funcional", "pilates", "yogaargentina" }),
        ("bookingpro_barber", new[] { "barberia", "barbershop", "barberiaargentina" }),
        ("bookingpro_salon", new[] { "salondebelleza", "peluqueria", "manicura" }),
        ("playcrew", new[] { "padel", "padelargentina", "clubdepadel" }),
        ("bunker", new[] { "personaltrainer", "coachfitness", "nutricionista" }),
        ("unistock", new[] { "emprendedor", "mercadolibre", "tiendanube" }),
        ("construction", new[] { "construccion", "obrasargentina", "arquitecturaar" })
    };

    public TrendsIngestWorker(IServiceScopeFactory scopes, IConfiguration config, ILogger<TrendsIngestWorker> log)
    {
        _scopes = scopes; _config = config; _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var enabled = _config.GetValue<bool>("Workers:TrendsAutoStart", false);
        if (!enabled)
        {
            _log.LogInformation("TrendsIngestWorker disabled (on-demand only). Set Workers:TrendsAutoStart=true to enable cron.");
            return;
        }
        _log.LogInformation("TrendsIngestWorker started");
        await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try { await RunOnceAsync(stoppingToken); }
            catch (Exception ex) { _log.LogError(ex, "Trends ingest failed"); }
            await Task.Delay(TimeSpan.FromHours(6), stoppingToken);
        }
    }

    private async Task RunOnceAsync(CancellationToken ct)
    {
        using var scope = _scopes.CreateScope();
        var tiktok = scope.ServiceProvider.GetRequiredService<ApifyTikTokSource>();
        foreach (var (vertical, hashtags) in Map)
        {
            foreach (var h in hashtags)
            {
                try { await tiktok.FetchHashtagAsync(h, vertical, 30, ct); }
                catch (Exception ex) { _log.LogWarning(ex, "TikTok fetch failed for #{H}", h); }
                // Spread out runs so we don't spike Apify memory budget.
                await Task.Delay(TimeSpan.FromSeconds(30), ct);
            }
        }
    }
}
