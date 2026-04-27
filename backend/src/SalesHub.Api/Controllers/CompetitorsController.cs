using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SalesHub.Core.Domain.Entities;
using SalesHub.Infrastructure.Persistence;

namespace SalesHub.Api.Controllers;

[ApiController]
[Route("api/competitors")]
[Authorize]
public class CompetitorsController : ControllerBase
{
    private readonly ApplicationDbContext _db;
    public CompetitorsController(ApplicationDbContext db) { _db = db; }

    public record CreateCompetitor(string Handle, string Platform, string? DisplayName, string? Vertical);

    [HttpGet]
    public async Task<IActionResult> List([FromQuery] string? vertical, CancellationToken ct)
    {
        var q = _db.Competitors.AsNoTracking().Where(c => c.IsActive);
        if (!string.IsNullOrWhiteSpace(vertical)) q = q.Where(c => c.Vertical == vertical);
        return Ok(await q.OrderBy(c => c.Handle).ToListAsync(ct));
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateCompetitor req, CancellationToken ct)
    {
        if (!CurrentUser.IsAdmin(User)) return Forbid();
        var entity = new Competitor
        {
            Id = Guid.NewGuid(),
            Handle = req.Handle,
            Platform = string.IsNullOrWhiteSpace(req.Platform) ? "instagram" : req.Platform.ToLowerInvariant(),
            DisplayName = req.DisplayName,
            Vertical = req.Vertical,
            IsActive = true
        };
        _db.Competitors.Add(entity);
        await _db.SaveChangesAsync(ct);
        return Ok(entity);
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        if (!CurrentUser.IsAdmin(User)) return Forbid();
        var c = await _db.Competitors.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (c is null) return NotFound();
        c.IsActive = false;
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }

    [HttpGet("{id:guid}/posts")]
    public async Task<IActionResult> Posts(Guid id, [FromQuery] int limit = 50, CancellationToken ct = default)
    {
        var posts = await _db.CompetitorPosts.AsNoTracking()
            .Where(p => p.CompetitorId == id)
            .OrderByDescending(p => p.PostedAt ?? p.ScrapedAt)
            .Take(Math.Min(limit, 200))
            .ToListAsync(ct);
        return Ok(posts);
    }

    [HttpGet("{id:guid}/negative-comments")]
    public async Task<IActionResult> NegativeComments(Guid id, [FromQuery] int limit = 100, CancellationToken ct = default)
    {
        var q = from c in _db.CompetitorComments.AsNoTracking()
                join p in _db.CompetitorPosts on c.PostId equals p.Id
                where p.CompetitorId == id && c.IsNegative
                orderby c.PostedAt descending
                select new { c.Id, c.AuthorHandle, c.Text, c.PostedAt, PostId = p.Id, p.PostUrl };
        return Ok(await q.Take(Math.Min(limit, 500)).ToListAsync(ct));
    }
}
