using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SalesHub.Core.Abstractions;
using SalesHub.Core.Domain.Enums;
using SalesHub.Infrastructure.Persistence;

namespace SalesHub.Infrastructure.Services;

public class InstanceMonitor
{
    private readonly ApplicationDbContext _db;
    private readonly IEvolutionClient _evo;
    private readonly ILogger<InstanceMonitor> _log;

    public InstanceMonitor(ApplicationDbContext db, IEvolutionClient evo, ILogger<InstanceMonitor> log)
    {
        _db = db; _evo = evo; _log = log;
    }

    public async Task TickAsync(CancellationToken ct)
    {
        var instances = await _db.EvolutionInstances.ToListAsync(ct);
        foreach (var inst in instances)
        {
            try
            {
                var info = await _evo.GetInstanceStatusAsync(inst.InstanceName, ct);
                var status = info.Status switch
                {
                    "open" or "connected" => InstanceStatus.Connected,
                    "connecting" or "qr" => InstanceStatus.Connecting,
                    "close" or "disconnected" or "not_found" => InstanceStatus.Disconnected,
                    _ => InstanceStatus.Unknown
                };
                if (status != inst.Status)
                {
                    inst.Status = status;
                    inst.UpdatedAt = DateTimeOffset.UtcNow;
                    if (status == InstanceStatus.Connected && inst.ConnectedAt is null)
                        inst.ConnectedAt = DateTimeOffset.UtcNow;
                    if (status == InstanceStatus.Disconnected)
                        inst.DisconnectedAt = DateTimeOffset.UtcNow;
                }
                inst.LastStatusCheckAt = DateTimeOffset.UtcNow;
            }
            catch (Exception ex)
            {
                _log.LogDebug(ex, "Instance {N} status check failed", inst.InstanceName);
            }
        }
        await _db.SaveChangesAsync(ct);
    }
}
