using SalesHub.Infrastructure.Persistence;
using SalesHub.Infrastructure.Services;

namespace SalesHub.Workers;

public class HumanizedSenderService : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<HumanizedSenderService> _log;

    public HumanizedSenderService(IServiceScopeFactory scopes, ILogger<HumanizedSenderService> log)
    {
        _scopes = scopes;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _log.LogInformation("HumanizedSenderService started");
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _scopes.CreateScope();
                var gateDb = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                if (!await gateDb.IsFlagOnAsync("whatsapp", scope.ServiceProvider.GetRequiredService<IConfiguration>().GetValue<bool>("Workers:WhatsAppAutoStart", true), stoppingToken))
                { await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken); continue; }
                var sender = scope.ServiceProvider.GetRequiredService<OutboxSender>();
                var sent = await sender.TickAsync(stoppingToken);
                if (sent > 0) _log.LogInformation("Sender tick dispatched {N}", sent);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Sender tick failed");
            }
            await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
        }
    }
}
