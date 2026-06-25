using Microsoft.EntityFrameworkCore;
using SalesHub.Core.Abstractions;
using SalesHub.Core.Domain.Enums;
using SalesHub.Infrastructure.Persistence;

namespace SalesHub.Workers;

/// <summary>
/// Pull-guard del follow-up: cada N min consulta el estado de los leads B2B (los que tienen
/// ExternalId) en su producto de origen. Si el producto dice "stopFollowup" (ya completó setup /
/// pagó / churneó), cancela el outbox pendiente y cierra el lead (Won→Closed, !Won→Lost). Así no
/// perseguimos a quien ya convirtió. Config: LeadImport:Sources[] = { ProductKey, StateUrl, ApiKey }.
/// </summary>
public class LeadStateGuardWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly IConfiguration _config;
    private readonly ILogger<LeadStateGuardWorker> _log;

    public LeadStateGuardWorker(IServiceScopeFactory scopes, IConfiguration config, ILogger<LeadStateGuardWorker> log)
    {
        _scopes = scopes; _config = config; _log = log;
    }

    private record SourceCfg(string ProductKey, string StateUrl, string ApiKey);

    private static readonly LeadStatus[] ActiveStatuses =
    {
        LeadStatus.Assigned, LeadStatus.Queued, LeadStatus.Sent,
        LeadStatus.Replied, LeadStatus.Interested, LeadStatus.DemoScheduled
    };

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var intervalMin = _config.GetValue<int?>("LeadImport:StateGuardMinutes") ?? 5;
        _log.LogInformation("LeadStateGuardWorker started (cada {Min} min)", intervalMin);
        await Task.Delay(TimeSpan.FromMinutes(1), ct);
        while (!ct.IsCancellationRequested)
        {
            try { await TickAsync(ct); }
            catch (Exception ex) { _log.LogError(ex, "LeadStateGuardWorker tick failed"); }
            await Task.Delay(TimeSpan.FromMinutes(intervalMin), ct);
        }
    }

    private List<SourceCfg> ReadSources()
    {
        var list = new List<SourceCfg>();
        foreach (var c in _config.GetSection("LeadImport:Sources").GetChildren())
        {
            var pk = c["ProductKey"]; var url = c["StateUrl"]; var key = c["ApiKey"];
            if (!string.IsNullOrWhiteSpace(pk) && !string.IsNullOrWhiteSpace(url) && !string.IsNullOrWhiteSpace(key))
                list.Add(new SourceCfg(pk!, url!.TrimEnd('/'), key!));
        }
        return list;
    }

    private async Task TickAsync(CancellationToken ct)
    {
        var sources = ReadSources();
        if (sources.Count == 0) return; // sin StateUrl configurado, no hace nada

        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var client = scope.ServiceProvider.GetRequiredService<IProductStateClient>();

        foreach (var src in sources)
        {
            var leads = await db.Leads
                .Where(l => l.ProductKey == src.ProductKey && l.ExternalId != null && ActiveStatuses.Contains(l.Status))
                .Take(200).ToListAsync(ct);
            if (leads.Count == 0) continue;

            var byExt = leads.Where(l => l.ExternalId != null)
                .GroupBy(l => l.ExternalId!).ToDictionary(g => g.Key, g => g.First());
            var states = await client.GetStatesAsync(src.StateUrl, src.ApiKey, byExt.Keys.ToList(), ct);

            var changed = 0;
            foreach (var st in states)
            {
                if (!st.StopFollowup) continue;
                if (!byExt.TryGetValue(st.ExternalId, out var lead)) continue;

                var pending = await db.Outbox
                    .Where(o => o.LeadId == lead.Id && o.Status == OutboxStatus.Scheduled)
                    .ToListAsync(ct);
                foreach (var o in pending)
                {
                    o.Status = OutboxStatus.Cancelled;
                    o.Error = "convirtió en el producto (state-guard)";
                }

                lead.Status = st.Won ? LeadStatus.Closed : LeadStatus.Lost;
                lead.ClosedAt ??= DateTimeOffset.UtcNow;
                lead.UpdatedAt = DateTimeOffset.UtcNow;
                lead.Notes = $"{(string.IsNullOrEmpty(lead.Notes) ? "" : lead.Notes + " | ")}" +
                             $"Stop follow-up: {st.Status ?? (st.Won ? "won" : "lost")} (state-guard)";
                changed++;
            }

            if (changed > 0)
            {
                await db.SaveChangesAsync(ct);
                _log.LogInformation("StateGuard {Pk}: {N} leads cerrados por estado del producto", src.ProductKey, changed);
            }
        }
    }
}
