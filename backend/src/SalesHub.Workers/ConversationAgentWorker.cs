using SalesHub.Infrastructure.Services;

namespace SalesHub.Workers;

/// <summary>
/// Tick periódico que procesa los mensajes inbound: transcribe notas de voz
/// (y en Fase 2, genera la respuesta sugerida por IA).
/// </summary>
public class ConversationAgentWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<ConversationAgentWorker> _log;

    public ConversationAgentWorker(IServiceScopeFactory scopes, ILogger<ConversationAgentWorker> log)
    {
        _scopes = scopes;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _log.LogInformation("ConversationAgentWorker started");
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _scopes.CreateScope();
                var svc = scope.ServiceProvider.GetRequiredService<ConversationAgentService>();
                var n = await svc.TickAsync(stoppingToken);
                if (n > 0) _log.LogInformation("ConversationAgent processed {N}", n);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "ConversationAgent tick failed");
            }
            await Task.Delay(TimeSpan.FromSeconds(20), stoppingToken);
        }
    }
}
