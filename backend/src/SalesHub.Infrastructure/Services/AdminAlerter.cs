using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SalesHub.Core.Abstractions;
using SalesHub.Core.Domain.Entities.Social;
using SalesHub.Infrastructure.Persistence;

namespace SalesHub.Infrastructure.Services;

/// <summary>
/// Manda el aviso al número MAESTRO por WhatsApp (misma config que el intake de
/// inspiraciones: <see cref="InspirationSettings.InstanceName"/> + MasterPhone).
/// Si no hay línea/número configurados, no-op (deja constancia en el log).
/// </summary>
public class AdminAlerter : IAdminAlerter
{
    private readonly ApplicationDbContext _db;
    private readonly IEvolutionClient _evo;
    private readonly ILogger<AdminAlerter> _log;

    public AdminAlerter(ApplicationDbContext db, IEvolutionClient evo, ILogger<AdminAlerter> log)
    {
        _db = db; _evo = evo; _log = log;
    }

    public async Task<bool> AlertAsync(string message, CancellationToken ct = default)
    {
        try
        {
            var s = await _db.Set<InspirationSettings>().AsNoTracking().FirstOrDefaultAsync(ct);
            if (s is null || string.IsNullOrWhiteSpace(s.InstanceName) || string.IsNullOrWhiteSpace(s.MasterPhone))
            {
                _log.LogWarning("AdminAlerter: no hay línea/número maestro configurado (InspirationSettings) — el aviso no se envía");
                return false;
            }
            var ok = await _evo.SendTextAsync(s.InstanceName!, s.MasterPhone!, message, ct);
            if (!ok) _log.LogWarning("AdminAlerter: Evolution no confirmó el envío del aviso");
            return ok;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "AdminAlerter: falló el envío del aviso");
            return false;
        }
    }
}
