using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using SalesHub.Core.Abstractions;
using SalesHub.Core.Domain.Entities.Social;
using SalesHub.Infrastructure.Persistence;

namespace SalesHub.Infrastructure.Services;

/// <summary>
/// Manda el aviso ("📞 LLAMAR YA", alertas de SLA, etc.) al número MAESTRO por WhatsApp.
///
/// El destinatario es <see cref="InspirationSettings.MasterPhone"/> (con <c>Alerts:Phone</c> de
/// respaldo). La línea que manda se prueba en orden: <c>Alerts:InstanceName</c> (la línea elegida
/// para avisos) y después <see cref="InspirationSettings.InstanceName"/>. Antes salía solo por la
/// de inspiraciones ("Eric Frick", +1 689…): esa línea se desvinculó el 30/07/2026 y desde ahí
/// ningún aviso llegó; cada fallo quedaba como un warning que nadie veía.
///
/// Nunca manda por una línea de solo escucha: eso lo bloquea el propio cliente de Evolution.
/// </summary>
public class AdminAlerter : IAdminAlerter
{
    private readonly ApplicationDbContext _db;
    private readonly IEvolutionClient _evo;
    private readonly IConfiguration _config;
    private readonly ILogger<AdminAlerter> _log;

    public AdminAlerter(ApplicationDbContext db, IEvolutionClient evo, IConfiguration config, ILogger<AdminAlerter> log)
    {
        _db = db; _evo = evo; _config = config; _log = log;
    }

    public async Task<bool> AlertAsync(string message, CancellationToken ct = default)
    {
        try
        {
            var s = await _db.Set<InspirationSettings>().AsNoTracking().FirstOrDefaultAsync(ct);
            var phone = FirstNonEmpty(s?.MasterPhone, _config["Alerts:Phone"]);
            if (phone is null)
            {
                _log.LogError("AdminAlerter: no hay número maestro configurado (InspirationSettings.MasterPhone / Alerts:Phone) — el aviso no se envía");
                return false;
            }

            var lines = new[] { _config["Alerts:InstanceName"], s?.InstanceName }
                .Where(i => !string.IsNullOrWhiteSpace(i))
                .Select(i => i!.Trim())
                .Distinct(StringComparer.Ordinal)
                .ToList();
            if (lines.Count == 0)
            {
                _log.LogError("AdminAlerter: no hay línea configurada para avisos (Alerts:InstanceName / InspirationSettings.InstanceName) — el aviso no se envía");
                return false;
            }

            foreach (var line in lines)
            {
                if (await _evo.SendTextAsync(line, phone, message, ct)) return true;
                _log.LogWarning("AdminAlerter: el aviso no salió por {Line}; pruebo la siguiente", line);
            }

            _log.LogError("AdminAlerter: el aviso NO salió por ninguna línea ({Lines}). Revisá que estén conectadas.",
                string.Join(", ", lines));
            return false;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "AdminAlerter: falló el envío del aviso");
            return false;
        }
    }

    private static string? FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v))?.Trim();
}
