using SalesHub.Infrastructure.Services;

namespace SalesHub.Workers;

/// <summary>
/// Vence las tareas de Claude Code que la Mac nunca tomó (12 h) o tomó y no devolvió (45 min), y le avisa a
/// Eric por WhatsApp. El poller también barre al consultar; esto cubre el caso de la Mac apagada.
/// </summary>
public class ClaudeTaskSweepWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<ClaudeTaskSweepWorker> _log;

    public ClaudeTaskSweepWorker(IServiceScopeFactory scopes, ILogger<ClaudeTaskSweepWorker> log)
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
                await scope.ServiceProvider.GetRequiredService<ClaudeTaskService>().SweepAsync(stoppingToken);
            }
            catch (Exception ex) { _log.LogError(ex, "Claude task sweep failed"); }
            await Task.Delay(TimeSpan.FromMinutes(10), stoppingToken);
        }
    }
}
