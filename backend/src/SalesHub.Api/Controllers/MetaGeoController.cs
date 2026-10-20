using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SalesHub.Infrastructure.Services;

namespace SalesHub.Api.Controllers;

/// <summary>
/// Mapa de anuncios por provincia (Marketing → Mapa de anuncios): rendimiento de los anuncios de Meta
/// por provincia (ver <see cref="MetaGeoSyncService"/>) cruzado con los leads de sales-hub que
/// vinieron de anuncios (provincia por código de área del celular).
/// </summary>
[ApiController]
[Route("api/meta-geo")]
[Authorize(Roles = "Admin")]
public class MetaGeoController : ControllerBase
{
    private readonly MetaGeoSyncService _sync;
    private readonly MetaGeoReportService _report;

    public MetaGeoController(MetaGeoSyncService sync, MetaGeoReportService report) { _sync = sync; _report = report; }

    /// <summary>Opciones de los filtros: apps, campañas, anuncios (con formato inferido), provincias y fechas con datos.</summary>
    [HttpGet("filters")]
    public async Task<IActionResult> Filters(CancellationToken ct) => Ok(await _report.FiltersAsync(ct));

    /// <summary>
    /// KPIs, serie diaria, provincias, anuncios y formatos. Listas repetibles:
    /// ?from=2026-09-17&amp;to=2026-10-16&amp;product=gymhero&amp;product=turnospro&amp;campaignId=…&amp;adId=…&amp;format=pov&amp;region=Córdoba&amp;country=MX
    /// </summary>
    [HttpGet("summary")]
    public async Task<IActionResult> Summary(
        [FromQuery] DateOnly? from, [FromQuery] DateOnly? to,
        [FromQuery(Name = "product")] List<string>? products,
        [FromQuery(Name = "campaignId")] List<string>? campaignIds,
        [FromQuery(Name = "adId")] List<string>? adIds,
        [FromQuery(Name = "format")] List<string>? formats,
        [FromQuery(Name = "region")] List<string>? regions,
        [FromQuery(Name = "country")] List<string>? countries,
        CancellationToken ct)
    {
        var q = new MetaGeoQuery(from, to, Clean(products), Clean(campaignIds), Clean(adIds), Clean(formats), Clean(regions),
            Clean(countries).Select(c => c.ToUpperInvariant()).ToList());
        return Ok(await _report.SummaryAsync(q, ct));
    }

    /// <summary>
    /// Tabla dinámica: agrupa por 1 o 2 dimensiones (?dim=adset&amp;dim=weekday; ver
    /// <see cref="MetaGeoReportService.TableDims"/>) con los mismos filtros que /summary.
    /// </summary>
    [HttpGet("table")]
    public async Task<IActionResult> Table(
        [FromQuery] DateOnly? from, [FromQuery] DateOnly? to,
        [FromQuery(Name = "dim")] List<string>? dims,
        [FromQuery(Name = "product")] List<string>? products,
        [FromQuery(Name = "campaignId")] List<string>? campaignIds,
        [FromQuery(Name = "adId")] List<string>? adIds,
        [FromQuery(Name = "format")] List<string>? formats,
        [FromQuery(Name = "region")] List<string>? regions,
        [FromQuery(Name = "country")] List<string>? countries,
        CancellationToken ct)
    {
        var q = new MetaGeoQuery(from, to, Clean(products), Clean(campaignIds), Clean(adIds), Clean(formats), Clean(regions),
            Clean(countries).Select(c => c.ToUpperInvariant()).ToList());
        return Ok(await _report.TableAsync(q, Clean(dims), ct));
    }

    private static List<string> Clean(List<string>? xs) => (xs ?? new())
        .SelectMany(x => x.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        .Distinct().ToList();

    /// <summary>Fuerza la sync. Sin days: últimos 3 días (90 si una cuenta no tiene filas). days=90 rehace el backfill.</summary>
    [HttpPost("sync")]
    public async Task<IActionResult> Sync([FromQuery] int? days, CancellationToken ct)
    {
        if (!_sync.IsConfigured) return BadRequest(new { error = "Falta Meta:AdsAccessToken en el servidor." });
        var r = await _sync.SyncAsync(days, ct);
        return Ok(new { accounts = r.Accounts, rows = r.Rows, removed = r.Removed, from = r.From, to = r.To });
    }
}
