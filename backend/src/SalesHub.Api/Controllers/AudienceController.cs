using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using SalesHub.Infrastructure.Options;
using SalesHub.Infrastructure.Services;

namespace SalesHub.Api.Controllers;

/// <summary>
/// Públicos de competencia (Marketing → Competencia → Públicos): listados de clientes de la competencia
/// para públicos de Meta, guardados en <c>audience_leads</c>. Nunca se contactan. La extensión
/// "Buscar WhatsApp" manda cada resultado a <c>finder-results</c> con la clave de <c>ingest-key</c>.
/// </summary>
[ApiController]
[Route("api/audience")]
[Authorize(Roles = "Admin")]
public class AudienceController : ControllerBase
{
    public const string KeyHeader = "X-Audience-Key";

    private readonly AudienceLeadService _svc;
    private readonly IServiceScopeFactory _scopes;
    private readonly JwtOptions _jwt;

    public AudienceController(AudienceLeadService svc, IServiceScopeFactory scopes, IOptions<JwtOptions> jwt)
    {
        _svc = svc; _scopes = scopes; _jwt = jwt.Value;
    }

    /// <summary>Avance por app, competidor, dataset y país: total, con teléfono, WhatsApp y de dónde salió.</summary>
    [HttpGet("summary")]
    public async Task<IActionResult> Summary(CancellationToken ct) => Ok(await _svc.SummaryAsync(ct));

    /// <summary>Últimos números conseguidos (extensión, leads o Google).</summary>
    [HttpGet("recent")]
    public async Task<IActionResult> Recent([FromQuery] int limit = 50, CancellationToken ct = default) =>
        Ok(await _svc.RecentAsync(Math.Clamp(limit, 1, 200), ct));

    /// <summary>Últimos movimientos: cada paso de la búsqueda (Instagram, web, leads, Google) con su fecha.</summary>
    [HttpGet("events")]
    public async Task<IActionResult> Events([FromQuery] int limit = 60, CancellationToken ct = default) =>
        Ok(await _svc.EventsAsync(Math.Clamp(limit, 1, 300), ct));

    /// <summary>Clave para pegar en la extensión.</summary>
    [HttpGet("ingest-key")]
    public IActionResult Key() => Ok(new { key = AudienceLeadService.IngestKey(_jwt.SigningKey) });

    /// <summary>Carga el JSON de audience_export.py (también .json.gz). Upsert por dataset + id.</summary>
    [HttpPost("import")]
    [RequestSizeLimit(200_000_000)]
    [RequestFormLimits(MultipartBodyLengthLimit = 200_000_000)]
    public async Task<IActionResult> Import(IFormFile file, CancellationToken ct)
    {
        await using var raw = file.OpenReadStream();
        Stream s = file.FileName.EndsWith(".gz", StringComparison.OrdinalIgnoreCase) ? new GZipStream(raw, CompressionMode.Decompress) : raw;
        List<AudienceRow>? rows;
        try
        {
            rows = await JsonSerializer.DeserializeAsync<List<AudienceRow>>(s,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true }, ct);
        }
        catch (JsonException ex)
        {
            return BadRequest(new { error = $"JSON inválido: {ex.Message}" });
        }
        rows = rows?.Where(r => !string.IsNullOrWhiteSpace(r.Dataset) && !string.IsNullOrWhiteSpace(r.ExternalId)
                                && !string.IsNullOrWhiteSpace(r.Name)).ToList() ?? new();
        var (inserted, updated) = await _svc.ImportAsync(rows, ct);
        return Ok(new { rows = rows.Count, inserted, updated });
    }

    public record PlacesRequest(string? Dataset, int Max = 100);

    /// <summary>
    /// Busca en leads y Google Places lo que la extensión no encontró. Corre en segundo plano
    /// (Google tarda ~1 s por negocio); el avance se ve en summary.
    /// </summary>
    [HttpPost("places")]
    public IActionResult Places([FromBody] PlacesRequest req)
    {
        var max = Math.Clamp(req.Max, 0, 2000);
        _ = Task.Run(async () =>
        {
            using var scope = _scopes.CreateScope();
            var svc = scope.ServiceProvider.GetRequiredService<AudienceLeadService>();
            var log = scope.ServiceProvider.GetRequiredService<ILogger<AudienceController>>();
            try { await svc.PlacesAsync(string.IsNullOrWhiteSpace(req.Dataset) ? null : req.Dataset, max); }
            catch (Exception ex) { log.LogError(ex, "audience places falló"); }
        });
        return Accepted(new { started = true, max });
    }

    public record FinderQueueRequest(int Limit = 30, List<string>? Skip = null,
        List<string>? ExcludeProducts = null, List<string>? OnlyProducts = null);

    /// <summary>
    /// Próximos negocios para la extensión (sin login: header X-Audience-Key). POST para poder mandar
    /// las claves que ya tiene en cola sin pasar el largo de una URL.
    /// </summary>
    [HttpPost("finder-queue")]
    [AllowAnonymous]
    public async Task<IActionResult> FinderQueue([FromBody] FinderQueueRequest req, CancellationToken ct)
    {
        if (!KeyOk()) return Unauthorized();
        var skip = (req.Skip ?? new()).Take(5000).ToList();
        return Ok(await _svc.FinderQueueAsync(Math.Clamp(req.Limit, 1, 100), skip, req.ExcludeProducts, req.OnlyProducts, ct));
    }

    /// <summary>Resultados de la extensión "Buscar WhatsApp" (sin login: header X-Audience-Key).</summary>
    [HttpPost("finder-results")]
    [AllowAnonymous]
    public async Task<IActionResult> FinderResults([FromBody] List<FinderResult> results, CancellationToken ct)
    {
        if (!KeyOk()) return Unauthorized();
        if (results.Count > 500) return BadRequest(new { error = "máximo 500 por envío" });
        return Ok(new { applied = await _svc.ApplyFinderResultsAsync(results, ct) });
    }

    private bool KeyOk()
    {
        var expected = Encoding.UTF8.GetBytes(AudienceLeadService.IngestKey(_jwt.SigningKey));
        var given = Encoding.UTF8.GetBytes(Request.Headers[KeyHeader].ToString());
        return given.Length == expected.Length && CryptographicOperations.FixedTimeEquals(given, expected);
    }
}
