using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SalesHub.Infrastructure.Instagram;
using SalesHub.Infrastructure.Persistence;
using SalesHub.Workers;

namespace SalesHub.Api.Controllers;

[ApiController]
[Route("api/instagram/structure")]
[Authorize]
public class InstagramStructureController : ControllerBase
{
    private readonly InstagramStructureAnalyzerWorker _worker;
    private readonly SelectorFailureTracker _failureTracker;
    private readonly ApplicationDbContext _db;
    private readonly ILogger<InstagramStructureController> _log;

    public InstagramStructureController(
        InstagramStructureAnalyzerWorker worker,
        SelectorFailureTracker failureTracker,
        ApplicationDbContext db,
        ILogger<InstagramStructureController> log)
    {
        _worker = worker;
        _failureTracker = failureTracker;
        _db = db;
        _log = log;
    }

    /// <summary>
    /// Ejecuta un análisis de estructura a demanda.
    /// POST /api/instagram/structure/analyze
    /// </summary>
    [HttpPost("analyze")]
    public async Task<IActionResult> AnalyzeStructure()
    {
        _log.LogInformation("Análisis de estructura solicitado manualmente via API");

        var started = await _worker.RunAnalysisOnDemandAsync(HttpContext.RequestAborted);

        if (!started)
        {
            return Conflict(new
            {
                message = "Ya hay un análisis de estructura en ejecución. Espere a que termine."
            });
        }

        return Accepted(new
        {
            message = "Análisis de estructura iniciado correctamente. " +
                      "Los resultados se guardarán en la tabla instagram_structure_snapshots."
        });
    }

    /// <summary>
    /// Obtiene el último snapshot de estructura válido.
    /// GET /api/instagram/structure/latest
    /// </summary>
    [HttpGet("latest")]
    public async Task<IActionResult> GetLatestStructure()
    {
        var latest = await _db.InstagramStructureSnapshots
            .Where(s => s.IsValid)
            .OrderByDescending(s => s.SnapshotDate)
            .ThenByDescending(s => s.CreatedAt)
            .FirstOrDefaultAsync();

        if (latest is null)
        {
            return NotFound(new
            {
                message = "No hay snapshots de estructura disponibles. " +
                          "Ejecute un análisis primero (POST /api/instagram/structure/analyze)."
            });
        }

        return Ok(new
        {
            id = latest.Id,
            snapshotDate = latest.SnapshotDate,
            llmModel = latest.LlmModel,
            isValid = latest.IsValid,
            createdAt = latest.CreatedAt,
            selectors = System.Text.Json.JsonSerializer.Deserialize<object>(latest.SelectorsJson)
        });
    }

    /// <summary>
    /// Obtiene el historial de snapshots de estructura.
    /// GET /api/instagram/structure/history?limit=10
    /// </summary>
    [HttpGet("history")]
    public async Task<IActionResult> GetHistory([FromQuery] int limit = 10)
    {
        var history = await _db.InstagramStructureSnapshots
            .OrderByDescending(s => s.CreatedAt)
            .Take(limit)
            .Select(s => new
            {
                s.Id,
                s.SnapshotDate,
                s.LlmModel,
                s.IsValid,
                s.ErrorMessage,
                s.CreatedAt
            })
            .ToListAsync();

        return Ok(history);
    }

    /// <summary>
    /// Obtiene el estado actual de los contadores de fallos de selectores.
    /// GET /api/instagram/structure/failures
    /// </summary>
    [HttpGet("failures")]
    public IActionResult GetFailureStatus()
    {
        var snapshot = _failureTracker.GetSnapshot();

        return Ok(new
        {
            hasFailures = snapshot.Count > 0,
            failures = snapshot,
            maxFailuresBeforeReanalysis = 3 // InstagramOptions.MaxSelectorFailuresBeforeReanalysis
        });
    }

    /// <summary>
    /// Resetea los contadores de fallos de selectores.
    /// POST /api/instagram/structure/failures/reset
    /// </summary>
    [HttpPost("failures/reset")]
    public IActionResult ResetFailures()
    {
        _failureTracker.ResetAll();
        return Ok(new { message = "Contadores de fallos reseteados correctamente." });
    }
}
