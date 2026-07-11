using Microsoft.EntityFrameworkCore;
using SalesHub.Infrastructure.Persistence;
using SalesHub.Infrastructure.Services;

namespace SalesHub.Workers;

/// <summary>
/// Resumen diario por WhatsApp: cuando pasa la hora configurada (Argentina) y todavía
/// no se mandó hoy, compone y manda el digest del día al número configurado.
/// Config y on/off en DigestSettings (UI en /digest). El dedup por día vive en
/// DigestSettings.LastSentAt, así un reinicio no lo re-manda; y si el contenedor
/// estuvo caído a esa hora, el próximo tick del día lo recupera.
/// </summary>
public class DailyDigestWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<DailyDigestWorker> _log;

    public DailyDigestWorker(IServiceScopeFactory scopes, ILogger<DailyDigestWorker> log)
    {
        _scopes = scopes; _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _log.LogInformation("DailyDigestWorker started (config en digest_settings, hora AR)");
        await Task.Delay(TimeSpan.FromMinutes(2), stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await TickAsync(stoppingToken); }
            catch (Exception ex) { _log.LogError(ex, "DailyDigestWorker tick failed"); }
            await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);
        }
    }

    private async Task TickAsync(CancellationToken ct)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var s = await db.DigestSettings.AsNoTracking().FirstOrDefaultAsync(ct);
        if (s is null || !s.Enabled || string.IsNullOrWhiteSpace(s.InstanceName)) return;

        var nowAr = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, DailyDigestService.ArTz);
        if (nowAr.Hour < Math.Clamp(s.SendHour, 0, 23)) return;
        if (s.LastSentAt is { } last && TimeZoneInfo.ConvertTime(last, DailyDigestService.ArTz).Date == nowAr.Date) return;

        var digest = scope.ServiceProvider.GetRequiredService<DailyDigestService>();
        var res = await digest.SendAsync(markSent: true, ct);
        if (res.Sent) _log.LogInformation("Resumen diario enviado a {Phone} por {Instance}", res.Phone, res.Instance);
        else _log.LogWarning("Resumen diario no enviado: {Error}", res.Error);
    }
}
