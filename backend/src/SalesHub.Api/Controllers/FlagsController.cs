using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SalesHub.Core.Domain.Entities;
using SalesHub.Infrastructure.Persistence;

namespace SalesHub.Api.Controllers;

/// <summary>
/// Switches on/off de los runners autónomos (posteos, captación, SEO) persistidos en DB,
/// para prenderlos/apagarlos desde la UI sin tocar env vars ni reiniciar. Si no hay fila,
/// el worker cae al valor de config (Workers:*AutoStart / Seo:AutoStart).
/// </summary>
[ApiController]
[Route("api/flags")]
[Authorize(Roles = "Admin")]
public class FlagsController : ControllerBase
{
    private readonly ApplicationDbContext _db;
    private readonly IConfiguration _config;
    public FlagsController(ApplicationDbContext db, IConfiguration config) { _db = db; _config = config; }

    private static readonly (string Key, string ConfigKey, string Label)[] Defs =
    {
        ("posteos", "Workers:PosteosAutoStart", "Posteos automáticos"),
        ("captacion", "Workers:PipelineAutoStart", "Captación de leads"),
        ("seo", "Seo:AutoStart", "Artículos SEO"),
        ("reengage", "LeadImport:AutoStart", "Re-engagement (TurnosPro/GymHero)"),
    };

    public record FlagDto(string Key, string Label, bool Enabled);
    public record SetFlagRequest(bool Enabled);

    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken ct)
    {
        var map = (await _db.RuntimeFlags.AsNoTracking().ToListAsync(ct)).ToDictionary(r => r.Key, r => r.Enabled);
        var result = Defs.Select(d => new FlagDto(d.Key, d.Label,
            map.TryGetValue(d.Key, out var v) ? v : _config.GetValue<bool>(d.ConfigKey, true)));
        return Ok(result);
    }

    [HttpPost("{key}")]
    public async Task<IActionResult> Set(string key, [FromBody] SetFlagRequest req, CancellationToken ct)
    {
        var def = Defs.FirstOrDefault(d => d.Key == key);
        if (def.Key is null) return BadRequest(new { error = "Flag desconocido" });

        var row = await _db.RuntimeFlags.FirstOrDefaultAsync(f => f.Key == key, ct);
        if (row is null) { row = new RuntimeFlag { Key = key }; _db.RuntimeFlags.Add(row); }
        row.Enabled = req.Enabled;
        row.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);
        return Ok(new FlagDto(key, def.Label, row.Enabled));
    }
}
