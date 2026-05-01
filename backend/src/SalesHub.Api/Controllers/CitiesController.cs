using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SalesHub.Api.Dtos;
using SalesHub.Core.Domain.Entities;
using SalesHub.Core.Domain.Enums;
using SalesHub.Infrastructure.Persistence;

namespace SalesHub.Api.Controllers;

[ApiController]
[Route("api/cities")]
[Authorize]
public class CitiesController : ControllerBase
{
    private const int CooldownDays = 30;
    private readonly ApplicationDbContext _db;

    public CitiesController(ApplicationDbContext db) { _db = db; }

    public record CityMapPin(Guid Id, string Country, string Province, string City,
        PopulationBucket Bucket, double? Latitude, double? Longitude);

    /// <summary>Lista compacta para el mapa de zonas (admin → /sellers/.../zones).</summary>
    [HttpGet("map")]
    public async Task<ActionResult<IEnumerable<CityMapPin>>> Map(
        [FromQuery] string? country, CancellationToken ct = default)
    {
        var cities = await _db.Cities.AsNoTracking()
            .Where(c => (country == null || c.Country == country)
                     && c.Latitude != null && c.Longitude != null)
            .OrderBy(c => c.Province).ThenBy(c => c.City)
            .Select(c => new CityMapPin(c.Id, c.Country, c.Province, c.City,
                c.PopulationBucket, c.Latitude, c.Longitude))
            .ToListAsync(ct);
        return cities;
    }

    /// <summary>Returns the city catalog enriched with per-product scrape status.</summary>
    [HttpGet]
    public async Task<ActionResult<IEnumerable<CityDto>>> List(
        [FromQuery] string? country, [FromQuery] string? productKey,
        [FromQuery] LeadSource? source, CancellationToken ct = default)
    {
        var cities = await _db.Cities
            .Where(c => country == null || c.Country == country)
            .OrderBy(c => c.Country).ThenBy(c => c.Province).ThenBy(c => c.City)
            .ToListAsync(ct);
        if (cities.Count == 0) return new List<CityDto>();

        var cityNames = cities.Select(c => c.City).ToList();
        var logs = await (from l in _db.ScrapeLogs
                          where (productKey == null || l.ProductKey == productKey)
                             && (source == null || l.Source == source)
                             && cityNames.Contains(l.City!)
                          group l by l.City into g
                          select new
                          {
                              City = g.Key!,
                              LastRunAt = g.Max(x => x.RunAt),
                              LastResults = g.OrderByDescending(x => x.RunAt).First().ResultsCount
                          }).ToListAsync(ct);
        var logMap = logs.ToDictionary(x => x.City, x => (x.LastRunAt, x.LastResults));

        var leadCounts = productKey is null
            ? new Dictionary<string, int>()
            : await _db.Leads.Where(l => l.ProductKey == productKey && l.City != null)
                .GroupBy(l => l.City!)
                .Select(g => new { City = g.Key, N = g.Count() })
                .ToDictionaryAsync(x => x.City, x => x.N, ct);

        var now = DateTimeOffset.UtcNow;
        return cities.Select(c =>
        {
            var (last, lastResults) = logMap.TryGetValue(c.City, out var entry)
                ? (entry.LastRunAt, (int?)entry.LastResults)
                : ((DateTimeOffset?)null, (int?)null);
            var days = last is null ? int.MaxValue : (int)(now - last.Value).TotalDays;
            var cooldown = last is not null && days < CooldownDays;
            var leadsForProduct = leadCounts.TryGetValue(c.City, out var n) ? n : 0;
            return new CityDto(c.Id, c.Country, c.Province, c.City, c.PopulationBucket,
                last, days == int.MaxValue ? -1 : days, leadsForProduct, cooldown, lastResults);
        }).ToList();
    }

    /// <summary>
    /// Sweep: returns up to N cities in "smart sweep" order — bucket desc, then
    /// latitude ascending (sur a norte dentro de cada bucket). Skips cities in cooldown.
    /// Use this to generate a queue of runs that systematically covers the country.
    /// </summary>
    [HttpGet("sweep")]
    public async Task<ActionResult<IEnumerable<SuggestedCityDto>>> Sweep(
        [FromQuery] string productKey, [FromQuery] LeadSource? source,
        [FromQuery] int limit = 30, [FromQuery] string? country = null,
        [FromQuery] string? province = null, [FromQuery] string? bucketsCsv = null,
        CancellationToken ct = default)
    {
        var buckets = ParseBuckets(bucketsCsv);
        var product = await _db.Products.FirstOrDefaultAsync(p => p.ProductKey == productKey, ct);
        if (product is null) return NotFound();

        var cities = await _db.Cities
            .Where(c => c.Country == (country ?? product.Country))
            .Where(c => province == null || c.Province == province)
            .Where(c => buckets == null || buckets.Contains(c.PopulationBucket))
            .ToListAsync(ct);

        var cityNames = cities.Select(c => c.City).ToList();
        var cooldown = DateTimeOffset.UtcNow.AddDays(-CooldownDays);
        var emptyCooldown = DateTimeOffset.UtcNow.AddDays(-90);
        var logs = await _db.ScrapeLogs
            .Where(l => l.ProductKey == productKey
                     && (source == null || l.Source == source)
                     && cityNames.Contains(l.City!))
            .GroupBy(l => l.City!)
            .Select(g => new
            {
                City = g.Key,
                LastRunAt = g.Max(x => x.RunAt),
                LastStatus = g.OrderByDescending(x => x.RunAt).First().Status
            })
            .ToListAsync(ct);
        var logMap = logs.ToDictionary(x => x.City, x => (x.LastRunAt, x.LastStatus));

        var result = cities
            .Where(c =>
            {
                if (!logMap.TryGetValue(c.City, out var entry)) return true;
                var (lastRun, lastStatus) = entry;
                if (lastStatus == "empty" && lastRun > emptyCooldown) return false;
                if (lastStatus == "done" && lastRun > cooldown) return false;
                return true;
            })
            .OrderByDescending(c => (int)c.PopulationBucket) // Mega/Big primero
            .ThenBy(c => c.Latitude ?? 0)                     // dentro del bucket, sur a norte
            .Take(limit)
            .Select(c => new SuggestedCityDto(c.Id, c.Country, c.Province, c.City,
                c.PopulationBucket, (int)c.PopulationBucket * 1000,
                $"{c.PopulationBucket} · lat {c.Latitude?.ToString("F2") ?? "?"} · pop {c.Population?.ToString("N0") ?? "?"}"))
            .ToList();

        return result;
    }

    /// <summary>
    /// Returns top-N cities the next pipeline run should target, priorized by
    /// population bucket and inverse recency for the selected product/source.
    /// </summary>
    [HttpGet("suggested")]
    public async Task<ActionResult<IEnumerable<SuggestedCityDto>>> Suggested(
        [FromQuery] string productKey, [FromQuery] LeadSource? source,
        [FromQuery] int limit = 10, [FromQuery] string? bucketsCsv = null,
        [FromQuery] string? country = null, CancellationToken ct = default)
    {
        var buckets = ParseBuckets(bucketsCsv);
        var product = await _db.Products.FirstOrDefaultAsync(p => p.ProductKey == productKey, ct);
        if (product is null) return NotFound();

        var cities = await _db.Cities
            .Where(c => c.Country == (country ?? product.Country))
            .Where(c => buckets == null || buckets.Contains(c.PopulationBucket))
            .ToListAsync(ct);

        var cityNames = cities.Select(c => c.City).ToList();
        var cooldown = DateTimeOffset.UtcNow.AddDays(-CooldownDays);
        var emptyCooldown = DateTimeOffset.UtcNow.AddDays(-90);
        var logs = await _db.ScrapeLogs
            .Where(l => l.ProductKey == productKey
                     && (source == null || l.Source == source)
                     && cityNames.Contains(l.City!)
                     && l.RunAt >= emptyCooldown)
            .GroupBy(l => l.City!)
            .Select(g => new
            {
                City = g.Key,
                LastRunAt = g.Max(x => x.RunAt),
                LastStatus = g.OrderByDescending(x => x.RunAt).First().Status
            })
            .ToListAsync(ct);
        var logMap = logs.ToDictionary(x => x.City, x => (x.LastRunAt, x.LastStatus));

        var now = DateTimeOffset.UtcNow;
        var ranked = cities
            .Select(c =>
            {
                logMap.TryGetValue(c.City, out var entry);
                var (lastRun, lastStatus) = entry;
                bool hardBlock = lastRun != default
                    && ((lastStatus == "empty" && lastRun > emptyCooldown)
                        || (lastStatus == "done" && lastRun > cooldown));
                var daysSince = lastRun == default ? 999 : (int)(now - lastRun).TotalDays;
                var score = (int)c.PopulationBucket * 100 + Math.Min(daysSince, 180);
                var reason = hardBlock
                    ? $"En cooldown hasta {(lastStatus == "empty" ? lastRun.AddDays(90) : lastRun.AddDays(CooldownDays)):dd MMM}"
                    : lastRun == default
                        ? $"{c.PopulationBucket} · nunca scrapeada"
                        : $"{c.PopulationBucket} · hace {daysSince}d ({lastStatus})";
                return (City: c, Score: score, Blocked: hardBlock, Reason: reason);
            })
            .Where(x => !x.Blocked)
            .OrderByDescending(x => x.Score)
            .Take(limit)
            .Select(x => new SuggestedCityDto(x.City.Id, x.City.Country, x.City.Province,
                x.City.City, x.City.PopulationBucket, x.Score, x.Reason))
            .ToList();

        return ranked;
    }

    [HttpPost]
    public async Task<ActionResult<CityQueue>> Create([FromBody] CreateCityRequest req, CancellationToken ct)
    {
        if (!CurrentUser.IsAdmin(User)) return Forbid();
        var exists = await _db.Cities.AnyAsync(c =>
            c.Country == req.Country && c.Province == req.Province && c.City == req.City, ct);
        if (exists) return Conflict(new { error = "Ciudad ya existe" });
        var city = new CityQueue
        {
            Id = Guid.NewGuid(),
            Country = req.Country,
            Province = req.Province,
            City = req.City,
            PopulationBucket = req.Bucket
        };
        _db.Cities.Add(city);
        await _db.SaveChangesAsync(ct);
        return city;
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        if (!CurrentUser.IsAdmin(User)) return Forbid();
        var city = await _db.Cities.FirstOrDefaultAsync(c => c.Id == id, ct);
        if (city is null) return NotFound();
        _db.Cities.Remove(city);
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }

    private static HashSet<PopulationBucket>? ParseBuckets(string? csv)
    {
        if (string.IsNullOrWhiteSpace(csv)) return null;
        var set = new HashSet<PopulationBucket>();
        foreach (var chunk in csv.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (Enum.TryParse<PopulationBucket>(chunk, ignoreCase: true, out var b)) set.Add(b);
        }
        return set.Count == 0 ? null : set;
    }
}
