using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SalesHub.Core.Abstractions;
using SalesHub.Core.Domain.Entities.Social;
using SalesHub.Infrastructure.Persistence;
using SalesHub.Infrastructure.Services.Social;

namespace SalesHub.Workers;

/// <summary>
/// Módulo Posteos: por cada PostingProfile habilitado, en sus PostHours, genera
/// N ideas con Claude, (si hay generador) crea el asset, y las sube a Buffer como
/// DRAFT para aprobación humana. Apagado por defecto:
/// Workers:PosteosAutoStart=true para habilitarlo.
/// </summary>
public class SocialContentWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly IConfiguration _config;
    private readonly ILogger<SocialContentWorker> _log;
    private readonly HashSet<string> _ranThisHour = new();
    private int _lastHour = -1;

    public SocialContentWorker(IServiceScopeFactory scopes, IConfiguration config, ILogger<SocialContentWorker> log)
    {
        _scopes = scopes; _config = config; _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_config.GetValue<bool>("Workers:PosteosAutoStart", false))
        {
            _log.LogInformation("SocialContentWorker disabled (set Workers:PosteosAutoStart=true). Se puede generar a demanda desde la API.");
            return;
        }
        _log.LogInformation("SocialContentWorker started");
        await Task.Delay(TimeSpan.FromMinutes(2), stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await TickAsync(stoppingToken); }
            catch (Exception ex) { _log.LogError(ex, "SocialContentWorker tick failed"); }
            await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);
        }
    }

    private async Task TickAsync(CancellationToken ct)
    {
        var hour = DateTime.Now.Hour;
        if (hour != _lastHour) { _lastHour = hour; _ranThisHour.Clear(); }

        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var profiles = await db.PostingProfiles.Where(p => p.Enabled).ToListAsync(ct);

        foreach (var profile in profiles)
        {
            if (_ranThisHour.Contains(profile.ProductKey)) continue;
            if (!(profile.PostHours?.Contains(hour) ?? false)) continue;
            _ranThisHour.Add(profile.ProductKey);

            var n = Math.Max(1, profile.PostsPerDay);
            for (var i = 0; i < n; i++)
            {
                try { await GenerateOneAsync(scope, db, profile, ct); }
                catch (Exception ex) { _log.LogError(ex, "Posteo gen falló para {Product}", profile.ProductKey); }
            }
        }
    }

    private async Task GenerateOneAsync(IServiceScope scope, ApplicationDbContext db, PostingProfile profile, CancellationToken ct)
    {
        var generator = scope.ServiceProvider.GetRequiredService<SocialContentGenerator>();

        var recent = await db.SocialPosts
            .Where(s => s.ProductKey == profile.ProductKey)
            .OrderByDescending(s => s.CreatedAt)
            .Select(s => s.Concept)
            .Take(15)
            .ToListAsync(ct);

        var gen = await generator.GenerateAsync(profile, recent, ct);
        if (gen == null) { _log.LogWarning("Generador devolvió null para {Product}", profile.ProductKey); return; }

        var (platform, channelId) = PickChannel(profile.BufferChannelsJson, gen.AssetKind);

        var post = new SocialPost
        {
            Id = Guid.NewGuid(),
            ProductKey = profile.ProductKey,
            Platform = platform,
            BufferChannelId = channelId ?? string.Empty,
            Format = ParseFormat(gen.Format),
            AssetKind = gen.AssetKind == "video" ? SocialAssetKind.Video : SocialAssetKind.Image,
            ContentPillar = gen.Pillar,
            Concept = gen.Concept,
            Prompt = gen.Prompt,
            Caption = gen.Caption,
            Hashtags = gen.Hashtags,
            GenerationModel = "claude",
            RawJson = gen.RawJson,
            Status = SocialPostStatus.Idea,
        };
        db.SocialPosts.Add(post);
        await db.SaveChangesAsync(ct);

        // Asset (si hay un generador que lo maneje)
        var assetGen = scope.ServiceProvider.GetServices<ISocialAssetGenerator>()
            .FirstOrDefault(g => g.CanHandle(gen.AssetKind));
        if (assetGen != null)
        {
            post.Status = SocialPostStatus.GeneratingAsset;
            await db.SaveChangesAsync(ct);
            var asset = await assetGen.GenerateAsync(gen.Prompt, gen.AssetKind, ct);
            if (asset != null) { post.AssetUrl = asset.Url; post.ThumbnailUrl = asset.ThumbnailUrl; }
        }

        post.Status = SocialPostStatus.DraftReady;
        await db.SaveChangesAsync(ct);

        // Push a Buffer como draft (solo si hay asset + canal mapeado)
        if (!string.IsNullOrWhiteSpace(post.AssetUrl) && !string.IsNullOrWhiteSpace(post.BufferChannelId))
        {
            var publisher = scope.ServiceProvider.GetRequiredService<ISocialPublisher>();
            var res = await publisher.CreatePostAsync(new PublishRequest
            {
                ChannelId = post.BufferChannelId,
                Service = platform.ToString().ToLowerInvariant(),
                Caption = BuildCaption(post),
                ImageUrl = post.AssetKind == SocialAssetKind.Image ? post.AssetUrl : null,
                VideoUrl = post.AssetKind == SocialAssetKind.Video ? post.AssetUrl : null,
                ThumbnailUrl = post.ThumbnailUrl,
                InstagramType = post.Format.ToString().ToLowerInvariant(),
                SaveAsDraft = true,
                Automatic = true,
            }, ct);

            if (res.Success) { post.Status = SocialPostStatus.PushedToBuffer; post.BufferPostId = res.ExternalPostId; }
            else { post.Status = SocialPostStatus.Error; post.Error = res.Error; }
            await db.SaveChangesAsync(ct);
        }

        _log.LogInformation("Posteo {Status} para {Product}: {Concept}", post.Status, profile.ProductKey, post.Concept);
    }

    private static string BuildCaption(SocialPost p)
    {
        if (p.Hashtags.Count == 0) return p.Caption;
        return $"{p.Caption}\n\n{string.Join(" ", p.Hashtags.Select(h => h.StartsWith('#') ? h : "#" + h))}";
    }

    private static SocialPostFormat ParseFormat(string f) =>
        Enum.TryParse<SocialPostFormat>(f, true, out var v) ? v : SocialPostFormat.Post;

    /// <summary>Elige canal+plataforma del mapa {service:channelId} según el tipo de asset.</summary>
    private static (SocialPlatform, string?) PickChannel(string channelsJson, string assetKind)
    {
        Dictionary<string, string>? map = null;
        try { map = JsonSerializer.Deserialize<Dictionary<string, string>>(channelsJson); } catch { }
        map ??= new();

        var order = assetKind == "video"
            ? new[] { "tiktok", "youtube", "instagram", "facebook" }
            : new[] { "instagram", "facebook", "tiktok", "twitter" };

        foreach (var svc in order)
            if (map.TryGetValue(svc, out var id) && !string.IsNullOrWhiteSpace(id))
                return (ToPlatform(svc), id);

        // fallback: primer canal mapeado, o Instagram sin canal
        var first = map.FirstOrDefault(kv => !string.IsNullOrWhiteSpace(kv.Value));
        return first.Key != null ? (ToPlatform(first.Key), first.Value) : (SocialPlatform.Instagram, null);
    }

    private static SocialPlatform ToPlatform(string svc) => svc.ToLowerInvariant() switch
    {
        "instagram" => SocialPlatform.Instagram,
        "tiktok" => SocialPlatform.TikTok,
        "youtube" => SocialPlatform.YouTube,
        "facebook" => SocialPlatform.Facebook,
        "twitter" => SocialPlatform.Twitter,
        "linkedin" => SocialPlatform.LinkedIn,
        _ => SocialPlatform.Instagram,
    };
}
