using Microsoft.EntityFrameworkCore;
using SalesHub.Core.Abstractions;
using SalesHub.Infrastructure.Persistence;

namespace SalesHub.Workers;

/// <summary>
/// Vigilante del SLA de atención: cada pocos minutos busca chats donde el lead escribió y
/// nadie contestó dentro de <c>Sla:ThresholdMinutes</c>, y manda UN aviso agrupado al número
/// maestro. Existe porque el histórico mostró 714 turnos que nunca se contestaron: el problema
/// no es la velocidad de respuesta, es que un chat se cuelga y nadie se entera.
///
/// Config (appsettings / env):
///   Sla:ThresholdMinutes   minutos sin responder que disparan el aviso (10)
///   Sla:CheckMinutes       cada cuánto revisa (3)
///   Sla:MaxAgeHours        antigüedad máxima a considerar, para no alertar deuda vieja al prender (24)
///   Sla:QuietStartHour     hora AR desde la que NO molesta (23)
///   Sla:QuietEndHour       hora AR hasta la que NO molesta (8)
/// Flag de runtime: 'sla-alerts' (la fila en RuntimeFlags manda sobre la config).
/// </summary>
public class SlaAlertWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly IConfiguration _config;
    private readonly ILogger<SlaAlertWorker> _log;

    public SlaAlertWorker(IServiceScopeFactory scopes, IConfiguration config, ILogger<SlaAlertWorker> log)
    {
        _scopes = scopes; _config = config; _log = log;
    }

    /// <summary>Cuántos chats se listan en el aviso antes de resumir con "y N más".</summary>
    private const int MaxListed = 8;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var every = Math.Clamp(_config.GetValue<int?>("Sla:CheckMinutes") ?? 3, 1, 60);
        _log.LogInformation("SlaAlertWorker started (cada {Min} min)", every);
        await Task.Delay(TimeSpan.FromMinutes(2), ct);
        while (!ct.IsCancellationRequested)
        {
            try { await TickAsync(ct); }
            catch (Exception ex) { _log.LogError(ex, "SlaAlertWorker tick failed"); }
            await Task.Delay(TimeSpan.FromMinutes(every), ct);
        }
    }

    private async Task TickAsync(CancellationToken ct)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var fallback = _config.GetValue<bool?>("Workers:SlaAlertsAutoStart") ?? true;
        if (!await db.IsFlagOnAsync("sla-alerts", fallback, ct)) return;

        var threshold = Math.Clamp(_config.GetValue<int?>("Sla:ThresholdMinutes") ?? 10, 1, 24 * 60);
        var maxAge = Math.Clamp(_config.GetValue<int?>("Sla:MaxAgeHours") ?? 24, 1, 24 * 30);

        if (InQuietHours()) return;

        var rt = scope.ServiceProvider.GetRequiredService<IResponseTimeService>();
        var waiting = await rt.GetWaitingAsync(null, maxAge, 200, ct);

        // Se avisa una vez por espera: si el lead vuelve a escribir después de que le
        // contestamos, WaitingSince avanza y el chat entra de nuevo al radar.
        var due = waiting
            .Where(w => w.MinutesWaiting > threshold)
            .Where(w => w.SlaAlertedAt is null || w.SlaAlertedAt < w.WaitingSince)
            .OrderBy(w => w.WaitingSince)
            .ToList();
        if (due.Count == 0) return;

        var lines = due.Take(MaxListed).Select(w =>
        {
            var mins = (int)w.MinutesWaiting;
            var espera = mins >= 120 ? $"{mins / 60} hs" : $"{mins} min";
            var texto = w.LastText.Replace("\n", " ").Trim();
            if (texto.Length > 60) texto = texto[..60] + "...";
            return $"- {w.LeadName} ({w.ProductKey}) espera {espera}: \"{texto}\" {WaLink(w.Phone)}";
        });

        var titulo = due.Count == 1
            ? $"SLA: 1 chat sin responder hace mas de {threshold} min"
            : $"SLA: {due.Count} chats sin responder hace mas de {threshold} min";
        var extra = due.Count > MaxListed ? $"\ny {due.Count - MaxListed} mas en el panel de Atencion." : "";
        var msg = $"{titulo}\n{string.Join("\n", lines)}{extra}";

        var alerter = scope.ServiceProvider.GetRequiredService<IAdminAlerter>();
        var sent = await alerter.AlertAsync(msg, ct);
        if (!sent)
        {
            // No marcamos nada: si el aviso no salió (línea caída), se reintenta al próximo tick.
            _log.LogWarning("SLA: {N} chats colgados pero no se pudo avisar", due.Count);
            return;
        }

        var ids = due.Select(w => w.LeadId).ToList();
        var leads = await db.Leads.Where(l => ids.Contains(l.Id)).ToListAsync(ct);
        var now = DateTimeOffset.UtcNow;
        foreach (var l in leads) l.SlaAlertedAt = now;
        await db.SaveChangesAsync(ct);
        _log.LogInformation("SLA: aviso enviado por {N} chats colgados", due.Count);
    }

    /// <summary>Horario de silencio en hora AR: de noche no tiene sentido despertar a nadie.</summary>
    private bool InQuietHours()
    {
        var start = Math.Clamp(_config.GetValue<int?>("Sla:QuietStartHour") ?? 23, 0, 23);
        var end = Math.Clamp(_config.GetValue<int?>("Sla:QuietEndHour") ?? 8, 0, 23);
        if (start == end) return false;

        TimeZoneInfo tz;
        try { tz = TimeZoneInfo.FindSystemTimeZoneById("America/Argentina/Buenos_Aires"); }
        catch { tz = TimeZoneInfo.CreateCustomTimeZone("AR", TimeSpan.FromHours(-3), "AR", "AR"); }
        var hour = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, tz).Hour;

        // Ventana que cruza medianoche (23 → 8) o ventana normal.
        return start > end ? hour >= start || hour < end : hour >= start && hour < end;
    }

    private static string WaLink(string phone)
    {
        var digits = new string((phone ?? "").Where(char.IsDigit).ToArray());
        return digits.Length == 0 ? "" : $"https://wa.me/{digits}";
    }
}
