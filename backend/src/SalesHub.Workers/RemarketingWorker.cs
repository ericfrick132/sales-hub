using Microsoft.EntityFrameworkCore;
using SalesHub.Infrastructure.Persistence;
using SalesHub.Infrastructure.Services;

namespace SalesHub.Workers;

/// <summary>
/// Campaña de remarketing (config en /remarketing): dentro de la ventana horaria, completa el
/// cupo del día de cada línea elegida con los leads mejor rankeados. Solo encola si la línea
/// puede mandar en ese momento: con el celu caído no se apilan mensajes que saldrían todos
/// juntos al volver.
/// </summary>
public class RemarketingWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<RemarketingWorker> _log;

    private static readonly TimeSpan Every = TimeSpan.FromMinutes(10);

    public RemarketingWorker(IServiceScopeFactory scopes, ILogger<RemarketingWorker> log)
    {
        _scopes = scopes; _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        _log.LogInformation("RemarketingWorker started (cada {Min} min)", Every.TotalMinutes);
        await Task.Delay(TimeSpan.FromMinutes(3), ct);
        while (!ct.IsCancellationRequested)
        {
            try { await TickAsync(ct); }
            catch (Exception ex) { _log.LogError(ex, "RemarketingWorker tick failed"); }
            await Task.Delay(Every, ct);
        }
    }

    private async Task TickAsync(CancellationToken ct)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var svc = scope.ServiceProvider.GetRequiredService<RemarketingService>();

        var s = await svc.GetSettingsAsync(ct);
        if (!s.Enabled || s.SenderSellerIds.Count == 0 || s.PerLinePerDay <= 0) return;

        var sellers = await db.Sellers.Include(x => x.EvolutionInstance)
            .Where(x => s.SenderSellerIds.Contains(x.Id))
            .ToListAsync(ct);
        foreach (var seller in sellers)
        {
            var (ok, reason, _) = await svc.LineStatusAsync(seller, ct);
            if (!ok)
            {
                _log.LogDebug("Remarketing: {Seller} no puede mandar ({Reason})", seller.DisplayName, reason);
                continue;
            }
            await svc.EnqueueForSellerAsync(s, seller, ct);
        }
    }
}
