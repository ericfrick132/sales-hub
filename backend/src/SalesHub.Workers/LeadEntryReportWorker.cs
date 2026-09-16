using SalesHub.Infrastructure.Persistence;
using SalesHub.Infrastructure.Services;

namespace SalesHub.Workers;

/// <summary>
/// Manda una vez por día, a partir de la hora configurada (Argentina), el reporte de cuántos
/// leads entraron ayer a cada teléfono. La config vive en <c>lead_entry_report_settings</c>
/// (se edita en /entradas). El día reportado queda guardado en la fila, así un deploy o un
/// reinicio no lo manda dos veces; si no hay línea viva reintenta cada 30 min hasta medianoche.
/// </summary>
public class LeadEntryReportWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<LeadEntryReportWorker> _log;

    private static readonly TimeSpan Every = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan RetryAfterFailure = TimeSpan.FromMinutes(30);

    public LeadEntryReportWorker(IServiceScopeFactory scopes, ILogger<LeadEntryReportWorker> log)
    {
        _scopes = scopes; _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        _log.LogInformation("LeadEntryReportWorker started (cada {Min} min)", Every.TotalMinutes);
        await Task.Delay(TimeSpan.FromMinutes(2), ct);
        while (!ct.IsCancellationRequested)
        {
            try { await TickAsync(ct); }
            catch (Exception ex) { _log.LogError(ex, "LeadEntryReportWorker tick failed"); }
            await Task.Delay(Every, ct);
        }
    }

    private async Task TickAsync(CancellationToken ct)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var svc = scope.ServiceProvider.GetRequiredService<LeadEntryService>();

        var settings = await svc.GetSettingsAsync(ct);
        if (!settings.Enabled) return;

        var nowAr = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, LeadEntryService.ArTz);
        if (nowAr.Hour < Math.Clamp(settings.SendHour, 0, 23)) return;

        var day = LeadEntryService.TodayAr().AddDays(-1);
        if (settings.LastReportedDay >= day) return;
        if (settings.LastAttemptAt is { } last && settings.LastError is not null
            && DateTimeOffset.UtcNow - last < RetryAfterFailure) return;

        var result = await svc.SendReportAsync(day, ct);
        var now = DateTimeOffset.UtcNow;
        settings.LastAttemptAt = now;
        settings.UpdatedAt = now;
        if (result.Ok)
        {
            settings.LastReportedDay = day;
            settings.LastSentAt = now;
            settings.LastError = null;
            _log.LogInformation("Reporte de leads del {Day} enviado por {Via}", day, result.Via);
        }
        else
        {
            settings.LastError = result.Error?[..Math.Min(result.Error.Length, 500)];
            _log.LogWarning("Reporte de leads del {Day} no salió: {Error}", day, result.Error);
        }
        await db.SaveChangesAsync(ct);
    }
}
