using Microsoft.Extensions.Options;
using SalesHub.Infrastructure.Instagram;
using SalesHub.Infrastructure.Persistence;

namespace SalesHub.Workers;

/// <summary>
/// Worker que procesa las campañas de auto-follow en Instagram.
/// Tickea cada minuto y por cada campaña activa ejecuta como mucho 1 follow
/// (respeta el DailyRate configurado y mete jitter entre follows).
/// </summary>
public class InstagramFollowWorker : BackgroundService
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromMinutes(1);

    private readonly IServiceScopeFactory _scopes;
    private readonly IOptions<InstagramOptions> _opts;
    private readonly ILogger<InstagramFollowWorker> _log;

    public InstagramFollowWorker(
        IServiceScopeFactory scopes,
        IOptions<InstagramOptions> opts,
        ILogger<InstagramFollowWorker> log)
    {
        _scopes = scopes;
        _opts = opts;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _log.LogInformation("InstagramFollowWorker started (interval: {Interval}s)", TickInterval.TotalSeconds);

        // Pequeña espera para no chocar con el batch login del scraper worker
        await Task.Delay(TimeSpan.FromMinutes(2), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _scopes.CreateScope();
                var gateDb = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                if (!await gateDb.IsFlagOnAsync("instagram", scope.ServiceProvider.GetRequiredService<IConfiguration>().GetValue<bool>("Workers:InstagramAutoStart", true), stoppingToken))
                { await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken); continue; }
                var service = scope.ServiceProvider.GetRequiredService<InstagramFollowService>();
                await service.RunAllAsync(stoppingToken);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                _log.LogError(ex, "InstagramFollowWorker tick failed");
            }

            await Task.Delay(TickInterval, stoppingToken);
        }
    }
}
