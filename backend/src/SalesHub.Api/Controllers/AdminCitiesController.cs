using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SalesHub.Infrastructure.Persistence;
using SalesHub.Infrastructure.Services;

namespace SalesHub.Api.Controllers;

[ApiController]
[Route("api/admin/cities")]
[Authorize]
public class AdminCitiesController : ControllerBase
{
    private readonly GeonamesImporter _importer;
    private readonly ApplicationDbContext _db;
    private readonly ILogger<AdminCitiesController> _log;

    public AdminCitiesController(GeonamesImporter importer, ApplicationDbContext db, ILogger<AdminCitiesController> log)
    {
        _importer = importer; _db = db; _log = log;
    }

    public record ImportRequest(string Country, int MinPopulation = 500);

    /// <summary>Run a GeoNames country import synchronously. Can take a minute.</summary>
    [HttpPost("import")]
    public async Task<IActionResult> Import([FromBody] ImportRequest req, CancellationToken ct)
    {
        if (!CurrentUser.IsAdmin(User)) return Forbid();
        try
        {
            var r = await _importer.ImportAsync(req.Country, req.MinPopulation, ct);
            return Ok(new { r.Inserted, r.Updated, r.Skipped });
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "GeoNames import failed");
            return StatusCode(500, new { error = ex.Message });
        }
    }

    /// <summary>Available admin zones (provinces) for seller assignment UI.</summary>
    [HttpGet("/api/admin/zones")]
    public async Task<IActionResult> Zones([FromQuery] string? country, CancellationToken ct)
    {
        var q = _db.Cities.AsQueryable();
        if (!string.IsNullOrWhiteSpace(country)) q = q.Where(c => c.Country == country);
        var zones = await q.Select(c => new { c.Country, c.Province })
            .Distinct()
            .OrderBy(x => x.Country).ThenBy(x => x.Province)
            .ToListAsync(ct);
        return Ok(zones);
    }
}
