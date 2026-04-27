using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SalesHub.Core.Domain.Entities;
using SalesHub.Infrastructure.Options;
using SalesHub.Infrastructure.Persistence;

namespace SalesHub.Infrastructure.Apify;

/// <summary>
/// Runs clockworks/tiktok-scraper for hashtag/keyword and persists posts into CompetitorPost
/// tagged with vertical so /trends can show top videos per vertical.
/// </summary>
public class ApifyTikTokSource
{
    private readonly ApifyHttpClient _client;
    private readonly ApifyOptions _opts;
    private readonly ApplicationDbContext _db;
    private readonly ILogger<ApifyTikTokSource> _log;

    public ApifyTikTokSource(ApifyHttpClient client, IOptions<ApifyOptions> opts, ApplicationDbContext db, ILogger<ApifyTikTokSource> log)
    {
        _client = client; _opts = opts.Value; _db = db; _log = log;
    }

    public async Task<int> FetchHashtagAsync(string hashtag, string vertical, int maxResults, CancellationToken ct)
    {
        if (!_opts.TikTok.Enabled) return 0;
        hashtag = hashtag.TrimStart('#');

        var input = new
        {
            hashtags = new[] { hashtag },
            resultsPerPage = Math.Min(maxResults, _opts.TikTok.MaxResults),
            shouldDownloadVideos = false,
            shouldDownloadCovers = false,
            shouldDownloadSubtitles = false
        };

        JsonElement[] items;
        try
        {
            items = await _client.RunActorSyncAsync(_opts.TikTok.ActorId, input, _opts.RunTimeoutSeconds, ct);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "TikTok scraper failed for #{Tag}", hashtag);
            return 0;
        }

        var competitor = await GetOrCreateVerticalBucketAsync(vertical, ct);
        var saved = 0;
        foreach (var item in items)
        {
            var externalId = GetStr(item, "id") ?? GetStr(item, "videoId");
            if (string.IsNullOrWhiteSpace(externalId)) continue;

            var exists = _db.CompetitorPosts.Any(p => p.CompetitorId == competitor.Id && p.ExternalPostId == externalId);
            if (exists) continue;

            _db.CompetitorPosts.Add(new CompetitorPost
            {
                Id = Guid.NewGuid(),
                CompetitorId = competitor.Id,
                ExternalPostId = externalId,
                PostUrl = GetStr(item, "webVideoUrl") ?? GetStr(item, "videoUrl"),
                Caption = GetStr(item, "text"),
                PostedAt = ParseDate(item, "createTimeISO") ?? ParseDate(item, "createTime"),
                Likes = GetInt(item, "diggCount") ?? 0,
                CommentsCount = GetInt(item, "commentCount") ?? 0,
                Hashtags = ExtractHashtags(item),
                RawJson = item.GetRawText()
            });
            saved++;
        }
        await _db.SaveChangesAsync(ct);
        _log.LogInformation("TikTok #{Tag} -> {N} new posts for vertical {V}", hashtag, saved, vertical);
        return saved;
    }

    private async Task<Competitor> GetOrCreateVerticalBucketAsync(string vertical, CancellationToken ct)
    {
        var handle = $"__tiktok_trends_{vertical}";
        var existing = _db.Competitors.FirstOrDefault(c => c.Platform == "tiktok" && c.Handle == handle);
        if (existing is not null) return existing;
        var c = new Competitor
        {
            Id = Guid.NewGuid(),
            Handle = handle,
            Platform = "tiktok",
            DisplayName = $"TikTok trends — {vertical}",
            Vertical = vertical,
            IsActive = true
        };
        _db.Competitors.Add(c);
        await _db.SaveChangesAsync(ct);
        return c;
    }

    private static string? GetStr(JsonElement e, string p)
        => e.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    private static int? GetInt(JsonElement e, string p)
        => e.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : null;
    private static DateTimeOffset? ParseDate(JsonElement e, string p)
    {
        if (!e.TryGetProperty(p, out var v)) return null;
        if (v.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(v.GetString(), out var d)) return d;
        if (v.ValueKind == JsonValueKind.Number)
        {
            var n = v.GetInt64();
            if (n > 1_000_000_000_000) return DateTimeOffset.FromUnixTimeMilliseconds(n);
            return DateTimeOffset.FromUnixTimeSeconds(n);
        }
        return null;
    }
    private static List<string> ExtractHashtags(JsonElement item)
    {
        var result = new List<string>();
        if (item.TryGetProperty("hashtags", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var h in arr.EnumerateArray())
            {
                var name = h.ValueKind == JsonValueKind.Object && h.TryGetProperty("name", out var n)
                    ? n.GetString()
                    : (h.ValueKind == JsonValueKind.String ? h.GetString() : null);
                if (!string.IsNullOrWhiteSpace(name)) result.Add(name!);
            }
        }
        return result;
    }
}
