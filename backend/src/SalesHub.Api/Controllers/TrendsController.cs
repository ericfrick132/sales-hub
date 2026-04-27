using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SalesHub.Infrastructure.Persistence;

namespace SalesHub.Api.Controllers;

[ApiController]
[Route("api/trends")]
[Authorize]
public class TrendsController : ControllerBase
{
    private readonly ApplicationDbContext _db;
    public TrendsController(ApplicationDbContext db) { _db = db; }

    [HttpGet("hashtags")]
    public async Task<IActionResult> TopHashtags([FromQuery] string? vertical, [FromQuery] int days = 14, CancellationToken ct = default)
    {
        var since = DateTimeOffset.UtcNow.AddDays(-days);
        var q = from p in _db.CompetitorPosts.AsNoTracking()
                join c in _db.Competitors.AsNoTracking() on p.CompetitorId equals c.Id
                where (p.PostedAt ?? p.ScrapedAt) >= since
                      && (vertical == null || c.Vertical == vertical)
                select p;

        var posts = await q.ToListAsync(ct);
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var post in posts)
        {
            foreach (var tag in post.Hashtags ?? new())
            {
                counts.TryGetValue(tag, out var v);
                counts[tag] = v + 1;
            }
        }
        return Ok(counts.OrderByDescending(kv => kv.Value).Take(50).Select(kv => new { hashtag = kv.Key, count = kv.Value }));
    }

    [HttpGet("top-posts")]
    public async Task<IActionResult> TopPosts([FromQuery] string? vertical, [FromQuery] int days = 14, [FromQuery] int limit = 30, CancellationToken ct = default)
    {
        var since = DateTimeOffset.UtcNow.AddDays(-days);
        var q = from p in _db.CompetitorPosts.AsNoTracking()
                join c in _db.Competitors.AsNoTracking() on p.CompetitorId equals c.Id
                where (p.PostedAt ?? p.ScrapedAt) >= since
                      && (vertical == null || c.Vertical == vertical)
                orderby (p.Likes + p.CommentsCount) descending
                select new { c.Handle, c.DisplayName, p.Caption, p.PostedAt, p.Likes, p.CommentsCount, p.PostUrl };
        return Ok(await q.Take(Math.Min(limit, 200)).ToListAsync(ct));
    }
}
