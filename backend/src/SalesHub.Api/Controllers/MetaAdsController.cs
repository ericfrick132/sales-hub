using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SalesHub.Infrastructure.Persistence;
using SalesHub.Infrastructure.Services;

namespace SalesHub.Api.Controllers;

/// <summary>
/// Anuncios de Meta sincronizados (ver <see cref="MetaAdsSyncService"/>): qué mensaje pre-armado deja
/// cada uno, a qué número apunta, con qué rubro y ángulo lo lee el bot, y cuántos leads trajo.
/// </summary>
[ApiController]
[Route("api/meta-ads")]
[Authorize(Roles = "Admin")]
public class MetaAdsController : ControllerBase
{
    private readonly ApplicationDbContext _db;
    private readonly MetaAdsSyncService _sync;
    private readonly MetaAdMediaService _media;

    public MetaAdsController(ApplicationDbContext db, MetaAdsSyncService sync, MetaAdMediaService media) { _db = db; _sync = sync; _media = media; }

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        var leadCounts = await _db.Leads.AsNoTracking().Where(l => l.AdId != null)
            .GroupBy(l => l.AdId!).Select(g => new { g.Key, C = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.C, ct);
        var lines = await _db.EvolutionInstances.AsNoTracking()
            .Where(i => i.ConnectedPhoneNumber != null)
            .Select(i => new { i.ConnectedPhoneNumber, i.InstanceName, i.Status, i.ListenOnly, i.SellerId })
            .ToListAsync(ct);
        var ads = await _db.MetaAds.AsNoTracking()
            .OrderByDescending(a => a.Status == "ACTIVE").ThenBy(a => a.ProductKey).ThenBy(a => a.Rubro).ThenBy(a => a.Angle)
            .ToListAsync(ct);
        return Ok(new
        {
            configured = _sync.IsConfigured,
            lastSyncAt = ads.Count == 0 ? (DateTimeOffset?)null : ads.Max(a => a.SyncedAt),
            ads = ads.Select(a =>
            {
                // La línea que recibe: preferimos una conectada (puede haber instancias viejas con el mismo número).
                var line = lines.Where(l => l.ConnectedPhoneNumber == a.DestinationPhone)
                    .OrderByDescending(l => l.Status == Core.Domain.Enums.InstanceStatus.Connected).FirstOrDefault();
                return new
                {
                    a.AdId, a.Name, a.Status, a.ProductKey, a.Rubro, a.Angle, a.BusinessRef, a.PrefillText,
                    a.Title, a.Body, a.DestinationPhone, a.AdCreatedAt, a.VideoId,
                    lineInstance = line?.InstanceName,
                    lineStatus = line?.Status.ToString(),
                    lineListenOnly = line?.ListenOnly ?? false,
                    leads = leadCounts.TryGetValue(a.AdId, out var c) ? c : 0,
                };
            }),
        });
    }

    /// <summary>Miniatura de cada anuncio (?adId=…&amp;adId=…, hasta 300): { adId: url }. Las URLs de Meta vencen: no guardarlas.</summary>
    [HttpGet("media")]
    public async Task<IActionResult> Media([FromQuery(Name = "adId")] List<string>? adIds, CancellationToken ct)
    {
        var ids = (adIds ?? new()).SelectMany(x => x.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Distinct().Take(300).ToList();
        return Ok(await _media.ThumbnailsAsync(ids, ct));
    }

    /// <summary>URL para reproducir el video del anuncio (vence en horas: se pide al abrirlo).</summary>
    [HttpGet("{adId}/video")]
    public async Task<IActionResult> Video(string adId, CancellationToken ct)
    {
        var url = await _media.VideoUrlAsync(adId, ct);
        return url is null ? NotFound(new { error = "Este anuncio no tiene video o Meta no lo devolvió." }) : Ok(new { url });
    }

    [HttpPost("sync")]
    public async Task<IActionResult> Sync(CancellationToken ct)
    {
        if (!_sync.IsConfigured) return BadRequest(new { error = "Falta Meta:AdsAccessToken en el servidor." });
        var n = await _sync.SyncAsync(ct);
        return Ok(new { synced = n });
    }
}
