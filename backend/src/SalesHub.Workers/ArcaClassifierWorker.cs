using SalesHub.Infrastructure.Arca;
using SalesHub.Infrastructure.Persistence;

namespace SalesHub.Workers;

/// <summary>
/// Le pone rubro ARCA (CLAE F.883) a los leads nuevos y reclasifica cuando cambian las reglas
/// (<see cref="ArcaLeadClassifier.RulesVersion"/>). Corre al minuto de arrancar y después cada hora;
/// es barato porque solo toca los pendientes.
/// </summary>
public class ArcaClassifierWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<ArcaClassifierWorker> _log;

    public ArcaClassifierWorker(IServiceScopeFactory scopes, ILogger<ArcaClassifierWorker> log)
    {
        _scopes = scopes; _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        _log.LogInformation("ArcaClassifierWorker started (cada 60 min, reglas {Version})", ArcaLeadClassifier.RulesVersion);
        await Task.Delay(TimeSpan.FromMinutes(1), ct);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var scope = _scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                await ArcaLeadClassifier.ClassifyPendingAsync(db, _log, ct: ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                _log.LogError(ex, "ArcaClassifierWorker tick failed");
            }
            await Task.Delay(TimeSpan.FromMinutes(60), ct);
        }
    }
}
