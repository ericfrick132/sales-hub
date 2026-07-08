using Microsoft.EntityFrameworkCore;
using SalesHub.Infrastructure.Persistence;
using SalesHub.Infrastructure.Services;

namespace SalesHub.Workers;

/// <summary>
/// Tick del auto-reparto de leads (ver <see cref="LeadRebalancer"/>). Corre cada 5 min:
/// cuando un vendedor conecta su WhatsApp (el InstanceMonitor lo marca Connected en &lt;60s),
/// el próximo tick rescata congelados, re-intenta el pool y drena backlog hacia la línea
/// nueva — sin tocar ningún botón. Gate por flag DB 'rebalance' (pisa
/// Workers:RebalanceAutoStart, default true) → toggle desde la UI.
/// </summary>
public class LeadRebalanceWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly IConfiguration _config;
    private readonly ILogger<LeadRebalanceWorker> _log;

    public LeadRebalanceWorker(IServiceScopeFactory scopes, IConfiguration config, ILogger<LeadRebalanceWorker> log)
    {
        _scopes = scopes; _config = config; _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _log.LogInformation("LeadRebalanceWorker started (gate por flag 'rebalance' / Workers:RebalanceAutoStart)");
        await Task.Delay(TimeSpan.FromMinutes(2), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try { await TickAsync(stoppingToken); }
            catch (Exception ex) { _log.LogError(ex, "Lead rebalance tick failed"); }
            await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);
        }
    }

    private async Task TickAsync(CancellationToken ct)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        if (!await db.IsFlagOnAsync("rebalance", _config.GetValue<bool>("Workers:RebalanceAutoStart", true), ct))
            return;
        var rebalancer = scope.ServiceProvider.GetRequiredService<LeadRebalancer>();
        await rebalancer.RebalanceAsync(ct);
    }
}
