using SalesHub.Infrastructure.Services;

namespace SalesHub.Workers;

public class InstanceMonitorService : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<InstanceMonitorService> _log;

    public InstanceMonitorService(IServiceScopeFactory scopes, ILogger<InstanceMonitorService> log)
    {
        _scopes = scopes; _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _log.LogInformation("InstanceMonitorService started");
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _scopes.CreateScope();
                var monitor = scope.ServiceProvider.GetRequiredService<InstanceMonitor>();
                await monitor.TickAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Instance monitor tick failed");
            }
            await Task.Delay(TimeSpan.FromSeconds(60), stoppingToken);
        }
    }
}
