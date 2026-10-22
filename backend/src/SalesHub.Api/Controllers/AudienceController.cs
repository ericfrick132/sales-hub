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

    /// <summary>Por día (hora argentina) y app: procesados, con número, follows y motivos de los que no salieron.</summary>
    [HttpGet("finder-days")]
    public async Task<IActionResult> FinderDays([FromQuery] int days = 14, CancellationToken ct = default) =>
        Ok(await _svc.FinderDaysAsync(Math.Clamp(days, 1, 90), ct));

    /// <summary>Batches de la extensión con su avance.</summary>
    [HttpGet("batches")]
    public async Task<IActionResult> Batches(CancellationToken ct) => Ok(await _svc.BatchesAsync(30, null, ct));

    /// <summary>Detalle de un batch: cada negocio con lo que se buscó, lo que hizo la extensión y el resultado.</summary>
    [HttpGet("batches/{id:guid}/leads")]
    public async Task<IActionResult> BatchLeads(Guid id, CancellationToken ct) => Ok(await _svc.BatchLeadsAsync(id, ct));

    public record CreateBatchRequest(string ProductKey, string? Dataset, int Size = 50, string? Note = null, bool IncludeFailed = false);

    /// <summary>Arma un batch con N pendientes de la app (y del listado, si se elige).</summary>
    [HttpPost("batches")]
    public async Task<IActionResult> CreateBatch([FromBody] CreateBatchRequest req, CancellationToken ct)
    {
        var b = await _svc.CreateBatchAsync(req.ProductKey, req.Dataset, Math.Clamp(req.Size, 1, 2000), req.Note, req.IncludeFailed, ct);
        return b is null ? BadRequest(new { error = "No hay pendientes para esa app/listado." }) : Ok(b);
    }

    public record ResetNoMatchRequest(string ProductKey, DateTimeOffset Since);

    /// <summary>Vuelve a pendientes los "no lo encontró en IG" de una app desde una fecha (tras arreglar un error).</summary>
    [HttpPost("finder-reset-no-match")]
    public async Task<IActionResult> ResetNoMatch([FromBody] ResetNoMatchRequest req, CancellationToken ct) =>
        Ok(new { reset = await _svc.ResetNoMatchAsync(req.ProductKey, req.Since, ct) });

    /// <summary>Cierra el batch y libera los que todavía no se buscaron.</summary>
    [HttpPost("batches/{id:guid}/close")]
    public async Task<IActionResult> CloseBatch(Guid id, CancellationToken ct) =>
        await _svc.CloseBatchAsync(id, ct) ? Ok() : NotFound();

    /// <summary>Vuelve a poner en cola los del batch que terminaron sin número.</summary>
    [HttpPost("batches/{id:guid}/retry")]
    public async Task<IActionResult> RetryBatch(Guid id, CancellationToken ct) =>
        Ok(new { requeued = await _svc.RetryBatchAsync(id, ct) });

    /// <summary>Clave de la extensión (la toma sola al abrir sales-hub).</summary>
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

    /// <summary>Último estado que reportó cada extensión (en memoria: se pierde al reiniciar la API).</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (JsonElement State, DateTimeOffset At)> Heartbeats = new();

    /// <summary>La extensión avisa cada 2 min qué está haciendo (sin login: header X-Audience-Key).</summary>
    [HttpPost("finder-heartbeat")]
    [AllowAnonymous]
    public IActionResult FinderHeartbeat([FromBody] JsonElement state)
    {
        if (!KeyOk()) return Unauthorized();
        var account = state.TryGetProperty("account", out var a) && a.ValueKind == JsonValueKind.String ? a.GetString()! : "(sin cuenta)";
        Heartbeats[account] = (state.Clone(), DateTimeOffset.UtcNow);
        return Ok();
    }

    /// <summary>Estado de las extensiones: lo último que reportó cada una y cuándo.</summary>
    [HttpGet("finder-status")]
    public IActionResult FinderStatus() =>
        Ok(Heartbeats.OrderByDescending(h => h.Value.At).Select(h => new { account = h.Key, at = h.Value.At, state = h.Value.State }));

    private bool KeyOk()
    {
        var expected = Encoding.UTF8.GetBytes(AudienceLeadService.IngestKey(_jwt.SigningKey));
        var given = Encoding.UTF8.GetBytes(Request.Headers[KeyHeader].ToString());
        return given.Length == expected.Length && CryptographicOperations.FixedTimeEquals(given, expected);
    }
}
