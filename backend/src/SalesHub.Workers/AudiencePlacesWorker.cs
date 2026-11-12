using Microsoft.EntityFrameworkCore;
using SalesHub.Infrastructure.Persistence;
using SalesHub.Infrastructure.Services;

namespace SalesHub.Workers;

/// <summary>
/// Último paso de la búsqueda de teléfonos de los públicos de la competencia: lo que la extensión no encontró
/// en Instagram, la web ni Google (FinderStatus "google") se busca solo en Google Places, de 9 a 22 (Argentina),
/// con tope diario de búsquedas (Audience:PlacesDailyMax, 100 ≈ USD 3 por día, Eric 22/10). Antes lo cruza
/// gratis contra la tabla leads. El botón "Buscar N" de Públicos sigue andando aparte, sin este tope.
/// Los listados de Audience:PlacesDirectDatasets (separados por coma; por defecto "gymhero-wellhub") van directo
/// a Google sin esperar a la extensión, primero y dentro del mismo tope: son los que ya llegan con el teléfono
/// de la ficha del competidor y solo les falta el de unos pocos (Eric 12/11).
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
        var svc = scope.ServiceProvider.GetRequiredService<AudienceLeadService>();
        int pending = 0, fromLeads = 0, fromGoogle = 0, searched = 0;
        var direct = (_config["Audience:PlacesDirectDatasets"] ?? "gymhero-wellhub")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var dataset in direct)
        {
            if (searched >= n) break;
            var d = await svc.PlacesAsync(dataset, n - searched, onlyFinderDone: false, ct: ct, take: n - searched);
            pending += d.Pending; fromLeads += d.FromLeads; fromGoogle += d.FromGoogle; searched += d.Searched;
        }
        if (searched < n)
        {
            var r = await svc.PlacesAsync(null, n - searched, ct: ct, take: n - searched);
            pending += r.Pending; fromLeads += r.FromLeads; fromGoogle += r.FromGoogle; searched += r.Searched;
        }
        if (pending > 0)
            _log.LogInformation("Places automático: {Leads} desde leads, {Google} desde Google ({Searched} búsquedas, {Used}/{Max} hoy)",
                fromLeads, fromGoogle, searched, used + searched, dailyMax);
    }
}
