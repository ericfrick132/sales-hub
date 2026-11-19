using SalesHub.Infrastructure.Services.Outbound;

namespace SalesHub.Workers;

/// <summary>
/// Línea outbound (Leads → Línea outbound): cada minuto nutre una tanda de leads (lee su web, los cruza con la
/// competencia y recalcula el puntaje de prospecto) y deja que cada línea outbound lista mande a lo sumo un
/// contacto nuevo al mejor lead. El tope de por vida, la ventana horaria y el espaciado los aplica
/// <see cref="OutboundService"/>.
/// </summary>
public class OutboundWorker : BackgroundService
{
    private static readonly TimeSpan Every = TimeSpan.FromMinutes(1);
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<OutboundWorker> _log;

    public OutboundWorker(IServiceScopeFactory scopes, ILogger<OutboundWorker> log)
    {
        _scopes = scopes; _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        _log.LogInformation("OutboundWorker started (cada {Min} min)", Every.TotalMinutes);
        await Task.Delay(TimeSpan.FromMinutes(2), ct);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var scope = _scopes.CreateScope();
                var prospects = scope.ServiceProvider.GetRequiredService<ProspectService>();
                foreach (var productKey in await prospects.ProductsToScoreAsync(ct))
                {
                    var n = await prospects.EnrichPendingAsync(productKey, 20, ct);
                    if (n > 0) _log.LogInformation("Prospectos: {N} leads de {Product} nutridos", n, productKey);
                }
                await scope.ServiceProvider.GetRequiredService<OutboundService>().TickAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex) { _log.LogError(ex, "OutboundWorker tick failed"); }
            await Task.Delay(Every, ct);
        }
    }
}
