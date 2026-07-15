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

    public record CreateCompetitor(string Handle, string Platform, string? DisplayName, string? Vertical, string? Purpose);
    public record UpdateCompetitor(string? Purpose, string? Vertical, string? DisplayName);

    /// <summary>inspiration | followsource | both (default). Cualquier otra cosa = both.</summary>
    private static CompetitorPurpose ParsePurpose(string? s) =>
        Enum.TryParse<CompetitorPurpose>((s ?? "").Trim(), ignoreCase: true, out var p) ? p : CompetitorPurpose.Both;

    [HttpGet]
    public async Task<IActionResult> List([FromQuery] string? vertical, [FromQuery] string? purpose, CancellationToken ct)
    {
        var q = _db.Competitors.AsNoTracking().Where(c => c.IsActive);
        if (!string.IsNullOrWhiteSpace(vertical)) q = q.Where(c => c.Vertical == vertical);
        // Filtro por propósito: 'inspiration' devuelve Inspiration+Both; 'followsource' devuelve FollowSource+Both.
        if (!string.IsNullOrWhiteSpace(purpose))
        {
            var p = ParsePurpose(purpose);
            if (p == CompetitorPurpose.Inspiration) q = q.Where(c => c.Purpose != CompetitorPurpose.FollowSource);
            else if (p == CompetitorPurpose.FollowSource) q = q.Where(c => c.Purpose != CompetitorPurpose.Inspiration);
        }
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
            Purpose = ParsePurpose(req.Purpose),
            IsActive = true
        };
        _db.Competitors.Add(entity);
        await _db.SaveChangesAsync(ct);
        return Ok(entity);
    }

    [HttpPatch("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdateCompetitor req, CancellationToken ct)
    {
        if (!CurrentUser.IsAdmin(User)) return Forbid();
        var c = await _db.Competitors.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (c is null) return NotFound();
        if (req.Purpose is not null) c.Purpose = ParsePurpose(req.Purpose);
        if (!string.IsNullOrWhiteSpace(req.Vertical)) c.Vertical = req.Vertical;
        if (req.DisplayName is not null) c.DisplayName = req.DisplayName;
        await _db.SaveChangesAsync(ct);
        return Ok(c);
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

    public record CurateRequest(bool Curated);

    /// <summary>Marca/desmarca un post de competidor como inspiración ("me gusta esto, replicalo").</summary>
    [HttpPost("posts/{postId:guid}/curate")]
    public async Task<IActionResult> Curate(Guid postId, [FromBody] CurateRequest req, CancellationToken ct)
    {
        if (!CurrentUser.IsAdmin(User)) return Forbid();
        var post = await _db.CompetitorPosts.FirstOrDefaultAsync(p => p.Id == postId, ct);
        if (post is null) return NotFound();
        post.Curated = req.Curated;
        post.CuratedAt = req.Curated ? DateTimeOffset.UtcNow : null;
        await _db.SaveChangesAsync(ct);
        return Ok(post);
    }

    /// <summary>Board de inspiración: posts curados (cross-competidor), con el handle de origen.</summary>
    [HttpGet("inspiration")]
    public async Task<IActionResult> Inspiration([FromQuery] string? vertical, [FromQuery] int limit = 60, CancellationToken ct = default)
    {
        var q = from p in _db.CompetitorPosts.AsNoTracking()
                join c in _db.Competitors on p.CompetitorId equals c.Id
                where p.Curated && (string.IsNullOrEmpty(vertical) || c.Vertical == vertical)
                orderby p.CuratedAt descending
                select new
                {
                    p.Id, p.Caption, p.Hashtags, p.MediaUrl, p.ThumbnailUrl, p.PostUrl,
                    p.IsVideo, p.Likes, p.CommentsCount, p.CuratedAt,
                    Source = c.Handle, c.Platform, c.Vertical
                };
        return Ok(await q.Take(Math.Min(limit, 200)).ToListAsync(ct));
    }
}
