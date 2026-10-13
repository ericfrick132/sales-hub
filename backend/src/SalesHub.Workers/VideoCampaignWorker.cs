using Microsoft.EntityFrameworkCore;
using SalesHub.Core.Domain.Entities;
using SalesHub.Infrastructure.Persistence;
using SalesHub.Infrastructure.Services;

namespace SalesHub.Workers;

/// <summary>
/// Campañas con video activas: dentro de la franja y los días elegidos, manda de a un lead,
/// repartiendo el tope del día en la franja (ver <see cref="VideoCampaignService.GapMinutes"/>).
/// Si la línea se cae, espera; cuando no queda nadie pendiente, la campaña termina.
/// </summary>
public class VideoCampaignWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<VideoCampaignWorker> _log;

    public VideoCampaignWorker(IServiceScopeFactory scopes, ILogger<VideoCampaignWorker> log)
    {
        _scopes = scopes; _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        await Task.Delay(TimeSpan.FromMinutes(2), ct);
        while (!ct.IsCancellationRequested)
        {
            try { await TickAsync(ct); }
            catch (Exception ex) when (ex is not OperationCanceledException) { _log.LogError(ex, "VideoCampaignWorker tick failed"); }
            await Task.Delay(TimeSpan.FromMinutes(1), ct);
        }
    }

    private async Task TickAsync(CancellationToken ct)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var svc = scope.ServiceProvider.GetRequiredService<VideoCampaignService>();

        var active = await db.VideoCampaigns.Where(c => c.Status == VideoCampaignStatus.Active).ToListAsync(ct);
        var art = VideoCampaignService.ArtNow();
        foreach (var c in active)
        {
            if (!VideoCampaignService.InWindow(c, art)) continue;
            if (c.LastSentAt is { } last && DateTimeOffset.UtcNow - last < TimeSpan.FromMinutes(VideoCampaignService.GapMinutes(c))) continue;
            if (await svc.SentTodayAsync(c, ct) >= c.PerDay) continue;
            if (await svc.LineProblemAsync(c.InstanceName, ct) is { } problem)
            {
                _log.LogDebug("Campaña con video {Name}: no manda ({Problem})", c.Name, problem);
                continue;
            }
            if (!await svc.SendNextAsync(c, ct))
            {
                c.Status = VideoCampaignStatus.Done;
                await db.SaveChangesAsync(ct);
                _log.LogInformation("Campaña con video {Name}: terminada", c.Name);
            }
        }
    }
}
