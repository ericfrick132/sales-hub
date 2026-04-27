using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SalesHub.Api.Dtos;
using SalesHub.Core.Domain.Enums;
using SalesHub.Infrastructure.Persistence;
using SalesHub.Infrastructure.Services;

namespace SalesHub.Api.Controllers;

[ApiController]
[Route("api/pipeline")]
[Authorize]
public class PipelineController : ControllerBase
{
    private readonly PipelineService _pipeline;
    private readonly ApplicationDbContext _db;
    public PipelineController(PipelineService pipeline, ApplicationDbContext db)
    {
        _pipeline = pipeline; _db = db;
    }

    [HttpPost("run")]
    public async Task<ActionResult<TriggerPipelineResponse>> Run([FromBody] TriggerPipelineRequest req, CancellationToken ct)
    {
        if (!CurrentUser.IsAdmin(User)) return Forbid();
        var sources = req.Sources?.Length > 0
            ? req.Sources
            : new[] { LeadSource.GooglePlaces };
        var opts = new PipelineRunOptions(req.ProductKey, sources, req.City, req.Province, req.Category,
            req.MaxPerSource ?? 50, req.AutoQueue);
        var created = await _pipeline.RunAsync(opts, ct);
        return new TriggerPipelineResponse(created);
    }

    [HttpGet("runs")]
    public async Task<IActionResult> Runs([FromQuery] int limit = 50, CancellationToken ct = default)
    {
        if (!CurrentUser.IsAdmin(User)) return Forbid();
        var runs = await _db.ApifyRuns.OrderByDescending(r => r.StartedAt).Take(limit).ToListAsync(ct);
        return Ok(runs);
    }

    [HttpGet("scrape-log")]
    public async Task<IActionResult> ScrapeLog([FromQuery] int limit = 100, CancellationToken ct = default)
    {
        if (!CurrentUser.IsAdmin(User)) return Forbid();
        var items = await _db.ScrapeLogs.OrderByDescending(r => r.RunAt).Take(limit).ToListAsync(ct);
        return Ok(items);
    }
}
