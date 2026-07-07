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
        // El gate ahora se evalúa por tick (flag DB 'posteos' que pisa Workers:PosteosAutoStart),
        // así se puede prender/apagar desde la UI sin reiniciar el contenedor.
        _log.LogInformation("SocialContentWorker started (gate por flag 'posteos' / Workers:PosteosAutoStart)");
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
        if (!await db.IsFlagOnAsync("posteos", _config.GetValue<bool>("Workers:PosteosAutoStart", true), ct))
            return;
        var profiles = await db.PostingProfiles.Where(p => p.Enabled).ToListAsync(ct);

        foreach (var profile in profiles)
        {
            if (_ranThisHour.Contains(profile.ProductKey)) continue;
            if (!(profile.PostHours?.Contains(hour) ?? false)) continue;
            // Días de la semana: vacío = todos. (0=Domingo … 6=Sábado)
            if (profile.PostDays is { Count: > 0 } && !profile.PostDays.Contains((int)DateTime.Now.DayOfWeek)) continue;
            _ranThisHour.Add(profile.ProductKey);

            var channels = await db.PostingChannels
                .Where(c => c.ProductKey == profile.ProductKey && c.Enabled)
                .ToListAsync(ct);
            var n = Math.Max(1, profile.PostsPerDay);
            // Guard de reinicio: _ranThisHour vive en memoria y cada deploy lo borra —
            // sin este check en DB, un redeploy dentro del slot duplica el posteo
            // (pasó: gymhero x3 el 7/7 por los deploys de la mañana).
            var now = DateTimeOffset.Now;
            var slotStart = new DateTimeOffset(now.Year, now.Month, now.Day, hour, 0, 0, now.Offset);
            foreach (var channel in channels)
            {
                var already = await db.SocialPosts.CountAsync(s =>
                    s.ProductKey == profile.ProductKey
                    && s.Platform == channel.Platform
                    && s.CreatedAt >= slotStart, ct);
                for (var i = already; i < n; i++)
                {
                    try { await GenerateOneAsync(scope, db, profile, channel, ct); }
                    catch (Exception ex) { _log.LogError(ex, "Posteo gen falló para {Product}/{Net}", profile.ProductKey, channel.Platform); }
                }
            }
        }
    }

    private async Task GenerateOneAsync(IServiceScope scope, ApplicationDbContext db, PostingProfile profile, PostingChannel channel, CancellationToken ct)
    {
        var generator = scope.ServiceProvider.GetRequiredService<SocialContentGenerator>();

        var recent = await db.SocialPosts
            .Where(s => s.ProductKey == profile.ProductKey && s.Platform == channel.Platform)
            .OrderByDescending(s => s.CreatedAt)
            .Select(s => s.Concept)
            .Take(15)
            .ToListAsync(ct);

        var gen = await generator.GenerateForChannelAsync(profile, channel, recent, ct);
        if (gen == null) { _log.LogWarning("Generador null para {Product}/{Net}", profile.ProductKey, channel.Platform); return; }

        var post = new SocialPost
        {
            Id = Guid.NewGuid(),
            ProductKey = profile.ProductKey,
            Platform = channel.Platform,
            BufferChannelId = channel.BufferChannelId,
            Format = channel.Format,
            AssetKind = channel.AssetKind,
            Target = channel.Distribution,
            WarmrAccount = channel.WarmrAccount,
            ContentPillar = gen.Pillar,
            Concept = gen.Concept,
            Prompt = gen.Prompt,
            OverlayText = gen.Overlay,
            Caption = gen.Caption,
            Hashtags = gen.Hashtags,
            GenerationModel = "claude",
            RawJson = gen.RawJson,
            Status = SocialPostStatus.Idea,
            // Agenda en el slot de hoy (la hora que disparó el tick) para que el posteo
            // caiga en el calendario en su horario. Si ya pasó, lo corre 5' adelante
            // (Buffer rechaza fechas pasadas). El humano lo puede mover desde el calendario.
            ScheduledAt = NextSlot(),
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
            var asset = await assetGen.GenerateForPostAsync(profile, post, ct);
            if (asset != null) { post.AssetUrl = asset.Url; post.ThumbnailUrl = asset.ThumbnailUrl; }
        }

        post.Status = SocialPostStatus.DraftReady;
        await db.SaveChangesAsync(ct);

        // Ruteo de distribución según el canal: Warmr (handoff) vs Buffer (API).
        if (channel.Distribution == SocialDistribution.Warmr)
        {
            // Warmr no tiene API → dejamos el posteo en la cola de handoff (ReadyForWarmr)
            // con su asset/caption/cuenta/slot para subida manual a Cloud Drop.
            if (!string.IsNullOrWhiteSpace(post.AssetUrl))
            {
                var warmr = scope.ServiceProvider.GetRequiredService<IWarmrDistributor>();
                await warmr.DispatchAsync(post, ct);
                await db.SaveChangesAsync(ct);
            }
            else
            {
                _log.LogWarning("Posteo Warmr sin asset (no se encola): {Product}/{Net}", profile.ProductKey, channel.Platform);
            }
        }
        else
        {
            // Buffer: auto-publica en el slot (SaveAsDraft=false); para volver a "draft +
            // aprobación humana" sin tocar código → Workers:PosteosAutoPublish=false.
            var autoPublish = _config.GetValue<bool>("Workers:PosteosAutoPublish", true);

            // Push a Buffer (solo si hay asset + canal mapeado)
            if (!string.IsNullOrWhiteSpace(post.AssetUrl) && !string.IsNullOrWhiteSpace(post.BufferChannelId))
            {
                var publisher = scope.ServiceProvider.GetRequiredService<ISocialPublisher>();
                var res = await publisher.CreatePostAsync(new PublishRequest
                {
                    ChannelId = post.BufferChannelId,
                    Service = channel.Platform.ToString().ToLowerInvariant(),
                    Caption = BuildCaption(post),
                    ImageUrl = post.AssetKind == SocialAssetKind.Image ? post.AssetUrl : null,
                    VideoUrl = post.AssetKind == SocialAssetKind.Video ? post.AssetUrl : null,
                    ThumbnailUrl = post.ThumbnailUrl,
                    InstagramType = post.Format.ToString().ToLowerInvariant(),
                    ScheduledAt = post.ScheduledAt,
                    SaveAsDraft = !autoPublish,
                    Automatic = !channel.NotifyPublish,
                }, ct);

                if (res.Success)
                {
                    post.Status = autoPublish ? SocialPostStatus.Scheduled : SocialPostStatus.PushedToBuffer;
                    post.BufferPostId = res.ExternalPostId;
                }
                else { post.Status = SocialPostStatus.Error; post.Error = res.Error; }
                await db.SaveChangesAsync(ct);
            }
        }

        _log.LogInformation("Posteo {Status} para {Product}: {Concept}", post.Status, profile.ProductKey, post.Concept);
    }

    /// <summary>Slot de agenda = hoy a la hora del tick en punto; si ya pasó, ahora + 5'.</summary>
    private static DateTimeOffset NextSlot()
    {
        var now = DateTimeOffset.Now;
        var slot = new DateTimeOffset(now.Year, now.Month, now.Day, now.Hour, 0, 0, now.Offset);
        return slot <= now ? now.AddMinutes(5) : slot;
    }

    private static string BuildCaption(SocialPost p)
    {
        if (p.Hashtags.Count == 0) return p.Caption;
        return $"{p.Caption}\n\n{string.Join(" ", p.Hashtags.Select(h => h.StartsWith('#') ? h : "#" + h))}";
    }
}
