using SalesHub.Infrastructure.Services;

namespace SalesHub.Workers;

/// <summary>
/// Sincroniza los anuncios de Meta cada hora (ver <see cref="MetaAdsSyncService"/>): así un anuncio
/// nuevo queda reconocido por su texto pre-armado sin tocar nada. Sin Meta:AdsAccessToken no hace nada.
/// Cada 6 h dispara también el rendimiento por provincia (<see cref="MetaGeoSyncService"/>, mapa de anuncios).
/// </summary>
public class MetaAdsSyncWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<MetaAdsSyncWorker> _log;

    public MetaAdsSyncWorker(IServiceScopeFactory scopes, ILogger<MetaAdsSyncWorker> log)
    {
        _scopes = scopes; _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _scopes.CreateScope();
                var sync = scope.ServiceProvider.GetRequiredService<MetaAdsSyncService>();
                if (sync.IsConfigured) await sync.SyncAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { _log.LogError(ex, "Meta ads sync falló"); }
            // Rendimiento por provincia (mapa de anuncios): cada 6 h, después de los anuncios así ya tienen app.
            try
            {
                using var scope = _scopes.CreateScope();
                var geo = scope.ServiceProvider.GetRequiredService<MetaGeoSyncService>();
                if (geo.IsConfigured && await geo.IsDueAsync(stoppingToken)) await geo.SyncAsync(null, stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { _log.LogError(ex, "Meta geo sync falló"); }
            await Task.Delay(TimeSpan.FromHours(1), stoppingToken);
        }
    }
}
