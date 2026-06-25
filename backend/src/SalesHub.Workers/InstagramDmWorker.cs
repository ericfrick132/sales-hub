using Microsoft.Extensions.Options;
using SalesHub.Infrastructure.Instagram;
using SalesHub.Infrastructure.Persistence;

namespace SalesHub.Workers;

/// <summary>
/// Worker que despacha la cola de DMs de Instagram (MessageOutbox con
/// Channel == Instagram). Manda como mucho 1 DM por tick; el intervalo del tick
/// es el pacing anti-ban: con ~3 min entre envíos quedamos por debajo del
/// <c>MaxDmPerHour</c> configurado, y el cap diario por cuenta lo aplica el sender.
/// </summary>
public class InstagramDmWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly IOptions<InstagramOptions> _opts;
    private readonly ILogger<InstagramDmWorker> _log;

    public InstagramDmWorker(
        IServiceScopeFactory scopes,
        IOptions<InstagramOptions> opts,
        ILogger<InstagramDmWorker> log)
    {
        _scopes = scopes;
        _opts = opts;
        _log = log;
    }

    /// <summary>Intervalo entre DMs. Derivado de MaxDmPerHour (mín. 1 min, default ~3 min).</summary>
    private TimeSpan TickInterval
    {
        get
        {
            var perHour = Math.Max(1, _opts.Value.MaxDmPerHour);
            var minutes = Math.Max(1, 60 / perHour);
            return TimeSpan.FromMinutes(minutes);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TickInterval;
        _log.LogInformation("InstagramDmWorker started (interval: {Interval}min)", interval.TotalMinutes);

        // Espera inicial para no chocar con el batch login del scraper/follow workers.
        await Task.Delay(TimeSpan.FromMinutes(2), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _scopes.CreateScope();
                var gateDb = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                if (!await gateDb.IsFlagOnAsync("instagram", scope.ServiceProvider.GetRequiredService<IConfiguration>().GetValue<bool>("Workers:InstagramAutoStart", true), stoppingToken))
                { await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken); continue; }
                var sender = scope.ServiceProvider.GetRequiredService<InstagramDmSender>();
                await sender.TickAsync(stoppingToken);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                _log.LogError(ex, "InstagramDmWorker tick failed");
            }

            await Task.Delay(interval, stoppingToken);
        }
    }
}
