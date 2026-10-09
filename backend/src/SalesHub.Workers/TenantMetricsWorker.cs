using SalesHub.Infrastructure.Services;

namespace SalesHub.Workers;

/// <summary>
/// Cada 3 h trae los tenants de cada app (MRR, pagos, trials, bajas) → tab Negocio del
/// Dashboard. Sin TenantMetrics:Sources configurado no hace nada.
/// </summary>
public class TenantMetricsWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<TenantMetricsWorker> _log;

    public TenantMetricsWorker(IServiceScopeFactory scopes, ILogger<TenantMetricsWorker> log)
    {
        _scopes = scopes; _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        await Task.Delay(TimeSpan.FromMinutes(2), ct);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var scope = _scopes.CreateScope();
                await scope.ServiceProvider.GetRequiredService<TenantMetricsService>().SyncAllAsync(ct);
            }
            catch (Exception ex) { _log.LogError(ex, "TenantMetricsWorker tick failed"); }
            await Task.Delay(TimeSpan.FromHours(3), ct);
        }
    }
}
