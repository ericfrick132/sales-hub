using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SalesHub.Core.Abstractions;
using SalesHub.Core.Domain.Entities.Social;
using SalesHub.Infrastructure.Persistence;
using SalesHub.Infrastructure.Services;
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
    private readonly IEnumerable<ISocialAssetGenerator> _assetGenerators;
    private readonly ISocialPublisher _publisher;
    private readonly IWarmrDistributor _warmr;
    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<SocialPostsController> _log;

    public SocialPostsController(ApplicationDbContext db, SocialContentGenerator generator, IEnumerable<ISocialAssetGenerator> assetGenerators, ISocialPublisher publisher, IWarmrDistributor warmr, IHttpClientFactory httpFactory, ILogger<SocialPostsController> log)
    {
        _db = db; _generator = generator; _assetGenerators = assetGenerators; _publisher = publisher; _warmr = warmr; _httpFactory = httpFactory; _log = log;
    }

    private ISocialAssetGenerator? GeneratorFor(SocialAssetKind kind) =>
        _assetGenerators.FirstOrDefault(g => g.CanHandle(kind == SocialAssetKind.Video ? "video" : "image"));

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
        if (req.PostDays != null) p.PostDays = req.PostDays;
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

    /// <summary>
    /// Calendario universal: todos los posteos de todas las apps (o de una sola) cuyo
    /// ScheduledAt cae en [from, to), más un "backlog" de posteos sin agendar todavía.
    /// Alimenta la vista mes/semana del front.
    /// </summary>
    [HttpGet("calendar")]
    public async Task<IActionResult> Calendar([FromQuery] DateTimeOffset from, [FromQuery] DateTimeOffset to, [FromQuery] string? productKey, CancellationToken ct)
    {
        var q = _db.SocialPosts.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(productKey)) q = q.Where(s => s.ProductKey == productKey);

        var scheduled = await q
            .Where(s => s.ScheduledAt != null && s.ScheduledAt >= from && s.ScheduledAt < to)
            .OrderBy(s => s.ScheduledAt)
            .ToListAsync(ct);

        // Sin agendar y todavía vivos (no rechazados ni ya posteados): los mostramos
        // en una columna aparte para arrastrarlos/agendarlos a un día.
        var backlog = await q
            .Where(s => s.ScheduledAt == null
                && s.Status != SocialPostStatus.Rejected
                && s.Status != SocialPostStatus.Posted)
            .OrderByDescending(s => s.CreatedAt)
            .Take(100)
            .ToListAsync(ct);

        return Ok(new { scheduled, backlog });
    }

    /// <summary>
    /// Edita un posteo desde el calendario: contenido (concepto/caption/hashtags/pilar),
    /// formato, canal y fecha agendada. Pasar scheduledAt=null lo manda al backlog.
    /// </summary>
    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdatePostRequest req, CancellationToken ct)
    {
        var post = await _db.SocialPosts.FirstOrDefaultAsync(s => s.Id == id, ct);
        if (post == null) return NotFound();

        if (req.Concept != null) post.Concept = req.Concept;
        if (req.Caption != null) post.Caption = req.Caption;
        if (req.Hashtags != null) post.Hashtags = req.Hashtags;
        if (req.ContentPillar != null) post.ContentPillar = req.ContentPillar;
        if (req.BufferChannelId != null) post.BufferChannelId = req.BufferChannelId;
        if (!string.IsNullOrWhiteSpace(req.Format) && Enum.TryParse<SocialPostFormat>(req.Format, true, out var f)) post.Format = f;
        // ScheduledAt es deliberadamente sobreescribible a null (mandar al backlog).
        if (req.SetScheduledAt) post.ScheduledAt = req.ScheduledAt;
        post.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);
        return Ok(post);
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

    // ── Canales por red (red × app, con prompt propio) ─────────────────────
    [HttpGet("posting-channels")]
    public async Task<IActionResult> PostingChannels([FromQuery] string? productKey, CancellationToken ct)
    {
        var q = _db.PostingChannels.AsQueryable();
        if (!string.IsNullOrWhiteSpace(productKey)) q = q.Where(c => c.ProductKey == productKey);
        var channels = await q.OrderBy(c => c.ProductKey).ThenBy(c => c.Platform).ToListAsync(ct);
        return Ok(channels);
    }

    [HttpPut("posting-channels/{id:guid}")]
    public async Task<IActionResult> UpdatePostingChannel(Guid id, [FromBody] UpdateChannelRequest req, CancellationToken ct)
    {
        var ch = await _db.PostingChannels.FirstOrDefaultAsync(c => c.Id == id, ct);
        if (ch == null) return NotFound();
        if (req.Enabled.HasValue) ch.Enabled = req.Enabled.Value;
        if (req.NotifyPublish.HasValue) ch.NotifyPublish = req.NotifyPublish.Value;
        if (req.BufferChannelId != null) ch.BufferChannelId = req.BufferChannelId;
        if (req.PromptTemplate != null) ch.PromptTemplate = req.PromptTemplate;
        if (req.WarmrAccount != null) ch.WarmrAccount = req.WarmrAccount;
        if (!string.IsNullOrWhiteSpace(req.Format) && Enum.TryParse<SocialPostFormat>(req.Format, true, out var f)) ch.Format = f;
        if (!string.IsNullOrWhiteSpace(req.AssetKind) && Enum.TryParse<SocialAssetKind>(req.AssetKind, true, out var ak)) ch.AssetKind = ak;
        if (!string.IsNullOrWhiteSpace(req.Distribution) && Enum.TryParse<SocialDistribution>(req.Distribution, true, out var dist)) ch.Distribution = dist;
        ch.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);
        return Ok(ch);
    }

    /// <summary>Crea un canal nuevo (ej. agregar YouTube/Facebook a una app).</summary>
    [HttpPost("posting-channels")]
    public async Task<IActionResult> CreatePostingChannel([FromBody] CreateChannelRequest req, CancellationToken ct)
    {
        if (!Enum.TryParse<SocialPlatform>(req.Platform, true, out var platform))
            return BadRequest(new { error = "Platform inválida." });
        if (await _db.PostingChannels.AnyAsync(c => c.ProductKey == req.ProductKey && c.Platform == platform, ct))
            return Conflict(new { error = "Ya existe ese canal para esa app." });
        var ch = new PostingChannel
        {
            Id = Guid.NewGuid(), ProductKey = req.ProductKey, Platform = platform,
            Enabled = true,
            Format = Enum.TryParse<SocialPostFormat>(req.Format, true, out var f) ? f : SocialPostFormat.Post,
            AssetKind = Enum.TryParse<SocialAssetKind>(req.AssetKind, true, out var ak) ? ak : SocialAssetKind.Image,
            Distribution = Enum.TryParse<SocialDistribution>(req.Distribution, true, out var dist) ? dist : SocialDistribution.Buffer,
            WarmrAccount = req.WarmrAccount ?? string.Empty,
            PromptTemplate = req.PromptTemplate ?? string.Empty,
            NotifyPublish = req.NotifyPublish ?? false,
        };
        _db.PostingChannels.Add(ch);
        await _db.SaveChangesAsync(ct);
        return Ok(ch);
    }

    /// <summary>Genera 1 posteo a demanda para un canal puntual (usa su prompt propio).</summary>
    [HttpPost("posting-channels/{id:guid}/generate")]
    public async Task<IActionResult> GenerateForChannel(Guid id, CancellationToken ct)
    {
        var ch = await _db.PostingChannels.FirstOrDefaultAsync(c => c.Id == id, ct);
        if (ch == null) return NotFound();
        var profile = await _db.PostingProfiles.FirstOrDefaultAsync(p => p.ProductKey == ch.ProductKey, ct);
        if (profile == null) return NotFound(new { error = "Falta el PostingProfile de la app." });
        if (!_generator.IsConfigured) return BadRequest(new { error = "Claude no está configurado." });

        var recent = await _db.SocialPosts.Where(s => s.ProductKey == ch.ProductKey && s.Platform == ch.Platform)
            .OrderByDescending(s => s.CreatedAt).Select(s => s.Concept).Take(15).ToListAsync(ct);

        var gen = await _generator.GenerateForChannelAsync(profile, ch, recent, ct);
        if (gen == null) return StatusCode(502, new { error = "El generador no devolvió contenido." });

        var post = new SocialPost
        {
            Id = Guid.NewGuid(), ProductKey = ch.ProductKey, Platform = ch.Platform,
            BufferChannelId = ch.BufferChannelId, Format = ch.Format, AssetKind = ch.AssetKind,
            ContentPillar = gen.Pillar, Concept = gen.Concept, Prompt = gen.Prompt,
            Caption = gen.Caption, Hashtags = gen.Hashtags, GenerationModel = "claude",
            RawJson = gen.RawJson, Status = SocialPostStatus.DraftReady,
        };
        _db.SocialPosts.Add(post);
        await _db.SaveChangesAsync(ct);
        return Ok(post);
    }

    /// <summary>
    /// Genera un posteo ORIGINAL inspirado en un post de competencia que el usuario curó.
    /// Si se pasa channelId, hereda red/formato/asset/distribución de ese canal.
    /// </summary>
    [HttpPost("generate-from-inspiration")]
    public async Task<IActionResult> GenerateFromInspiration([FromBody] InspirationRequest req, CancellationToken ct)
    {
        var profile = await _db.PostingProfiles.FirstOrDefaultAsync(p => p.ProductKey == req.ProductKey, ct);
        if (profile == null) return NotFound(new { error = "No hay PostingProfile para ese producto." });
        if (!_generator.IsConfigured) return BadRequest(new { error = "Claude no está configurado (falta Claude:ApiKey)." });

        var insp = await _db.CompetitorPosts.AsNoTracking().FirstOrDefaultAsync(p => p.Id == req.CompetitorPostId, ct);
        if (insp == null) return NotFound(new { error = "No existe ese post de inspiración." });
        var comp = await _db.Competitors.AsNoTracking().FirstOrDefaultAsync(c => c.Id == insp.CompetitorId, ct);
        var sourceLabel = comp != null ? $"{comp.Platform} @{comp.Handle}" : "competencia";

        PostingChannel? ch = null;
        if (req.ChannelId.HasValue)
        {
            ch = await _db.PostingChannels.FirstOrDefaultAsync(c => c.Id == req.ChannelId.Value, ct);
            if (ch == null) return NotFound(new { error = "No existe ese canal." });
        }

        var recent = await _db.SocialPosts.Where(s => s.ProductKey == req.ProductKey)
            .OrderByDescending(s => s.CreatedAt).Select(s => s.Concept).Take(15).ToListAsync(ct);

        // Visión: bajamos la imagen del post (o el thumbnail si es video) best-effort
        // para que Claude MIRE el estilo visual además de leer el caption.
        var refImage = await DownloadImageAsync(insp.IsVideo ? insp.ThumbnailUrl : (insp.MediaUrl ?? insp.ThumbnailUrl), ct);

        var gen = await _generator.GenerateFromInspirationAsync(
            profile, ch, insp.Caption ?? "", insp.Hashtags, sourceLabel,
            refImage is null ? null : new[] { refImage }, recent, ct);
        if (gen == null) return StatusCode(502, new { error = "El generador no devolvió contenido." });

        var post = new SocialPost
        {
            Id = Guid.NewGuid(), ProductKey = req.ProductKey,
            Platform = ch?.Platform ?? SocialPlatform.Instagram,
            BufferChannelId = ch?.BufferChannelId ?? string.Empty,
            Format = Enum.TryParse<SocialPostFormat>(gen.Format, true, out var f) ? f : (ch?.Format ?? SocialPostFormat.Post),
            AssetKind = gen.AssetKind == "video" ? SocialAssetKind.Video : SocialAssetKind.Image,
            ContentPillar = gen.Pillar, Concept = gen.Concept, Prompt = gen.Prompt,
            Caption = gen.Caption, Hashtags = gen.Hashtags, GenerationModel = "claude",
            RawJson = gen.RawJson, Status = SocialPostStatus.DraftReady,
            InspirationPostId = insp.Id,
            Target = ch?.Distribution ?? SocialDistribution.Buffer,
            WarmrAccount = ch?.WarmrAccount ?? string.Empty,
        };
        _db.SocialPosts.Add(post);
        await _db.SaveChangesAsync(ct);
        return Ok(post);
    }

    /// <summary>
    /// Auto-genera el asset del posteo con IA (imagen → AiImageGenerator, video → fal.ai),
    /// lo guarda en la DB y deja el AssetUrl apuntando al endpoint público. El front lo
    /// previsualiza antes de distribuir (Buffer o cola Warmr).
    /// </summary>
    [HttpPost("{id:guid}/generate-asset")]
    public async Task<IActionResult> GenerateAsset(Guid id, CancellationToken ct)
    {
        var post = await _db.SocialPosts.FirstOrDefaultAsync(s => s.Id == id, ct);
        if (post == null) return NotFound();
        var gen = GeneratorFor(post.AssetKind);
        if (gen == null)
            return BadRequest(new { error = $"No hay generador para asset '{post.AssetKind}'." });
        if (!gen.IsConfigured)
            return BadRequest(new { error = post.AssetKind == SocialAssetKind.Video
                ? "El generador de video (fal.ai) no está configurado (falta Fal:ApiKey / FAL_KEY)."
                : "El generador de imagen no está configurado (falta ImageGen:ApiKey)." });
        var profile = await _db.PostingProfiles.FirstOrDefaultAsync(p => p.ProductKey == post.ProductKey, ct);
        if (profile == null) return NotFound(new { error = "Falta el PostingProfile de la app." });

        post.Status = SocialPostStatus.GeneratingAsset;
        post.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);

        var asset = await gen.GenerateForPostAsync(profile, post, ct);
        if (asset == null)
        {
            post.Status = SocialPostStatus.Error;
            post.Error = "No se pudo generar el asset (revisá la API key / logs del proveedor).";
            post.UpdatedAt = DateTimeOffset.UtcNow;
            await _db.SaveChangesAsync(ct);
            return StatusCode(502, new { error = post.Error });
        }

        post.AssetUrl = asset.Url;
        post.ThumbnailUrl = asset.ThumbnailUrl;
        post.Status = SocialPostStatus.DraftReady;
        post.Error = null;
        post.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);
        return Ok(post);
    }

    /// <summary>
    /// Sirve un asset generado desde la DB. Público (lo descarga Buffer y lo muestra el
    /// preview del front), por eso AllowAnonymous a diferencia del resto del controller.
    /// </summary>
    [AllowAnonymous]
    [HttpGet("assets/{id:guid}.png")]
    [HttpGet("assets/{id:guid}.mp4")]
    [HttpGet("assets/{id:guid}")]
    // Buffer hace un HEAD para verificar que la imagen es accesible antes de aceptarla;
    // sin estas rutas HEAD el endpoint devuelve 405 y Buffer rechaza el push.
    [HttpHead("assets/{id:guid}.png")]
    [HttpHead("assets/{id:guid}.mp4")]
    [HttpHead("assets/{id:guid}")]
    public async Task<IActionResult> GetAsset(Guid id, CancellationToken ct)
    {
        var asset = await _db.SocialPostAssets.AsNoTracking().FirstOrDefaultAsync(a => a.Id == id, ct);
        if (asset == null || asset.Content.Length == 0) return NotFound();
        return File(asset.Content, string.IsNullOrWhiteSpace(asset.MimeType) ? "image/png" : asset.MimeType);
    }

    /// <summary>Empuja un posteo a Buffer como DRAFT. Acepta override de assetUrl/channelId.</summary>
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
        if (req?.ScheduledAt != null) post.ScheduledAt = req.ScheduledAt;

        // asDraft=true (default) → queda como borrador en Buffer para aprobación humana.
        // asDraft=false + scheduledAt → Buffer lo auto-publica en esa fecha (agendado real).
        var asDraft = req?.AsDraft ?? true;

        // Notify Me: si el canal de esta app×red lo pide, Buffer no auto-publica sino
        // que manda push a la app móvil para terminar el post nativo (audio trending).
        var postingChannel = await _db.PostingChannels.AsNoTracking()
            .FirstOrDefaultAsync(c => c.ProductKey == post.ProductKey && c.Platform == post.Platform, ct);
        var notify = req?.NotifyPublish ?? postingChannel?.NotifyPublish ?? false;

        // Si ya había una copia en Buffer (ej. el draft que sube el worker), la borramos
        // antes de re-crear para no duplicar al reprogramar/republicar.
        if (!string.IsNullOrWhiteSpace(post.BufferPostId))
        {
            try { await _publisher.DeletePostAsync(post.BufferPostId!, ct); }
            catch (Exception ex) { _log.LogWarning(ex, "No pude borrar el post viejo de Buffer antes de re-subir"); }
            post.BufferPostId = null;
        }

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
            SaveAsDraft = asDraft,
            Automatic = !notify,
        }, ct);

        if (res.Success)
        {
            post.Status = (!asDraft && req?.ScheduledAt != null) ? SocialPostStatus.Scheduled : SocialPostStatus.PushedToBuffer;
            post.BufferPostId = res.ExternalPostId;
            post.Error = null;
        }
        else { post.Status = SocialPostStatus.Error; post.Error = res.Error; }
        post.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);
        return res.Success ? Ok(post) : BadRequest(new { error = res.Error, post });
    }

    // ── Distribución por Warmr (handoff: cola → subida manual a Cloud Drop) ──
    /// <summary>Modo de distribución Warmr (handoff vs auto) para que la UI lo muestre.</summary>
    [HttpGet("warmr/info")]
    public IActionResult WarmrInfo() => Ok(new { mode = _warmr.Mode, isConfigured = _warmr.IsConfigured });

    /// <summary>
    /// Despacha el posteo a Warmr. En handoff lo deja ReadyForWarmr: aparece en la cola
    /// con su asset + caption + cuenta + slot para que lo subas a mano a Cloud Drop.
    /// </summary>
    [HttpPost("{id:guid}/dispatch-warmr")]
    public async Task<IActionResult> DispatchWarmr(Guid id, [FromBody] DispatchWarmrRequest? req, CancellationToken ct)
    {
        var post = await _db.SocialPosts.FirstOrDefaultAsync(s => s.Id == id, ct);
        if (post == null) return NotFound();
        if (!string.IsNullOrWhiteSpace(req?.WarmrAccount)) post.WarmrAccount = req!.WarmrAccount!;
        var res = await _warmr.DispatchAsync(post, ct);
        await _db.SaveChangesAsync(ct);
        return res.Success ? Ok(post) : BadRequest(new { error = res.Error, post });
    }

    /// <summary>Cola de handoff: posteos listos para subir a Warmr Cloud Drop.</summary>
    [HttpGet("warmr/queue")]
    public async Task<IActionResult> WarmrQueue([FromQuery] string? productKey, CancellationToken ct)
    {
        var q = _db.SocialPosts.AsNoTracking().Where(s => s.Status == SocialPostStatus.ReadyForWarmr);
        if (!string.IsNullOrWhiteSpace(productKey)) q = q.Where(s => s.ProductKey == productKey);
        var posts = await q.OrderBy(s => s.ScheduledAt ?? s.CreatedAt).Take(200).ToListAsync(ct);
        return Ok(posts);
    }

    /// <summary>Marca un posteo de la cola como ya subido a Warmr (lo sacó el humano).</summary>
    [HttpPost("{id:guid}/warmr-uploaded")]
    public async Task<IActionResult> WarmrUploaded(Guid id, CancellationToken ct)
    {
        var post = await _db.SocialPosts.FirstOrDefaultAsync(s => s.Id == id, ct);
        if (post == null) return NotFound();
        post.Status = SocialPostStatus.WarmrUploaded;
        post.PostedAt = DateTimeOffset.UtcNow;
        post.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);
        return Ok(post);
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

    // ── Inspiraciones propias ("Mis ideas": WhatsApp o upload web) ──────────

    /// <summary>Lista inspiraciones propias (sin los bytes de imagen). Filtros opcionales.</summary>
    [HttpGet("inspirations")]
    public async Task<IActionResult> Inspirations([FromQuery] string? topic, [FromQuery] string? productKey, [FromQuery] bool? pending, [FromQuery] int take = 200, CancellationToken ct = default)
    {
        var q = _db.InspirationItems.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(topic)) q = q.Where(i => i.Topic == topic);
        if (!string.IsNullOrWhiteSpace(productKey)) q = q.Where(i => i.ProductKey == productKey || i.ProductKey == null);
        if (pending == true) q = q.Where(i => i.PendingTopic);
        var items = await q.OrderByDescending(i => i.CreatedAt).Take(Math.Clamp(take, 1, 500))
            .Select(i => new
            {
                i.Id, i.Topic, i.Note, i.SourceUrl, i.ProductKey, i.PendingTopic,
                HasImage = i.ImageContent != null, i.TimesUsed, i.LastUsedAt, i.CreatedAt,
            })
            .ToListAsync(ct);
        return Ok(items);
    }

    /// <summary>Alta manual desde la web (multipart: imagen opcional + tema + nota).</summary>
    [HttpPost("inspirations")]
    [RequestSizeLimit(20 * 1024 * 1024)]
    public async Task<IActionResult> CreateInspiration([FromForm] CreateInspirationRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.Topic) && req.File is null)
            return BadRequest(new { error = "Mandá al menos un tema o una imagen." });

        byte[]? bytes = null; string? mime = null;
        if (req.File is { Length: > 0 })
        {
            if (!(req.File.ContentType ?? "").StartsWith("image/", StringComparison.OrdinalIgnoreCase))
                return BadRequest(new { error = "Solo imágenes." });
            using var ms = new MemoryStream();
            await req.File.CopyToAsync(ms, ct);
            bytes = ms.ToArray(); mime = req.File.ContentType;
        }

        var item = new InspirationItem
        {
            Id = Guid.NewGuid(),
            Topic = string.IsNullOrWhiteSpace(req.Topic) ? "sin-clasificar" : req.Topic.Trim(),
            Note = string.IsNullOrWhiteSpace(req.Note) ? null : req.Note.Trim(),
            SourceUrl = string.IsNullOrWhiteSpace(req.SourceUrl) ? null : req.SourceUrl.Trim(),
            ProductKey = string.IsNullOrWhiteSpace(req.ProductKey) ? null : req.ProductKey.Trim(),
            MimeType = mime,
            ImageContent = bytes,
            SizeBytes = bytes?.Length ?? 0,
            PendingTopic = string.IsNullOrWhiteSpace(req.Topic),
        };
        _db.InspirationItems.Add(item);
        await _db.SaveChangesAsync(ct);
        return Ok(new { item.Id, item.Topic, item.PendingTopic });
    }

    /// <summary>Re-etiqueta una inspiración (tema/app/nota). Setear tema saca el pendiente.</summary>
    [HttpPatch("inspirations/{id:guid}")]
    public async Task<IActionResult> UpdateInspiration(Guid id, [FromBody] UpdateInspirationRequest req, CancellationToken ct)
    {
        var item = await _db.InspirationItems.FirstOrDefaultAsync(i => i.Id == id, ct);
        if (item == null) return NotFound();
        if (!string.IsNullOrWhiteSpace(req.Topic)) { item.Topic = req.Topic.Trim(); item.PendingTopic = false; }
        if (req.ProductKey != null) item.ProductKey = string.IsNullOrWhiteSpace(req.ProductKey) ? null : req.ProductKey.Trim();
        if (req.Note != null) item.Note = string.IsNullOrWhiteSpace(req.Note) ? null : req.Note.Trim();
        await _db.SaveChangesAsync(ct);
        return Ok(item);
    }

    [HttpDelete("inspirations/{id:guid}")]
    public async Task<IActionResult> DeleteInspiration(Guid id, CancellationToken ct)
    {
        var item = await _db.InspirationItems.FirstOrDefaultAsync(i => i.Id == id, ct);
        if (item == null) return NotFound();
        _db.InspirationItems.Remove(item);
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }

    /// <summary>Sirve la imagen de una inspiración (público, para &lt;img&gt; del front).</summary>
    [AllowAnonymous]
    [HttpGet("inspirations/{id:guid}/image")]
    [HttpHead("inspirations/{id:guid}/image")]
    public async Task<IActionResult> InspirationImage(Guid id, CancellationToken ct)
    {
        var item = await _db.InspirationItems.AsNoTracking()
            .Where(i => i.Id == id)
            .Select(i => new { i.ImageContent, i.MimeType })
            .FirstOrDefaultAsync(ct);
        if (item?.ImageContent == null || item.ImageContent.Length == 0) return NotFound();
        return File(item.ImageContent, string.IsNullOrWhiteSpace(item.MimeType) ? "image/jpeg" : item.MimeType!);
    }

    /// <summary>
    /// Genera un posteo desde inspiraciones propias. Se puede pasar una lista de IDs
    /// o directamente un tema (usa las más recientes de ese tema, hasta 5 imágenes).
    /// </summary>
    [HttpPost("generate-from-my-inspiration")]
    public async Task<IActionResult> GenerateFromMyInspiration([FromBody] MyInspirationRequest req, CancellationToken ct)
    {
        var profile = await _db.PostingProfiles.FirstOrDefaultAsync(p => p.ProductKey == req.ProductKey, ct);
        if (profile == null) return NotFound(new { error = "No hay PostingProfile para ese producto." });
        if (!_generator.IsConfigured) return BadRequest(new { error = "Claude no está configurado (falta Claude:ApiKey)." });

        List<InspirationItem> items;
        if (req.InspirationIds is { Count: > 0 })
        {
            items = await _db.InspirationItems.Where(i => req.InspirationIds.Contains(i.Id)).ToListAsync(ct);
        }
        else if (!string.IsNullOrWhiteSpace(req.Topic))
        {
            items = await _db.InspirationItems
                .Where(i => i.Topic == req.Topic && (i.ProductKey == null || i.ProductKey == req.ProductKey))
                .OrderByDescending(i => i.CreatedAt).Take(8).ToListAsync(ct);
        }
        else return BadRequest(new { error = "Pasá inspirationIds o topic." });

        if (items.Count == 0) return NotFound(new { error = "No hay inspiraciones para eso." });

        PostingChannel? ch = null;
        if (req.ChannelId.HasValue)
        {
            ch = await _db.PostingChannels.FirstOrDefaultAsync(c => c.Id == req.ChannelId.Value, ct);
            if (ch == null) return NotFound(new { error = "No existe ese canal." });
        }

        var topic = !string.IsNullOrWhiteSpace(req.Topic) ? req.Topic! : items[0].Topic;
        var notes = items.Select(i => i.Note).Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n!).ToList();
        // Hasta 5 imágenes para no inflar la llamada (Claude vision banca más, pero no hace falta).
        var images = items.Where(i => i.ImageContent is { Length: > 0 })
            .Take(5)
            .Select(i => new ClaudeImage(string.IsNullOrWhiteSpace(i.MimeType) ? "image/jpeg" : i.MimeType!, i.ImageContent!))
            .ToList();

        var recent = await _db.SocialPosts.Where(s => s.ProductKey == req.ProductKey)
            .OrderByDescending(s => s.CreatedAt).Select(s => s.Concept).Take(15).ToListAsync(ct);

        var gen = await _generator.GenerateFromOwnInspirationAsync(profile, ch, topic, notes, images, recent, ct);
        if (gen == null) return StatusCode(502, new { error = "El generador no devolvió contenido." });

        foreach (var i in items) { i.TimesUsed++; i.LastUsedAt = DateTimeOffset.UtcNow; }

        var post = new SocialPost
        {
            Id = Guid.NewGuid(), ProductKey = req.ProductKey,
            Platform = ch?.Platform ?? SocialPlatform.Instagram,
            BufferChannelId = ch?.BufferChannelId ?? string.Empty,
            Format = Enum.TryParse<SocialPostFormat>(gen.Format, true, out var f) ? f : (ch?.Format ?? SocialPostFormat.Post),
            AssetKind = gen.AssetKind == "video" ? SocialAssetKind.Video : SocialAssetKind.Image,
            ContentPillar = gen.Pillar, Concept = gen.Concept, Prompt = gen.Prompt,
            Caption = gen.Caption, Hashtags = gen.Hashtags, GenerationModel = "claude",
            RawJson = gen.RawJson, Status = SocialPostStatus.DraftReady,
            InspirationItemId = items[0].Id,
            Target = ch?.Distribution ?? SocialDistribution.Buffer,
            WarmrAccount = ch?.WarmrAccount ?? string.Empty,
        };
        _db.SocialPosts.Add(post);
        await _db.SaveChangesAsync(ct);
        return Ok(post);
    }

    // ── Config del intake por WhatsApp ──────────────────────────────────────
    [HttpGet("inspiration-settings")]
    public async Task<IActionResult> InspirationSettingsGet(CancellationToken ct)
    {
        var s = await _db.InspirationSettings.AsNoTracking().FirstOrDefaultAsync(ct);
        var instances = await _db.EvolutionInstances.AsNoTracking()
            .Include(i => i.Seller)
            .OrderBy(i => i.Seller!.DisplayName)
            .Select(i => new { i.InstanceName, PhoneNumber = i.ConnectedPhoneNumber, Label = i.Seller!.DisplayName, Status = i.Status.ToString() })
            .ToListAsync(ct);
        return Ok(new
        {
            Enabled = s?.Enabled ?? false,
            s?.InstanceName,
            s?.MasterPhone,
            Instances = instances,
        });
    }

    [HttpPut("inspiration-settings")]
    public async Task<IActionResult> InspirationSettingsPut([FromBody] UpdateInspirationSettingsRequest req, CancellationToken ct)
    {
        var s = await _db.InspirationSettings.FirstOrDefaultAsync(ct);
        if (s == null) { s = new InspirationSettings { Id = 1 }; _db.InspirationSettings.Add(s); }
        if (req.Enabled.HasValue) s.Enabled = req.Enabled.Value;
        if (req.InstanceName != null) s.InstanceName = string.IsNullOrWhiteSpace(req.InstanceName) ? null : req.InstanceName.Trim();
        if (req.MasterPhone != null) s.MasterPhone = string.IsNullOrWhiteSpace(req.MasterPhone) ? null : req.MasterPhone.Trim();
        s.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);
        InspirationIntakeRelay.InvalidateCache();
        return Ok(s);
    }

    /// <summary>Baja una imagen externa (referencia de competencia) best-effort. null si falla.</summary>
    private async Task<ClaudeImage?> DownloadImageAsync(string? url, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        try
        {
            var http = _httpFactory.CreateClient();
            http.Timeout = TimeSpan.FromSeconds(10);
            using var resp = await http.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode) return null;
            var mime = resp.Content.Headers.ContentType?.MediaType ?? "image/jpeg";
            if (!mime.StartsWith("image/", StringComparison.OrdinalIgnoreCase)) return null;
            var bytes = await resp.Content.ReadAsByteArrayAsync(ct);
            // Claude acepta jpeg/png/webp/gif; los CDNs de IG devuelven jpeg/webp.
            return bytes.Length is > 0 and < 8 * 1024 * 1024 ? new ClaudeImage(mime, bytes) : null;
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "No pude bajar la imagen de referencia {Url}", url);
            return null;
        }
    }

    public class GenerateRequest { public string ProductKey { get; set; } = string.Empty; }
    public class InspirationRequest
    {
        public string ProductKey { get; set; } = string.Empty;
        public Guid CompetitorPostId { get; set; }
        public Guid? ChannelId { get; set; }
    }
    public class DispatchWarmrRequest { public string? WarmrAccount { get; set; } }
    public class UpdateChannelRequest
    {
        public bool? Enabled { get; set; }
        public string? BufferChannelId { get; set; }
        public string? Format { get; set; }
        public string? AssetKind { get; set; }
        public string? Distribution { get; set; }
        public string? WarmrAccount { get; set; }
        public string? PromptTemplate { get; set; }
        public bool? NotifyPublish { get; set; }
    }
    public class CreateChannelRequest
    {
        public string ProductKey { get; set; } = string.Empty;
        public string Platform { get; set; } = string.Empty;
        public string? Format { get; set; }
        public string? AssetKind { get; set; }
        public string? Distribution { get; set; }
        public string? WarmrAccount { get; set; }
        public string? PromptTemplate { get; set; }
        public bool? NotifyPublish { get; set; }
    }
    public class PushRequest
    {
        public string? AssetUrl { get; set; }
        public string? ChannelId { get; set; }
        public DateTimeOffset? ScheduledAt { get; set; }
        /// <summary>true (default) = borrador en Buffer; false + ScheduledAt = agendar y auto-publicar.</summary>
        public bool? AsDraft { get; set; }
        /// <summary>Override de Notify Me; si es null se usa la config del canal (app×red).</summary>
        public bool? NotifyPublish { get; set; }
    }
    public class UpdatePostRequest
    {
        public string? Concept { get; set; }
        public string? Caption { get; set; }
        public List<string>? Hashtags { get; set; }
        public string? ContentPillar { get; set; }
        public string? Format { get; set; }
        public string? BufferChannelId { get; set; }
        /// <summary>Si es true, se aplica ScheduledAt (incluyendo null para mandarlo al backlog).</summary>
        public bool SetScheduledAt { get; set; }
        public DateTimeOffset? ScheduledAt { get; set; }
    }
    public class CreateInspirationRequest
    {
        public string? Topic { get; set; }
        public string? Note { get; set; }
        public string? SourceUrl { get; set; }
        public string? ProductKey { get; set; }
        public IFormFile? File { get; set; }
    }
    public class UpdateInspirationRequest
    {
        public string? Topic { get; set; }
        public string? ProductKey { get; set; }
        public string? Note { get; set; }
    }
    public class MyInspirationRequest
    {
        public string ProductKey { get; set; } = string.Empty;
        public List<Guid>? InspirationIds { get; set; }
        public string? Topic { get; set; }
        public Guid? ChannelId { get; set; }
    }
    public class UpdateInspirationSettingsRequest
    {
        public bool? Enabled { get; set; }
        public string? InstanceName { get; set; }
        public string? MasterPhone { get; set; }
    }
    public class UpdateProfileRequest
    {
        public bool? Enabled { get; set; }
        public string? BufferChannelsJson { get; set; }
        public List<int>? PostHours { get; set; }
        public List<int>? PostDays { get; set; }
        public int? PostsPerDay { get; set; }
        public List<string>? ContentPillars { get; set; }
        public string? BrandVoice { get; set; }
        public string? BrandGuidelines { get; set; }
    }
}
