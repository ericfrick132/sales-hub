using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SalesHub.Core.Abstractions;
using SalesHub.Core.Domain.Entities.Social;
using SalesHub.Infrastructure.Persistence;
using SalesHub.Infrastructure.Services.Social;

namespace SalesHub.Api.Controllers;

/// <summary>
/// Módulo Posteos: generar contenido a demanda, listar perfiles/posteos/canales
/// y empujar a Buffer como draft. El worker hace lo mismo en automático.
/// </summary>
[ApiController]
[Route("api/posteos")]
[Authorize]
public class SocialPostsController : ControllerBase
{
    private readonly ApplicationDbContext _db;
    private readonly SocialContentGenerator _generator;
    private readonly ISocialPublisher _publisher;
    private readonly ILogger<SocialPostsController> _log;

    public SocialPostsController(ApplicationDbContext db, SocialContentGenerator generator, ISocialPublisher publisher, ILogger<SocialPostsController> log)
    {
        _db = db; _generator = generator; _publisher = publisher; _log = log;
    }

    // ── Perfiles de marca ──────────────────────────────────────────────────
    [HttpGet("profiles")]
    public async Task<IActionResult> Profiles(CancellationToken ct)
    {
        var profiles = await _db.PostingProfiles.OrderBy(p => p.ProductKey).ToListAsync(ct);
        return Ok(profiles);
    }

    [HttpPut("profiles/{productKey}")]
    public async Task<IActionResult> UpdateProfile(string productKey, [FromBody] UpdateProfileRequest req, CancellationToken ct)
    {
        var p = await _db.PostingProfiles.FirstOrDefaultAsync(x => x.ProductKey == productKey, ct);
        if (p == null) return NotFound();
        if (req.Enabled.HasValue) p.Enabled = req.Enabled.Value;
        if (req.BufferChannelsJson != null) p.BufferChannelsJson = req.BufferChannelsJson;
        if (req.PostHours != null) p.PostHours = req.PostHours;
        if (req.PostsPerDay.HasValue) p.PostsPerDay = req.PostsPerDay.Value;
        if (req.ContentPillars != null) p.ContentPillars = req.ContentPillars;
        if (req.BrandVoice != null) p.BrandVoice = req.BrandVoice;
        if (req.BrandGuidelines != null) p.BrandGuidelines = req.BrandGuidelines;
        p.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);
        return Ok(p);
    }

    // ── Canales de Buffer ──────────────────────────────────────────────────
    [HttpGet("channels")]
    public async Task<IActionResult> Channels(CancellationToken ct)
    {
        try { return Ok(await _publisher.ListChannelsAsync(ct)); }
        catch (Exception ex) { return BadRequest(new { error = ex.Message }); }
    }

    // ── Posteos ────────────────────────────────────────────────────────────
    [HttpGet]
    public async Task<IActionResult> List([FromQuery] string? productKey, [FromQuery] string? status, [FromQuery] int take = 50, CancellationToken ct = default)
    {
        var q = _db.SocialPosts.AsQueryable();
        if (!string.IsNullOrWhiteSpace(productKey)) q = q.Where(s => s.ProductKey == productKey);
        if (!string.IsNullOrWhiteSpace(status) && Enum.TryParse<SocialPostStatus>(status, true, out var st))
            q = q.Where(s => s.Status == st);
        var posts = await q.OrderByDescending(s => s.CreatedAt).Take(Math.Clamp(take, 1, 200)).ToListAsync(ct);
        return Ok(posts);
    }

    /// <summary>Genera 1 idea de posteo a demanda con Claude (status DraftReady, sin asset todavía).</summary>
    [HttpPost("generate")]
    public async Task<IActionResult> Generate([FromBody] GenerateRequest req, CancellationToken ct)
    {
        var profile = await _db.PostingProfiles.FirstOrDefaultAsync(p => p.ProductKey == req.ProductKey, ct);
        if (profile == null) return NotFound(new { error = "No hay PostingProfile para ese producto." });
        if (!_generator.IsConfigured) return BadRequest(new { error = "Claude no está configurado (falta Claude:ApiKey)." });

        var recent = await _db.SocialPosts.Where(s => s.ProductKey == req.ProductKey)
            .OrderByDescending(s => s.CreatedAt).Select(s => s.Concept).Take(15).ToListAsync(ct);

        var gen = await _generator.GenerateAsync(profile, recent, ct);
        if (gen == null) return StatusCode(502, new { error = "El generador no devolvió contenido." });

        var post = new SocialPost
        {
            Id = Guid.NewGuid(), ProductKey = req.ProductKey,
            Platform = SocialPlatform.Instagram,
            Format = Enum.TryParse<SocialPostFormat>(gen.Format, true, out var f) ? f : SocialPostFormat.Post,
            AssetKind = gen.AssetKind == "video" ? SocialAssetKind.Video : SocialAssetKind.Image,
            ContentPillar = gen.Pillar, Concept = gen.Concept, Prompt = gen.Prompt,
            Caption = gen.Caption, Hashtags = gen.Hashtags, GenerationModel = "claude",
            RawJson = gen.RawJson, Status = SocialPostStatus.DraftReady,
        };
        _db.SocialPosts.Add(post);
        await _db.SaveChangesAsync(ct);
        return Ok(post);
    }

    /// <summary>Empuja un posteo a Buffer como DRAFT. Acepta override de assetUrl/channelId (ej. URL de Canva a mano).</summary>
    [HttpPost("{id:guid}/push")]
    public async Task<IActionResult> Push(Guid id, [FromBody] PushRequest? req, CancellationToken ct)
    {
        var post = await _db.SocialPosts.FirstOrDefaultAsync(s => s.Id == id, ct);
        if (post == null) return NotFound();

        var assetUrl = req?.AssetUrl ?? post.AssetUrl;
        var channelId = req?.ChannelId ?? post.BufferChannelId;
        if (string.IsNullOrWhiteSpace(assetUrl)) return BadRequest(new { error = "Falta assetUrl (generá el video/imagen o pasá una URL)." });
        if (string.IsNullOrWhiteSpace(channelId)) return BadRequest(new { error = "Falta channelId (mapealo en el perfil o pasalo en el body)." });

        post.AssetUrl = assetUrl; post.BufferChannelId = channelId;

        var res = await _publisher.CreatePostAsync(new PublishRequest
        {
            ChannelId = channelId,
            Service = post.Platform.ToString().ToLowerInvariant(),
            Caption = post.Hashtags.Count > 0 ? $"{post.Caption}\n\n{string.Join(" ", post.Hashtags.Select(h => h.StartsWith('#') ? h : "#" + h))}" : post.Caption,
            ImageUrl = post.AssetKind == SocialAssetKind.Image ? assetUrl : null,
            VideoUrl = post.AssetKind == SocialAssetKind.Video ? assetUrl : null,
            ThumbnailUrl = post.ThumbnailUrl,
            InstagramType = post.Format.ToString().ToLowerInvariant(),
            ScheduledAt = req?.ScheduledAt,
            SaveAsDraft = true,
            Automatic = true,
        }, ct);

        if (res.Success) { post.Status = SocialPostStatus.PushedToBuffer; post.BufferPostId = res.ExternalPostId; post.Error = null; }
        else { post.Status = SocialPostStatus.Error; post.Error = res.Error; }
        post.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);
        return res.Success ? Ok(post) : BadRequest(new { error = res.Error, post });
    }

    [HttpPost("{id:guid}/reject")]
    public async Task<IActionResult> Reject(Guid id, CancellationToken ct)
    {
        var post = await _db.SocialPosts.FirstOrDefaultAsync(s => s.Id == id, ct);
        if (post == null) return NotFound();
        post.Status = SocialPostStatus.Rejected; post.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);
        return Ok(post);
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        var post = await _db.SocialPosts.FirstOrDefaultAsync(s => s.Id == id, ct);
        if (post == null) return NotFound();
        // Si ya estaba en Buffer, intentamos borrarlo allá también.
        if (!string.IsNullOrWhiteSpace(post.BufferPostId))
            try { await _publisher.DeletePostAsync(post.BufferPostId!, ct); } catch (Exception ex) { _log.LogWarning(ex, "No pude borrar en Buffer"); }
        _db.SocialPosts.Remove(post);
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }

    public class GenerateRequest { public string ProductKey { get; set; } = string.Empty; }
    public class PushRequest { public string? AssetUrl { get; set; } public string? ChannelId { get; set; } public DateTimeOffset? ScheduledAt { get; set; } }
    public class UpdateProfileRequest
    {
        public bool? Enabled { get; set; }
        public string? BufferChannelsJson { get; set; }
        public List<int>? PostHours { get; set; }
        public int? PostsPerDay { get; set; }
        public List<string>? ContentPillars { get; set; }
        public string? BrandVoice { get; set; }
        public string? BrandGuidelines { get; set; }
    }
}
