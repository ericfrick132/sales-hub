using SalesHub.Infrastructure.Services;

namespace SalesHub.Workers;

/// <summary>
/// Sincroniza los anuncios de Meta cada hora (ver <see cref="MetaAdsSyncService"/>): así un anuncio
/// nuevo queda reconocido por su texto pre-armado sin tocar nada. Sin Meta:AdsAccessToken no hace nada.
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
            await Task.Delay(TimeSpan.FromHours(1), stoppingToken);
        }
    }
}
