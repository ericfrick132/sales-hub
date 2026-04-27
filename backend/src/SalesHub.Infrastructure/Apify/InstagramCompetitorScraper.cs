using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SalesHub.Core.Domain.Entities;
using SalesHub.Infrastructure.Options;
using SalesHub.Infrastructure.Persistence;

namespace SalesHub.Infrastructure.Apify;

/// <summary>
/// Runs apify/instagram-scraper in user mode to fetch posts + comments of a competitor,
/// storing into CompetitorPost/CompetitorComment. Negative comments flagged with a simple
/// keyword heuristic for the /competitors screen.
/// </summary>
public class InstagramCompetitorScraper
{
    private static readonly string[] NegativeKeywords =
    {
        "no funciona", "pésimo", "malo", "estafa", "me cobraron",
        "no me", "problema", "no responde", "horrible", "mala atención", "no anda"
    };
    private static readonly Regex HashtagRx = new(@"#(\w+)", RegexOptions.Compiled);

    private readonly ApifyHttpClient _client;
    private readonly ApifyOptions _opts;
    private readonly ApplicationDbContext _db;
    private readonly ILogger<InstagramCompetitorScraper> _log;

    public InstagramCompetitorScraper(ApifyHttpClient client, IOptions<ApifyOptions> opts, ApplicationDbContext db, ILogger<InstagramCompetitorScraper> log)
    {
        _client = client; _opts = opts.Value; _db = db; _log = log;
    }

    public async Task<(int posts, int comments)> ScrapeAsync(Guid competitorId, int maxPosts, CancellationToken ct)
    {
        var competitor = await _db.Competitors.FirstOrDefaultAsync(c => c.Id == competitorId, ct);
        if (competitor is null || competitor.Platform != "instagram") return (0, 0);

        var input = new
        {
            username = new[] { competitor.Handle },
            resultsType = "posts",
            resultsLimit = maxPosts,
            addParentData = true
        };

        var items = await _client.RunActorSyncAsync(_opts.Instagram.ActorId, input, _opts.RunTimeoutSeconds, ct);
        int newPosts = 0, newComments = 0;
        foreach (var item in items)
        {
            var externalId = GetStr(item, "id") ?? GetStr(item, "shortCode");
            if (string.IsNullOrWhiteSpace(externalId)) continue;

            var post = await _db.CompetitorPosts.FirstOrDefaultAsync(p => p.CompetitorId == competitor.Id && p.ExternalPostId == externalId, ct);
            if (post is null)
            {
                post = new CompetitorPost
                {
                    Id = Guid.NewGuid(),
                    CompetitorId = competitor.Id,
                    ExternalPostId = externalId,
                    PostUrl = GetStr(item, "url"),
                    Caption = GetStr(item, "caption"),
                    PostedAt = ParseDate(item, "timestamp"),
                    Likes = GetInt(item, "likesCount") ?? 0,
                    CommentsCount = GetInt(item, "commentsCount") ?? 0,
                    Hashtags = ExtractHashtags(GetStr(item, "caption") ?? ""),
                    RawJson = item.GetRawText()
                };
                _db.CompetitorPosts.Add(post);
                newPosts++;
            }

            if (item.TryGetProperty("latestComments", out var cs) && cs.ValueKind == JsonValueKind.Array)
            {
                foreach (var c in cs.EnumerateArray())
                {
                    var text = GetStr(c, "text") ?? "";
                    var owner = GetStr(c, "ownerUsername");
                    var commentId = GetStr(c, "id") ?? $"{externalId}:{owner}:{text.GetHashCode()}";
                    var exists = await _db.CompetitorComments.AnyAsync(x => x.PostId == post.Id && x.RawJson != null && x.RawJson.Contains(commentId), ct);
                    if (exists) continue;
                    _db.CompetitorComments.Add(new CompetitorComment
                    {
                        Id = Guid.NewGuid(),
                        PostId = post.Id,
                        AuthorHandle = owner,
                        Text = text,
                        PostedAt = ParseDate(c, "timestamp"),
                        IsNegative = NegativeKeywords.Any(k => text.Contains(k, StringComparison.OrdinalIgnoreCase)),
                        RawJson = c.GetRawText()
                    });
                    newComments++;
                }
            }
        }

        competitor.LastScrapedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);
        _log.LogInformation("IG competitor @{Handle}: {P} new posts, {C} new comments", competitor.Handle, newPosts, newComments);
        return (newPosts, newComments);
    }

    private static string? GetStr(JsonElement e, string p)
        => e.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    private static int? GetInt(JsonElement e, string p)
        => e.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : null;
    private static DateTimeOffset? ParseDate(JsonElement e, string p)
    {
        if (!e.TryGetProperty(p, out var v)) return null;
        if (v.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(v.GetString(), out var d)) return d;
        return null;
    }
    private static List<string> ExtractHashtags(string text)
        => HashtagRx.Matches(text).Select(m => m.Groups[1].Value).Distinct().ToList();
}
