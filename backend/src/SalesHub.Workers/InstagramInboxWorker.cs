using SalesHub.Infrastructure.Instagram;
using SalesHub.Infrastructure.Persistence;

namespace SalesHub.Workers;

/// <summary>
/// Worker que pollea periódicamente el inbox de DMs de las cuentas de Instagram
/// y mete los mensajes entrantes en las conversaciones. Es el equivalente del
/// webhook de WhatsApp para el canal Instagram (IG no tiene webhook → polling).
/// </summary>
public class InstagramInboxWorker : BackgroundService
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromMinutes(2);

    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<InstagramInboxWorker> _log;

    public InstagramInboxWorker(IServiceScopeFactory scopes, ILogger<InstagramInboxWorker> log)
    {
        _scopes = scopes;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _log.LogInformation("InstagramInboxWorker started (interval: {Interval}min)", TickInterval.TotalMinutes);

        // Espera inicial para dejar que el login batch del scraper deje sesiones listas.
        await Task.Delay(TimeSpan.FromMinutes(3), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _scopes.CreateScope();
                var gateDb = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                if (!await gateDb.IsFlagOnAsync("instagram", scope.ServiceProvider.GetRequiredService<IConfiguration>().GetValue<bool>("Workers:InstagramAutoStart", true), stoppingToken))
                { await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken); continue; }
                var poller = scope.ServiceProvider.GetRequiredService<InstagramInboxPoller>();
                await poller.PollAllAsync(stoppingToken);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                _log.LogError(ex, "InstagramInboxWorker tick failed");
            }

            await Task.Delay(TickInterval, stoppingToken);
        }
    }
}
