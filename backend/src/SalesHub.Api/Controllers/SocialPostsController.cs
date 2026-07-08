using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
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
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<SocialPostsController> _log;

    public SocialPostsController(ApplicationDbContext db, SocialContentGenerator generator, IEnumerable<ISocialAssetGenerator> assetGenerators, ISocialPublisher publisher, IWarmrDistributor warmr, IHttpClientFactory httpFactory, IServiceScopeFactory scopes, ILogger<SocialPostsController> log)
    {
        _db = db; _generator = generator; _assetGenerators = assetGenerators; _publisher = publisher; _warmr = warmr; _httpFactory = httpFactory; _scopes = scopes; _log = log;
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

    /// <summary>Crea el perfil de una app nueva (ej. efcloud). Los canales se agregan con posting-channels.</summary>
    [HttpPost("profiles")]
    public async Task<IActionResult> CreateProfile([FromBody] CreateProfileRequest req, CancellationToken ct)
    {
        var key = (req.ProductKey ?? "").Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(key)) return BadRequest(new { error = "productKey requerido." });
        if (await _db.PostingProfiles.AnyAsync(p => p.ProductKey == key, ct))
            return Conflict(new { error = "Ya existe un perfil para esa app." });

        var p = new PostingProfile
        {
            Id = Guid.NewGuid(),
            ProductKey = key,
            Enabled = false, // se prende cuando la marca/canales están configurados
            BrandVoice = req.BrandVoice ?? string.Empty,
            BrandGuidelines = req.BrandGuidelines ?? string.Empty,
            TargetAudience = req.TargetAudience ?? string.Empty,
            ContentPillars = req.ContentPillars ?? new List<string>(),
            PostHours = req.PostHours ?? new List<int> { 10, 18 },
            PostsPerDay = req.PostsPerDay ?? 1,
        };
        _db.PostingProfiles.Add(p);
        await _db.SaveChangesAsync(ct);
        return Ok(p);
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
        if (req.BrandFonts != null) p.BrandFonts = req.BrandFonts;
        if (req.BrandColorsJson != null) p.BrandColorsJson = req.BrandColorsJson;
        if (req.ImageStyle != null) p.ImageStyle = req.ImageStyle;
        if (req.LandingUrl != null) p.LandingUrl = req.LandingUrl.Trim();
        p.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);
        return Ok(p);
    }

    // ── Logo de marca (se compone sobre cada imagen; la IA no dibuja logos) ─
    /// <summary>Sube el logo de la app (multipart) — se pega en cada imagen generada.</summary>
    [HttpPost("profiles/{productKey}/logo")]
    [RequestSizeLimit(8 * 1024 * 1024)]
    public async Task<IActionResult> UploadLogo(string productKey, [FromForm] IFormFile file, CancellationToken ct)
    {
        var p = await _db.PostingProfiles.FirstOrDefaultAsync(x => x.ProductKey == productKey, ct);
        if (p == null) return NotFound();
        if (file is not { Length: > 0 } || !(file.ContentType ?? "").StartsWith("image/", StringComparison.OrdinalIgnoreCase))
            return BadRequest(new { error = "Subí una imagen (PNG con transparencia idealmente)." });
        using var ms = new MemoryStream();
        await file.CopyToAsync(ms, ct);
        var id = await SaveLogoAssetAsync(ms.ToArray(), file.ContentType!, ct);
        p.BrandLogoAssetId = id; p.BrandLogoUrl = $"/api/posteos/assets/{id}.png"; p.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);
        return Ok(new { p.BrandLogoAssetId, p.BrandLogoUrl });
    }

    /// <summary>Intenta traer el logo de la landing (best-effort: apple-touch-icon/favicon/og:image).</summary>
    [HttpPost("profiles/{productKey}/logo/fetch")]
    public async Task<IActionResult> FetchLogo(string productKey, [FromServices] BrandLogoService logos, CancellationToken ct)
    {
        var p = await _db.PostingProfiles.FirstOrDefaultAsync(x => x.ProductKey == productKey, ct);
        if (p == null) return NotFound();
        if (string.IsNullOrWhiteSpace(p.LandingUrl)) return BadRequest(new { error = "El perfil no tiene LandingUrl." });
        var logo = await logos.FetchFromLandingAsync(p.LandingUrl, ct);
        if (logo is null) return StatusCode(502, new { error = "No encontré un logo en la landing. Subilo a mano." });
        var id = await SaveLogoAssetAsync(logo.Value.Bytes, logo.Value.Mime, ct);
        p.BrandLogoAssetId = id; p.BrandLogoUrl = $"/api/posteos/assets/{id}.png"; p.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);
        return Ok(new { p.BrandLogoAssetId, p.BrandLogoUrl });
    }

    /// <summary>Sube un logo de FEATURE global (key = mercadopago | whatsapp) — id fijo, se pega cuando el posteo lo menciona.</summary>
    [HttpPost("feature-logos/{key}")]
    [RequestSizeLimit(4 * 1024 * 1024)]
    public async Task<IActionResult> UploadFeatureLogo(string key, [FromForm] IFormFile file, CancellationToken ct)
    {
        var fixedId = AiImageGenerator.FeatureLogoId(key.Trim().ToLowerInvariant());
        if (fixedId == Guid.Empty) return BadRequest(new { error = "key debe ser 'mercadopago' o 'whatsapp'." });
        if (file is not { Length: > 0 } || !(file.ContentType ?? "").StartsWith("image/", StringComparison.OrdinalIgnoreCase))
            return BadRequest(new { error = "Subí una imagen PNG." });
        using var ms = new MemoryStream();
        await file.CopyToAsync(ms, ct);
        var bytes = ms.ToArray();
        var existing = await _db.SocialPostAssets.FirstOrDefaultAsync(a => a.Id == fixedId, ct);
        if (existing is null)
            _db.SocialPostAssets.Add(new SocialPostAsset { Id = fixedId, MimeType = file.ContentType!, Content = bytes, SizeBytes = bytes.Length });
        else { existing.Content = bytes; existing.MimeType = file.ContentType!; existing.SizeBytes = bytes.Length; }
        await _db.SaveChangesAsync(ct);
        return Ok(new { key, id = fixedId });
    }

    private async Task<Guid> SaveLogoAssetAsync(byte[] bytes, string mime, CancellationToken ct)
    {
        var asset = new SocialPostAsset { Id = Guid.NewGuid(), MimeType = mime, Content = bytes, SizeBytes = bytes.Length };
        _db.SocialPostAssets.Add(asset);
        await _db.SaveChangesAsync(ct);
        return asset.Id;
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
        if (req.OverlayText != null) post.OverlayText = req.OverlayText;
        if (req.NarrationText != null) post.NarrationText = req.NarrationText;
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
            ContentPillar = gen.Pillar, Concept = gen.Concept, Prompt = gen.Prompt, OverlayText = gen.Overlay, NarrationText = gen.Narration,
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
        if (req.SlideCount.HasValue) ch.SlideCount = Math.Clamp(req.SlideCount.Value, 1, 10);
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
        var fmt = Enum.TryParse<SocialPostFormat>(req.Format, true, out var f) ? f : SocialPostFormat.Post;
        if (await _db.PostingChannels.AnyAsync(c => c.ProductKey == req.ProductKey && c.Platform == platform && c.Format == fmt, ct))
            return Conflict(new { error = "Ya existe ese canal (app×red×formato)." });
        var ch = new PostingChannel
        {
            Id = Guid.NewGuid(), ProductKey = req.ProductKey, Platform = platform,
            Enabled = true,
            Format = fmt,
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

        var gen = await _generator.GenerateForChannelAsync(profile, ch, recent, null, null, ct);
        if (gen == null) return StatusCode(502, new { error = "El generador no devolvió contenido." });

        var post = new SocialPost
        {
            Id = Guid.NewGuid(), ProductKey = ch.ProductKey, Platform = ch.Platform,
            BufferChannelId = ch.BufferChannelId, Format = ch.Format, AssetKind = ch.AssetKind,
            PostType = gen.Type,
            ContentPillar = gen.Pillar, Concept = gen.Concept, Prompt = gen.Prompt, OverlayText = gen.Overlay, NarrationText = gen.Narration,
            SlidesJson = gen.Slides is { Count: > 0 } ? SocialPostSlides.FromGenerated(gen.Slides) : "[]",
            Caption = gen.Caption, Hashtags = gen.Hashtags, GenerationModel = "claude",
            RawJson = gen.RawJson, Status = SocialPostStatus.DraftReady,
        };
        _db.SocialPosts.Add(post);
        await _db.SaveChangesAsync(ct);
        return Ok(post);
    }

    /// <summary>
    /// Gauge "Posteos hoy" del dashboard admin: por app, generado vs cupo del día
    /// (acumulado por slots ya pasados — misma lógica que el catch-up del worker),
    /// con errores y estados. Sin esto, un outage del pipeline es invisible
    /// (pasó 2026-07-08: 0 posteos en todo el día y nadie se enteró hasta mirar a mano).
    /// </summary>
    [HttpGet("today-health")]
    public async Task<IActionResult> TodayHealth(CancellationToken ct)
    {
        var hour = DateTime.Now.Hour;
        var now = DateTimeOffset.Now;
        var dayStart = new DateTimeOffset(now.Year, now.Month, now.Day, 0, 0, 0, now.Offset);

        var profiles = await _db.PostingProfiles.AsNoTracking().Where(p => p.Enabled).ToListAsync(ct);
        var channels = await _db.PostingChannels.AsNoTracking().Where(c => c.Enabled).ToListAsync(ct);
        var todays = await _db.SocialPosts.AsNoTracking()
            .Where(s => s.CreatedAt >= dayStart)
            .GroupBy(s => new { s.ProductKey, s.Status })
            .Select(g => new { g.Key.ProductKey, g.Key.Status, N = g.Count() })
            .ToListAsync(ct);

        var result = profiles.OrderBy(p => p.ProductKey).Select(p =>
        {
            var chs = channels.Count(c => c.ProductKey == p.ProductKey);
            var perSlot = Math.Max(1, p.PostsPerDay) * chs;
            var slotsPassed = (p.PostHours ?? new List<int>()).Count(h => h <= hour);
            var rows = todays.Where(t => t.ProductKey == p.ProductKey).ToList();
            int C(params SocialPostStatus[] st) => rows.Where(r => st.Contains(r.Status)).Sum(r => r.N);
            return new
            {
                productKey = p.ProductKey,
                channels = chs,
                postHours = p.PostHours,
                quotaSoFar = perSlot * slotsPassed,
                dayQuota = perSlot * (p.PostHours?.Count ?? 0),
                createdToday = rows.Sum(r => r.N),
                generating = C(SocialPostStatus.Idea, SocialPostStatus.GeneratingAsset),
                drafts = C(SocialPostStatus.DraftReady),
                scheduled = C(SocialPostStatus.PushedToBuffer, SocialPostStatus.Scheduled,
                              SocialPostStatus.ReadyForWarmr, SocialPostStatus.WarmrUploaded),
                posted = C(SocialPostStatus.Posted),
                errors = C(SocialPostStatus.Error),
            };
        });
        return Ok(result);
    }

    /// <summary>Todos los canales de todas las apps (para el multi-select de "Publicar YA").</summary>
    [HttpGet("all-channels")]
    public async Task<IActionResult> AllChannels(CancellationToken ct)
    {
        var channels = await _db.PostingChannels.AsNoTracking()
            .OrderBy(c => c.ProductKey).ThenBy(c => c.Platform)
            .Select(c => new
            {
                c.Id, c.ProductKey,
                Platform = c.Platform.ToString(),
                c.Enabled,
                AssetKind = c.AssetKind.ToString(),
                Format = c.Format.ToString(),
                Distribution = c.Distribution.ToString(),
                HasBuffer = c.BufferChannelId != "",
            })
            .ToListAsync(ct);
        return Ok(channels);
    }

    /// <summary>Catálogo de tipos de posteo (para el selector de la UI).</summary>
    [HttpGet("content-types")]
    public IActionResult ContentTypesList() =>
        Ok(ContentTypes.All.Select(t => new { key = t.Key, label = t.Label, weight = t.Weight }));

    /// <summary>
    /// Re-destila la landing de una app (baja la URL, Claude extrae features/precios reales)
    /// y cachea la ficha. Se llama al guardar la URL o con el botón "actualizar".
    /// </summary>
    [HttpPost("profiles/{productKey}/refresh-landing")]
    public async Task<IActionResult> RefreshLanding(string productKey, [FromServices] LandingKnowledgeService landing, CancellationToken ct)
    {
        var p = await _db.PostingProfiles.FirstOrDefaultAsync(x => x.ProductKey == productKey, ct);
        if (p == null) return NotFound();
        if (string.IsNullOrWhiteSpace(p.LandingUrl)) return BadRequest(new { error = "El perfil no tiene LandingUrl." });
        var knowledge = await landing.EnsureFreshAsync(p, force: true, ct);
        await _db.SaveChangesAsync(ct);
        return Ok(new { p.LandingUrl, p.LandingKnowledgeAt, landingKnowledge = knowledge });
    }

    /// <summary>
    /// "Publicar YA": por cada canal seleccionado genera contenido + asset y lo publica
    /// INMEDIATAMENTE (shareNow, no agendado). Crea las filas al toque en estado
    /// GeneratingAsset y termina el trabajo pesado (Claude → imagen/video → Buffer) en
    /// background; la UI las ve avanzar por su estado.
    /// </summary>
    [HttpPost("publish-now")]
    public async Task<IActionResult> PublishNow([FromBody] PublishNowRequest req, CancellationToken ct)
    {
        if (req.ChannelIds is not { Count: > 0 }) return BadRequest(new { error = "Elegí al menos un canal." });
        var channels = await _db.PostingChannels.Where(c => req.ChannelIds.Contains(c.Id)).ToListAsync(ct);
        if (channels.Count == 0) return NotFound(new { error = "No se encontraron esos canales." });

        var created = new List<object>();
        foreach (var ch in channels)
        {
            // Fila placeholder inmediata → la UI la muestra ya "Generando…".
            var post = new SocialPost
            {
                Id = Guid.NewGuid(), ProductKey = ch.ProductKey, Platform = ch.Platform,
                BufferChannelId = ch.BufferChannelId, Format = ch.Format, AssetKind = ch.AssetKind,
                Target = ch.Distribution, WarmrAccount = ch.WarmrAccount,
                Concept = "Generando…", GenerationModel = "claude",
                Status = SocialPostStatus.GeneratingAsset,
            };
            _db.SocialPosts.Add(post);
            created.Add(new { postId = post.Id, ch.ProductKey, platform = ch.Platform.ToString() });
            var channelId = ch.Id; var postId = post.Id; var hint = req.Topic; var typeKey = req.PostType;
            _ = Task.Run(() => RunPublishNowAsync(channelId, postId, hint, typeKey));
        }
        await _db.SaveChangesAsync(ct);
        return Accepted(new { started = created.Count, posts = created });
    }

    /// <summary>Trabajo pesado de "Publicar YA" para un canal: genera contenido + asset y publica ya.</summary>
    private async Task RunPublishNowAsync(Guid channelId, Guid postId, string? hint, string? typeKey)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var generator = scope.ServiceProvider.GetRequiredService<SocialContentGenerator>();
        var landing = scope.ServiceProvider.GetRequiredService<LandingKnowledgeService>();
        var assetGens = scope.ServiceProvider.GetServices<ISocialAssetGenerator>();
        var publisher = scope.ServiceProvider.GetRequiredService<ISocialPublisher>();
        var warmr = scope.ServiceProvider.GetRequiredService<IWarmrDistributor>();
        var log = scope.ServiceProvider.GetRequiredService<ILogger<SocialPostsController>>();
        var ct = CancellationToken.None;

        var post = await db.SocialPosts.FirstOrDefaultAsync(s => s.Id == postId, ct);
        if (post is null) return;
        try
        {
            var ch = await db.PostingChannels.FirstOrDefaultAsync(c => c.Id == channelId, ct);
            var profile = ch is null ? null : await db.PostingProfiles.FirstOrDefaultAsync(p => p.ProductKey == ch.ProductKey, ct);
            if (ch is null || profile is null) { post.Status = SocialPostStatus.Error; post.Error = "Falta canal o perfil."; await db.SaveChangesAsync(ct); return; }

            try { await landing.EnsureFreshAsync(profile, force: false, ct); await db.SaveChangesAsync(ct); }
            catch (Exception ex) { log.LogWarning(ex, "Publicar YA: no pude refrescar la landing de {Product}", profile.ProductKey); }

            var recentPosts = await db.SocialPosts.Where(s => s.ProductKey == ch.ProductKey && s.Platform == ch.Platform && s.Id != postId)
                .OrderByDescending(s => s.CreatedAt).Select(s => new { s.Concept, s.PostType }).Take(15).ToListAsync(ct);
            var recent = recentPosts.Select(x => x.Concept).ToList();

            // Tipo: si el usuario lo forzó, ese; si no, elegimos evitando el último.
            var resolvedType = typeKey;
            if (string.IsNullOrWhiteSpace(resolvedType))
            {
                var hasPrices = !string.IsNullOrWhiteSpace(profile.LandingKnowledge)
                    && profile.LandingKnowledge.IndexOf("sin precios", StringComparison.OrdinalIgnoreCase) < 0;
                resolvedType = ContentTypes.Pick(recentPosts.FirstOrDefault()?.PostType, hasPrices, Random.Shared).Key;
            }

            var gen = await generator.GenerateForChannelAsync(profile, ch, recent, hint, resolvedType, ct);
            if (gen is null) { post.Status = SocialPostStatus.Error; post.Error = "El generador no devolvió contenido."; await db.SaveChangesAsync(ct); return; }

            post.PostType = gen.Type;
            post.ContentPillar = gen.Pillar; post.Concept = gen.Concept; post.Prompt = gen.Prompt;
            post.OverlayText = gen.Overlay; post.NarrationText = gen.Narration;
            post.SlidesJson = gen.Slides is { Count: > 0 } ? SocialPostSlides.FromGenerated(gen.Slides) : "[]";
            post.Caption = gen.Caption; post.Hashtags = gen.Hashtags; post.RawJson = gen.RawJson;
            post.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);

            // Asset: multi-slide (carrusel/combo) → N imágenes; si no, el generador único.
            if (SocialPostSlides.HasSlides(post.SlidesJson))
            {
                var img = scope.ServiceProvider.GetRequiredService<AiImageGenerator>();
                var slides = await img.GenerateSlidesForPostAsync(profile, post, SocialPostSlides.Parse(post.SlidesJson), ct);
                post.SlidesJson = SocialPostSlides.Serialize(slides);
                post.AssetUrl = slides.FirstOrDefault(s => s.AssetUrl != null)?.AssetUrl;
            }
            else
            {
                var assetGen = assetGens.FirstOrDefault(g => g.CanHandle(gen.AssetKind));
                if (assetGen is not null && assetGen.IsConfigured)
                {
                    var asset = await assetGen.GenerateForPostAsync(profile, post, ct);
                    if (asset is not null) { post.AssetUrl = asset.Url; post.ThumbnailUrl = asset.ThumbnailUrl; }
                }
            }
            post.Status = SocialPostStatus.DraftReady;
            await db.SaveChangesAsync(ct);

            // Distribución INMEDIATA.
            if (ch.Distribution == SocialDistribution.Warmr)
            {
                await warmr.DispatchAsync(post, ct);
                await db.SaveChangesAsync(ct);
                return;
            }

            if (string.IsNullOrWhiteSpace(post.AssetUrl))
            {
                post.Status = SocialPostStatus.Error;
                post.Error = "No se generó el asset (revisá las API keys de imagen/video).";
                await db.SaveChangesAsync(ct);
                return;
            }

            var caption = post.Hashtags.Count > 0
                ? $"{post.Caption}\n\n{string.Join(" ", post.Hashtags.Select(h => h.StartsWith('#') ? h : "#" + h))}"
                : post.Caption;
            var slideUrls = SocialPostSlides.AssetUrls(post.SlidesJson);
            var res = await publisher.CreatePostAsync(new PublishRequest
            {
                ChannelId = post.BufferChannelId,
                Service = post.Platform.ToString().ToLowerInvariant(),
                Caption = caption,
                ImageUrl = post.AssetKind == SocialAssetKind.Image && slideUrls.Count == 0 ? post.AssetUrl : null,
                ImageUrls = slideUrls.Count > 0 ? slideUrls : null,
                VideoUrl = post.AssetKind == SocialAssetKind.Video ? post.AssetUrl : null,
                ThumbnailUrl = post.ThumbnailUrl,
                InstagramType = post.Format.ToString().ToLowerInvariant(),
                ScheduledAt = null,          // shareNow
                SaveAsDraft = false,
                Automatic = !ch.NotifyPublish,
            }, ct);

            if (res.Success)
            {
                post.Status = SocialPostStatus.Posted;
                post.BufferPostId = res.ExternalPostId;
                post.PostedAt = DateTimeOffset.UtcNow;
                post.Error = null;
            }
            else { post.Status = SocialPostStatus.Error; post.Error = res.Error; }
            post.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
            log.LogInformation("Publicar YA {Product}/{Net}: {Status}", post.ProductKey, post.Platform, post.Status);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Publicar YA falló (post {Id})", postId);
            try { post.Status = SocialPostStatus.Error; post.Error = ex.Message.Length > 500 ? ex.Message[..500] : ex.Message; await db.SaveChangesAsync(ct); }
            catch { /* no-op */ }
        }
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
            ContentPillar = gen.Pillar, Concept = gen.Concept, Prompt = gen.Prompt, OverlayText = gen.Overlay, NarrationText = gen.Narration,
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
        var profile = await _db.PostingProfiles.FirstOrDefaultAsync(p => p.ProductKey == post.ProductKey, ct);
        if (profile == null) return NotFound(new { error = "Falta el PostingProfile de la app." });

        post.Status = SocialPostStatus.GeneratingAsset;
        post.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);

        // Multi-slide (carrusel/combo) → N imágenes; si no, el generador único.
        if (SocialPostSlides.HasSlides(post.SlidesJson))
        {
            var img = HttpContext.RequestServices.GetRequiredService<AiImageGenerator>();
            var slides = await img.GenerateSlidesForPostAsync(profile, post, SocialPostSlides.Parse(post.SlidesJson), ct);
            post.SlidesJson = SocialPostSlides.Serialize(slides);
            post.AssetUrl = slides.FirstOrDefault(s => s.AssetUrl != null)?.AssetUrl;
            if (post.AssetUrl == null) { post.Status = SocialPostStatus.Error; post.Error = "No se generaron las imágenes de las slides."; await _db.SaveChangesAsync(ct); return StatusCode(502, new { error = post.Error }); }
            post.Status = SocialPostStatus.DraftReady; post.Error = null; post.UpdatedAt = DateTimeOffset.UtcNow;
            await _db.SaveChangesAsync(ct);
            return Ok(post);
        }

        var gen = GeneratorFor(post.AssetKind);
        if (gen == null)
            return BadRequest(new { error = $"No hay generador para asset '{post.AssetKind}'." });
        if (!gen.IsConfigured)
            return BadRequest(new { error = post.AssetKind == SocialAssetKind.Video
                ? "El generador de video (fal.ai) no está configurado (falta Fal:ApiKey / FAL_KEY)."
                : "El generador de imagen no está configurado (falta ImageGen:ApiKey)." });

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

        var slideUrls = SocialPostSlides.AssetUrls(post.SlidesJson);
        var res = await _publisher.CreatePostAsync(new PublishRequest
        {
            ChannelId = channelId,
            Service = post.Platform.ToString().ToLowerInvariant(),
            Caption = post.Hashtags.Count > 0 ? $"{post.Caption}\n\n{string.Join(" ", post.Hashtags.Select(h => h.StartsWith('#') ? h : "#" + h))}" : post.Caption,
            ImageUrl = post.AssetKind == SocialAssetKind.Image && slideUrls.Count == 0 ? assetUrl : null,
            ImageUrls = slideUrls.Count > 0 ? slideUrls : null,
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
            ContentPillar = gen.Pillar, Concept = gen.Concept, Prompt = gen.Prompt, OverlayText = gen.Overlay, NarrationText = gen.Narration,
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
    public class PublishNowRequest
    {
        /// <summary>Canales (app×red) a los que publicar YA.</summary>
        public List<Guid> ChannelIds { get; set; } = new();
        /// <summary>Tema/indicación opcional para el contenido (ej. "promo de verano").</summary>
        public string? Topic { get; set; }
        /// <summary>Tipo de posteo forzado (emocional, educativo, venta, precio…). Vacío = elige el sistema.</summary>
        public string? PostType { get; set; }
    }
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
        /// <summary>Slides del posteo (1 = simple; >1 = carrusel/combo).</summary>
        public int? SlideCount { get; set; }
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
        public string? OverlayText { get; set; }
        public string? NarrationText { get; set; }
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
        /// <summary>Tipografía de marca que la imagen debe respetar (ej. "Bricolage Grotesque, bold condensed").</summary>
        public string? BrandFonts { get; set; }
        /// <summary>Paleta JSON {"primary":"#..."} — antes no era editable por API.</summary>
        public string? BrandColorsJson { get; set; }
        /// <summary>Dirección de arte del prompt de imagen/video de esta app (va tal cual al modelo).</summary>
        public string? ImageStyle { get; set; }
        /// <summary>URL de la landing (de acá se destilan features/precios reales).</summary>
        public string? LandingUrl { get; set; }
    }
    public class CreateProfileRequest
    {
        public string ProductKey { get; set; } = string.Empty;
        public string? BrandVoice { get; set; }
        public string? BrandGuidelines { get; set; }
        public string? TargetAudience { get; set; }
        public List<string>? ContentPillars { get; set; }
        public List<int>? PostHours { get; set; }
        public int? PostsPerDay { get; set; }
    }
}
