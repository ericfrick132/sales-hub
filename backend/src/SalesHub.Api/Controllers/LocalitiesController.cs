using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SalesHub.Core.Domain.Entities;
using SalesHub.Core.Domain.Enums;
using SalesHub.Infrastructure.Persistence;

namespace SalesHub.Api.Controllers;

[ApiController]
[Route("api")]
[Authorize]
public class LocalitiesController : ControllerBase
{
    private readonly ApplicationDbContext _db;
    public LocalitiesController(ApplicationDbContext db) { _db = db; }

    public record LocalityDto(
        string Gid2,
        string Name,
        string AdminLevel1Name,
        string CountryCode,
        double Lat,
        double Lng);

    public record SellerWithLocalitiesDto(
        Guid SellerId,
        string DisplayName,
        string Color,
        string[] LocalityGid2s);

    public record AssignLocalitiesRequest(string[] LocalityGid2s);

    public record LocalityImportItem(
        string Gid2,
        string Name,
        string AdminLevel1Gid,
        string AdminLevel1Name,
        string CountryCode,
        string CountryName,
        double CentroidLat,
        double CentroidLng);

    public record BulkLocalityImportRequest(LocalityImportItem[] Items);

    public record BulkLocalityImportResult(int Inserted, int Updated);

    [HttpGet("localities")]
    public async Task<ActionResult<IEnumerable<LocalityDto>>> List(
        [FromQuery] string? country,
        [FromQuery] int limit = 50_000,
        CancellationToken ct = default)
    {
        var q = _db.Localities.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(country)) q = q.Where(l => l.CountryCode == country);
        var rows = await q
            .OrderBy(l => l.CountryCode).ThenBy(l => l.AdminLevel1Name).ThenBy(l => l.Name)
            .Take(Math.Min(limit, 100_000))
            .Select(l => new LocalityDto(l.Gid2, l.Name, l.AdminLevel1Name, l.CountryCode, l.CentroidLat, l.CentroidLng))
            .ToListAsync(ct);
        return rows;
    }

    /// <summary>Sellers with their assigned locality gid2s for the colored-by-seller map view.</summary>
    [HttpGet("sellers/with-localities")]
    public async Task<ActionResult<IEnumerable<SellerWithLocalitiesDto>>> SellersWithLocalities(CancellationToken ct)
    {
        if (!CurrentUser.IsAdmin(User)) return Forbid();
        var rows = await _db.Sellers.AsNoTracking()
            .Where(s => s.IsActive)
            .OrderBy(s => s.DisplayName)
            .Select(s => new
            {
                s.Id,
                s.SellerKey,
                s.DisplayName,
                Gid2s = s.LocalityAssignments.Select(a => a.LocalityGid2).ToArray()
            })
            .ToListAsync(ct);
        return rows
            .Select(r => new SellerWithLocalitiesDto(r.Id, r.DisplayName, ColorFor(r.SellerKey), r.Gid2s))
            .ToList();
    }

    [HttpGet("admin/sellers/{sellerId:guid}/localities")]
    public async Task<ActionResult<IEnumerable<LocalityDto>>> SellerLocalities(Guid sellerId, CancellationToken ct)
    {
        if (!CurrentUser.IsAdmin(User)) return Forbid();
        var rows = await _db.SellerLocalities.AsNoTracking()
            .Where(sl => sl.SellerId == sellerId)
            .Select(sl => sl.Locality!)
            .Select(l => new LocalityDto(l.Gid2, l.Name, l.AdminLevel1Name, l.CountryCode, l.CentroidLat, l.CentroidLng))
            .ToListAsync(ct);
        return rows;
    }

    /// <summary>Bulk-replace the locality assignments for a seller (M:N). The frontend
    /// sends the full desired set after a paint session and we diff against the current rows.</summary>
    [HttpPut("admin/sellers/{sellerId:guid}/localities")]
    public async Task<ActionResult<SellerWithLocalitiesDto>> AssignLocalities(
        Guid sellerId, [FromBody] AssignLocalitiesRequest req, CancellationToken ct)
    {
        if (!CurrentUser.IsAdmin(User)) return Forbid();
        var seller = await _db.Sellers.FirstOrDefaultAsync(s => s.Id == sellerId, ct);
        if (seller is null) return NotFound();

        var desired = (req.LocalityGid2s ?? Array.Empty<string>())
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Distinct()
            .ToHashSet();

        // Reject gid2s that don't exist in the localities table to prevent FK failures
        // and silent typos in the payload.
        if (desired.Count > 0)
        {
            var existing = await _db.Localities.AsNoTracking()
                .Where(l => desired.Contains(l.Gid2))
                .Select(l => l.Gid2)
                .ToListAsync(ct);
            var missing = desired.Except(existing).ToArray();
            if (missing.Length > 0)
                return BadRequest(new { error = $"Localities desconocidas: {string.Join(", ", missing.Take(5))}{(missing.Length > 5 ? "…" : "")}" });
        }

        var current = await _db.SellerLocalities
            .Where(sl => sl.SellerId == sellerId)
            .ToListAsync(ct);
        var currentSet = current.Select(sl => sl.LocalityGid2).ToHashSet();

        var toRemove = current.Where(sl => !desired.Contains(sl.LocalityGid2)).ToList();
        var toAdd = desired.Except(currentSet).ToList();

        _db.SellerLocalities.RemoveRange(toRemove);
        var now = DateTimeOffset.UtcNow;
        foreach (var gid2 in toAdd)
            _db.SellerLocalities.Add(new SellerLocality { SellerId = sellerId, LocalityGid2 = gid2, AssignedAt = now });
        await _db.SaveChangesAsync(ct);

        return new SellerWithLocalitiesDto(seller.Id, seller.DisplayName, ColorFor(seller.SellerKey), desired.ToArray());
    }

    [HttpPost("admin/localities/import")]
    public async Task<ActionResult<BulkLocalityImportResult>> Import(
        [FromBody] BulkLocalityImportRequest req, CancellationToken ct)
    {
        if (!CurrentUser.IsAdmin(User)) return Forbid();
        if (req.Items is null || req.Items.Length == 0) return BadRequest(new { error = "items vacío" });

        var ids = req.Items.Select(i => i.Gid2).ToHashSet();
        var existing = await _db.Localities
            .Where(l => ids.Contains(l.Gid2))
            .ToDictionaryAsync(l => l.Gid2, ct);

        var inserted = 0;
        var updated = 0;
        foreach (var item in req.Items)
        {
            if (string.IsNullOrWhiteSpace(item.Gid2)) continue;
            if (existing.TryGetValue(item.Gid2, out var loc))
            {
                loc.Name = item.Name;
                loc.AdminLevel1Gid = item.AdminLevel1Gid;
                loc.AdminLevel1Name = item.AdminLevel1Name;
                loc.CountryCode = item.CountryCode;
                loc.CountryName = item.CountryName;
                loc.CentroidLat = item.CentroidLat;
                loc.CentroidLng = item.CentroidLng;
                updated++;
            }
            else
            {
                _db.Localities.Add(new Locality
                {
                    Gid2 = item.Gid2,
                    Name = item.Name,
                    AdminLevel1Gid = item.AdminLevel1Gid,
                    AdminLevel1Name = item.AdminLevel1Name,
                    CountryCode = item.CountryCode,
                    CountryName = item.CountryName,
                    CentroidLat = item.CentroidLat,
                    CentroidLng = item.CentroidLng
                });
                inserted++;
            }
        }
        await _db.SaveChangesAsync(ct);
        return new BulkLocalityImportResult(inserted, updated);
    }

    // Stable per-seller color for the map. HSL with golden-ratio hue rotation
    // gives well-spaced, distinguishable colors for arbitrary keys.
    private static string ColorFor(string sellerKey)
    {
        unchecked
        {
            int hash = 17;
            foreach (var c in sellerKey) hash = hash * 31 + c;
            var hue = (Math.Abs(hash) * 0.618_033_988_75) % 1.0 * 360.0;
            return $"hsl({hue:F0} 70% 50%)";
        }
    }
}
