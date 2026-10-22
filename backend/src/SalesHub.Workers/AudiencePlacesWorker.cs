using Microsoft.EntityFrameworkCore;
using SalesHub.Infrastructure.Persistence;
using SalesHub.Infrastructure.Services;

namespace SalesHub.Workers;

/// <summary>
/// Último paso de la búsqueda de teléfonos de los públicos de la competencia: lo que la extensión no encontró
/// en Instagram, la web ni Google (FinderStatus "google") se busca solo en Google Places, de 9 a 22 (Argentina),
/// con tope diario de búsquedas (Audience:PlacesDailyMax, 100 ≈ USD 3 por día, Eric 22/10). Antes lo cruza
/// gratis contra la tabla leads. El botón "Buscar N" de Públicos sigue andando aparte, sin este tope.
/// </summary>
public class AudiencePlacesWorker : BackgroundService
{
    private static readonly TimeSpan Every = TimeSpan.FromMinutes(30);

    private readonly IServiceScopeFactory _scopes;
    private readonly IConfiguration _config;
    private readonly ILogger<AudiencePlacesWorker> _log;

    public AudiencePlacesWorker(IServiceScopeFactory scopes, IConfiguration config, ILogger<AudiencePlacesWorker> log)
    {
        _scopes = scopes; _config = config; _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        await Task.Delay(TimeSpan.FromMinutes(4), ct);
        while (!ct.IsCancellationRequested)
        {
            try { await TickAsync(ct); }
            catch (Exception ex) { _log.LogError(ex, "AudiencePlacesWorker tick failed"); }
            await Task.Delay(Every, ct);
        }
    }

    private async Task TickAsync(CancellationToken ct)
    {
        var dailyMax = _config.GetValue<int?>("Audience:PlacesDailyMax") ?? 100;
        if (dailyMax <= 0) return;
        var tz = BotLearningService.ArTz;
        var nowAr = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, tz);
        if (nowAr.Hour < 9 || nowAr.Hour >= 22) return;
        var dayStart = new DateTimeOffset(nowAr.Date, nowAr.Offset);

        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var used = await db.AudienceLeads.CountAsync(a => a.PlacesCheckedAt >= dayStart, ct);
        var left = dailyMax - used;
        if (left <= 0) return;
        // De a 25 por vuelta: reparte las búsquedas en el día y no frena otras cosas.
        var n = Math.Min(left, 25);
        var (pending, fromLeads, fromGoogle, searched) = await scope.ServiceProvider.GetRequiredService<AudienceLeadService>()
            .PlacesAsync(null, n, ct: ct, take: n);
        if (pending > 0)
            _log.LogInformation("Places automático: {Leads} desde leads, {Google} desde Google ({Searched} búsquedas, {Used}/{Max} hoy)",
                fromLeads, fromGoogle, searched, used + searched, dailyMax);
    }
}
