using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SalesHub.Core.Abstractions;
using SalesHub.Core.Domain.Entities.Social;
using SalesHub.Infrastructure.Options;
using SalesHub.Infrastructure.Persistence;

namespace SalesHub.Infrastructure.Services.Social;

/// <summary>
/// Genera la imagen del posteo con un modelo de IA texto→imagen (GPT Image por
/// default, Grok como alternativa — ambos OpenAI-compatibles). Guarda el PNG en
/// la DB (SocialPostAsset) y devuelve la URL pública /api/posteos/assets/{id}.png
/// que consume Buffer. Proveedor swappable por ImageGen__Provider.
/// </summary>
public class AiImageGenerator : ISocialAssetGenerator
{
    private readonly HttpClient _http;
    private readonly ImageGenOptions _opts;
    private readonly ApplicationDbContext _db;
    private readonly BrandLogoService _logos;
    private readonly ILogger<AiImageGenerator> _log;

    public AiImageGenerator(HttpClient http, IOptions<ImageGenOptions> opts, ApplicationDbContext db, BrandLogoService logos, ILogger<AiImageGenerator> log)
    {
        _http = http;
        _opts = opts.Value;
        _db = db;
        _logos = logos;
        _log = log;
        _http.BaseAddress = new Uri(_opts.ResolveBaseUrl().TrimEnd('/') + "/");
        _http.Timeout = TimeSpan.FromSeconds(_opts.TimeoutSeconds);
        if (!string.IsNullOrWhiteSpace(_opts.ApiKey))
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _opts.ApiKey);
    }

    public bool IsConfigured => IsPollinations || !string.IsNullOrWhiteSpace(_opts.ApiKey);

    /// <summary>Pollinations es gratis y sin API key (texto→imagen por URL GET).</summary>
    private bool IsPollinations => string.Equals(_opts.Provider?.Trim(), "pollinations", StringComparison.OrdinalIgnoreCase);

    public bool CanHandle(string assetKind) =>
        string.Equals(assetKind?.Trim(), "image", StringComparison.OrdinalIgnoreCase);

    /// <summary>Camino genérico de la interfaz (worker viejo / fallback): usa el prompt tal cual.</summary>
    public async Task<AssetResult?> GenerateAsync(string prompt, string assetKind, CancellationToken ct = default)
    {
        if (!CanHandle(assetKind)) return null;
        var bytes = await GenerateBytesAsync(prompt, null, ct);
        if (bytes == null) return null;
        return await PersistAsync(bytes, null, ct);
    }

    /// <summary>
    /// Camino rico (controller + worker): arma un prompt de marca a partir del
    /// concepto/visual de Claude + la paleta/voz del perfil, y liga el asset al posteo.
    /// </summary>
    public async Task<AssetResult?> GenerateForPostAsync(PostingProfile profile, SocialPost post, CancellationToken ct = default)
    {
        if (post.AssetKind != SocialAssetKind.Image) return null;
        var prompt = BuildBrandPrompt(profile, post);
        var bytes = await GenerateBytesAsync(prompt, post.Format, ct);
        if (bytes == null) return null;
        // Pegamos los logos REALES (la IA no los dibuja): el de marca siempre, y los de
        // feature (MercadoPago/WhatsApp) solo si el posteo los menciona de verdad.
        bytes = await ApplyLogosAsync(profile, post, bytes, ct);
        return await PersistAsync(bytes, post.Id, ct);
    }

    /// <summary>
    /// Genera la imagen de CADA slide (carrusel/combo) con su propio prompt+overlay, aplica
    /// logos, persiste cada una como asset y devuelve las slides con AssetUrl completo.
    /// </summary>
    public async Task<List<PostSlide>> GenerateSlidesForPostAsync(PostingProfile profile, SocialPost post, IReadOnlyList<PostSlide> slides, CancellationToken ct = default)
    {
        var done = new List<PostSlide>();
        foreach (var slide in slides.OrderBy(s => s.Order))
        {
            var prompt = BuildBrandPrompt(profile, post.Format, post.Platform, slide.Prompt, slide.Concept, slide.Overlay);
            var bytes = await GenerateBytesAsync(prompt, post.Format, ct);
            if (bytes is null) { _log.LogWarning("Slide {Order} de {Post} sin imagen", slide.Order, post.Id); done.Add(slide); continue; }
            // logos: marca siempre; feature según el texto de ESTA slide + el caption del posteo.
            bytes = await ApplyLogosForTextAsync(profile, $"{slide.Overlay} {post.Caption}", bytes, ct);
            var asset = await PersistAsync(bytes, post.Id, ct);
            slide.AssetUrl = asset.Url;
            done.Add(slide);
        }
        return done;
    }

    /// <summary>Compone el logo de marca + logos de feature (contextuales) sobre la imagen.</summary>
    private Task<byte[]> ApplyLogosAsync(PostingProfile profile, SocialPost post, byte[] bytes, CancellationToken ct)
        => ApplyLogosForTextAsync(profile, $"{post.Caption} {post.Concept} {post.OverlayText}", bytes, ct);

    private async Task<byte[]> ApplyLogosForTextAsync(PostingProfile profile, string contextText, byte[] bytes, CancellationToken ct)
    {
        try
        {
            byte[]? brand = null;
            if (profile.BrandLogoAssetId is { } logoId)
                brand = await _db.SocialPostAssets.AsNoTracking()
                    .Where(a => a.Id == logoId).Select(a => a.Content).FirstOrDefaultAsync(ct);

            var text = (contextText ?? "").ToLowerInvariant();
            var featureIds = new List<Guid>();
            if (text.Contains("mercadopago") || text.Contains("mercado pago")) AddFeature(featureIds, "mercadopago");
            if (text.Contains("whatsapp") || text.Contains("bot")) AddFeature(featureIds, "whatsapp");

            var featureLogos = new List<byte[]>();
            foreach (var fid in featureIds)
            {
                var b = await _db.SocialPostAssets.AsNoTracking().Where(a => a.Id == fid).Select(a => a.Content).FirstOrDefaultAsync(ct);
                if (b is { Length: > 0 }) featureLogos.Add(b);
            }

            if (brand is null && featureLogos.Count == 0) return bytes;
            return await _logos.CompositeAsync(bytes, brand, featureLogos, ct);
        }
        catch (Exception ex) { _log.LogWarning(ex, "No pude aplicar logos"); return bytes; }
    }

    // Los logos de feature (globales) se guardan como SocialPostAssets con un id fijo por clave.
    private static readonly Dictionary<string, Guid> FeatureLogoIds = new()
    {
        ["mercadopago"] = Guid.Parse("00000000-0000-0000-0000-0000000000a1"),
        ["whatsapp"] = Guid.Parse("00000000-0000-0000-0000-0000000000a2"),
    };
    private static void AddFeature(List<Guid> ids, string key)
    {
        if (FeatureLogoIds.TryGetValue(key, out var id)) ids.Add(id);
    }
    public static Guid FeatureLogoId(string key) => FeatureLogoIds.TryGetValue(key, out var id) ? id : Guid.Empty;

    // ── Prompt de marca ────────────────────────────────────────────────────
    private static string BuildBrandPrompt(PostingProfile profile, SocialPost post)
        => BuildBrandPrompt(profile, post.Format, post.Platform, post.Prompt, post.Concept, post.OverlayText);

    /// <summary>Arma el prompt de marca a partir de un visual+overlay explícitos (sirve para el post o para una slide).</summary>
    private static string BuildBrandPrompt(PostingProfile profile, SocialPostFormat format, SocialPlatform platform, string visualPrompt, string concept, string overlayText)
    {
        var sb = new StringBuilder();
        // Base visual: el prompt cinematográfico en inglés que ya generó Claude.
        sb.Append(string.IsNullOrWhiteSpace(visualPrompt) ? concept : visualPrompt);
        var aspect = format switch
        {
            SocialPostFormat.Story or SocialPostFormat.Reel => "vertical 9:16 full-bleed composition",
            SocialPostFormat.Carousel => "vertical 4:5 composition",
            _ => "square 1:1 composition",
        };
        sb.Append($". Social media {format.ToString().ToLowerInvariant()} for {platform}, {aspect}, clean and modern.");
        // Ancla cultural: si aparece gente/lugares, que se lean latinoamericanos, no asiáticos ni stock USA.
        sb.Append(" If people or places appear, they must read as Latin American / Argentine (latino features, Spanish-language signage), never Asian or generic US stock imagery.");
        if (!string.IsNullOrWhiteSpace(profile.BrandColorsJson) && profile.BrandColorsJson.Trim() != "{}")
            sb.Append($" Use this brand color palette (exact hex): {profile.BrandColorsJson}.");
        // Texto en la imagen: SOLO el overlay corto que Claude escribió como copy.
        // Nunca el concepto (descripción interna larga → el modelo lo trunca y lo rompe).
        var overlay = overlayText?.Trim() ?? string.Empty;
        if (overlay.Length > 0)
        {
            sb.Append($" Render ONLY this exact short headline text, nothing else written: \"{overlay}\" — bold, perfectly spelled, legible, well integrated into the layout.");
            // La tipografía SOLO importa si hay texto — y tiene que ser la de la marca,
            // no el lettering genérico "de IA" que meten los modelos por default.
            if (!string.IsNullOrWhiteSpace(profile.BrandFonts))
                sb.Append($" The headline typography MUST match this exact typeface style: {profile.BrandFonts}. Reproduce that font's look faithfully (letterforms, weight, spacing) — do NOT use generic AI lettering, decorative fonts, or any other typeface.");
        }
        else
        {
            sb.Append(" Absolutely NO text, no words, no letters, no captions anywhere in the image.");
        }
        sb.Append(" Professional marketing quality, on-brand, no watermark, no logos of other companies.");
        return sb.ToString();
    }

    // ── Llamada al proveedor ───────────────────────────────────────────────
    private async Task<byte[]?> GenerateBytesAsync(string prompt, SocialPostFormat? format, CancellationToken ct)
    {
        // Pollinations: gratis, sin API key, texto→imagen por GET. Dimensiones según formato.
        if (IsPollinations) return await GeneratePollinationsAsync(prompt, format, ct);

        if (string.IsNullOrWhiteSpace(_opts.ApiKey)) { _log.LogWarning("ImageGen ApiKey no configurada — no se genera imagen"); return null; }

        var provider = _opts.Provider.Trim().ToLowerInvariant();
        // gpt-image acepta 1024x1024 | 1024x1536 (vertical) | 1536x1024. Para Story/Reel/Carousel
        // pedimos vertical (mejor para IG); para el resto, lo que diga la config (default cuadrado).
        var size = format is SocialPostFormat.Story or SocialPostFormat.Reel or SocialPostFormat.Carousel
            ? "1024x1536"
            : _opts.Size;
        object body = provider switch
        {
            // Grok (xAI): OpenAI-compatible pero sin size/quality; pedimos b64.
            "grok" => new { model = _opts.Model, prompt, n = 1, response_format = "b64_json" },
            // OpenAI GPT Image: acepta size + quality; devuelve b64_json siempre.
            _ => new { model = _opts.Model, prompt, size, quality = _opts.Quality, n = 1 },
        };

        try
        {
            var resp = await _http.PostAsJsonAsync("v1/images/generations", body, ct);
            var raw = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
            {
                _log.LogWarning("ImageGen {Provider} falló: {Status} {Body}", provider, resp.StatusCode,
                    raw[..Math.Min(raw.Length, 500)]);
                return null;
            }

            using var doc = JsonDocument.Parse(raw);
            if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array || data.GetArrayLength() == 0)
            {
                _log.LogWarning("ImageGen {Provider}: respuesta sin data", provider);
                return null;
            }
            var first = data[0];
            if (first.TryGetProperty("b64_json", out var b64) && b64.ValueKind == JsonValueKind.String)
                return Convert.FromBase64String(b64.GetString()!);
            if (first.TryGetProperty("url", out var url) && url.ValueKind == JsonValueKind.String)
                return await _http.GetByteArrayAsync(url.GetString()!, ct);

            _log.LogWarning("ImageGen {Provider}: data[0] sin b64_json ni url", provider);
            return null;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "ImageGen {Provider} excepción", provider);
            return null;
        }
    }

    /// <summary>
    /// Pollinations (flux): GET https://image.pollinations.ai/prompt/{prompt}?width&height&seed.
    /// No requiere API key. La dimensión sale del formato del posteo.
    /// </summary>
    private async Task<byte[]?> GeneratePollinationsAsync(string prompt, SocialPostFormat? format, CancellationToken ct)
    {
        var (w, h) = DimsFor(format);
        var seed = Random.Shared.Next(1, int.MaxValue);
        var url = "https://image.pollinations.ai/prompt/" + Uri.EscapeDataString(prompt)
            + $"?width={w}&height={h}&seed={seed}&nologo=true&enhance=true&model=flux";
        try
        {
            var bytes = await _http.GetByteArrayAsync(url, ct);
            if (bytes is { Length: > 0 }) return bytes;
            _log.LogWarning("Pollinations devolvió 0 bytes");
            return null;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Pollinations excepción");
            return null;
        }
    }

    /// <summary>Dimensiones IG por formato (px).</summary>
    private static (int w, int h) DimsFor(SocialPostFormat? format) => format switch
    {
        SocialPostFormat.Story or SocialPostFormat.Reel => (1080, 1920),
        SocialPostFormat.Carousel => (1080, 1350),
        _ => (1080, 1080),
    };

    // ── Persistencia ───────────────────────────────────────────────────────
    private async Task<AssetResult> PersistAsync(byte[] bytes, Guid? postId, CancellationToken ct)
    {
        var asset = new SocialPostAsset
        {
            Id = Guid.NewGuid(),
            SocialPostId = postId,
            MimeType = "image/png",
            Content = bytes,
            SizeBytes = bytes.LongLength,
        };
        _db.SocialPostAssets.Add(asset);
        await _db.SaveChangesAsync(ct);
        var publicUrl = $"{_opts.PublicBaseUrl.TrimEnd('/')}/api/posteos/assets/{asset.Id}.png";
        return new AssetResult(publicUrl, publicUrl);
    }
}
