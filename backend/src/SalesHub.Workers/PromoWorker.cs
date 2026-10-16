using SalesHub.Infrastructure.Persistence;
using SalesHub.Infrastructure.Services.Promos;

namespace SalesHub.Workers;

/// <summary>
/// Cada 30 min corre el motor de promos: lanza las campañas de los eventos que entraron en
/// ventana (7 días antes) y cierra las vencidas. Gate: flag 'promos' (default Workers:PromosAutoStart=true).
/// </summary>
public class PromoWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly IConfiguration _config;
    private readonly ILogger<PromoWorker> _log;

    public PromoWorker(IServiceScopeFactory scopes, IConfiguration config, ILogger<PromoWorker> log)
    {
        _scopes = scopes; _config = config; _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        await Task.Delay(TimeSpan.FromMinutes(3), ct);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var scope = _scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                if (await db.IsFlagOnAsync("promos", _config.GetValue<bool>("Workers:PromosAutoStart", true), ct))
                {
                    var r = await scope.ServiceProvider.GetRequiredService<PromoEngine>().TickAsync(ct);
                    if (r.Launched + r.Ended + r.Errors > 0)
                        _log.LogInformation("Promos: {L} lanzadas, {E} cerradas, {X} con error", r.Launched, r.Ended, r.Errors);
                }
            }
            catch (Exception ex) { _log.LogError(ex, "PromoWorker tick failed"); }
            await Task.Delay(TimeSpan.FromMinutes(30), ct);
        }
    }
}
