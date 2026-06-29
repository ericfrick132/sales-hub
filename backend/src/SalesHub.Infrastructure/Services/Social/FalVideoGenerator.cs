using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SalesHub.Core.Abstractions;
using SalesHub.Core.Domain.Entities.Social;
using SalesHub.Infrastructure.Options;
using SalesHub.Infrastructure.Persistence;

namespace SalesHub.Infrastructure.Services.Social;

/// <summary>
/// Genera el VIDEO del posteo con fal.ai (texto→video, API key, headless → corre en el
/// droplet). Usa la Queue API: submit job → poll status → get response → descarga el mp4
/// y lo persiste como SocialPostAsset, servido en /api/posteos/assets/{id}.mp4.
///
/// Es un <see cref="ISocialAssetGenerator"/> más: el worker/controller lo resuelven por
/// CanHandle("video"). El prompt "pulido" lo arma Claude (SocialContentGenerator); acá
/// solo lo envolvemos con marca + aspect.
/// </summary>
public class FalVideoGenerator : ISocialAssetGenerator
{
    private readonly HttpClient _http;
    private readonly FalOptions _opts;
    private readonly ApplicationDbContext _db;
    private readonly ILogger<FalVideoGenerator> _log;

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    public FalVideoGenerator(HttpClient http, IOptions<FalOptions> opts, ApplicationDbContext db, ILogger<FalVideoGenerator> log)
    {
        _http = http;
        _opts = opts.Value;
        _db = db;
        _log = log;
        _http.Timeout = TimeSpan.FromSeconds(Math.Max(60, _opts.TimeoutSeconds));
        if (!string.IsNullOrWhiteSpace(_opts.ApiKey))
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Key", _opts.ApiKey);
    }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_opts.ApiKey);

    public bool CanHandle(string assetKind) =>
        string.Equals(assetKind?.Trim(), "video", StringComparison.OrdinalIgnoreCase);

    public async Task<AssetResult?> GenerateAsync(string prompt, string assetKind, CancellationToken ct = default)
    {
        if (!CanHandle(assetKind)) return null;
        var url = await GenerateVideoUrlAsync(prompt, "9:16", ct);
        return url == null ? null : await PersistAsync(url, null, ct);
    }

    public async Task<AssetResult?> GenerateForPostAsync(PostingProfile profile, SocialPost post, CancellationToken ct = default)
    {
        if (post.AssetKind != SocialAssetKind.Video) return null;
        var prompt = BuildBrandPrompt(profile, post);
        var aspect = post.Format switch
        {
            SocialPostFormat.Story or SocialPostFormat.Reel or SocialPostFormat.Video => "9:16",
            SocialPostFormat.Carousel => "3:4",
            _ => "16:9",
        };
        var url = await GenerateVideoUrlAsync(prompt, aspect, ct);
        return url == null ? null : await PersistAsync(url, post.Id, ct);
    }

    private static string BuildBrandPrompt(PostingProfile profile, SocialPost post)
    {
        var sb = new StringBuilder();
        sb.Append(string.IsNullOrWhiteSpace(post.Prompt) ? post.Concept : post.Prompt);
        sb.Append(". Short-form social video, dynamic, modern, eye-catching motion.");
        if (!string.IsNullOrWhiteSpace(profile.BrandColorsJson) && profile.BrandColorsJson.Trim() != "{}")
            sb.Append($" Brand color palette (exact hex): {profile.BrandColorsJson}.");
        if (!string.IsNullOrWhiteSpace(profile.BrandVoice))
            sb.Append($" Tone: {profile.BrandVoice}.");
        sb.Append(" Professional marketing quality, on-brand, no watermark.");
        return sb.ToString();
    }

    // ── fal.ai Queue API: submit → poll → response ─────────────────────────
    private async Task<string?> GenerateVideoUrlAsync(string prompt, string aspect, CancellationToken ct)
    {
        if (!IsConfigured) { _log.LogWarning("fal.ai ApiKey no configurada — no se genera video"); return null; }

        var model = _opts.VideoModel.Trim('/');
        var submitUrl = $"{_opts.BaseUrl.TrimEnd('/')}/{model}";
        object body = new
        {
            prompt,
            aspect_ratio = aspect,
            resolution = _opts.Resolution,
            duration = _opts.DurationSeconds,
        };

        string statusUrl, responseUrl;
        try
        {
            var resp = await _http.PostAsJsonAsync(submitUrl, body, Json, ct);
            var raw = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
            {
                _log.LogWarning("fal.ai submit falló ({Model}): {Status} {Body}", model, resp.StatusCode, Trunc(raw, 500));
                return null;
            }
            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;
            // Algunos modelos cortos responden sync con el output directo.
            var direct = TryExtractVideoUrl(root);
            if (direct != null) return direct;

            statusUrl = Str(root, "status_url");
            responseUrl = Str(root, "response_url");
            if (string.IsNullOrWhiteSpace(statusUrl) || string.IsNullOrWhiteSpace(responseUrl))
            {
                var id = Str(root, "request_id");
                if (string.IsNullOrWhiteSpace(id)) { _log.LogWarning("fal.ai submit sin request_id: {Body}", Trunc(raw, 400)); return null; }
                statusUrl = $"{submitUrl}/requests/{id}/status";
                responseUrl = $"{submitUrl}/requests/{id}/response";
            }
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "fal.ai submit excepción");
            return null;
        }

        // Poll status hasta COMPLETED.
        for (var attempt = 0; attempt < _opts.MaxPollAttempts; attempt++)
        {
            await Task.Delay(TimeSpan.FromSeconds(_opts.PollSeconds), ct);
            string status;
            try
            {
                var resp = await _http.GetAsync(statusUrl, ct);
                var raw = await resp.Content.ReadAsStringAsync(ct);
                if (!resp.IsSuccessStatusCode) { _log.LogWarning("fal.ai status {Status}", resp.StatusCode); continue; }
                using var doc = JsonDocument.Parse(raw);
                status = Str(doc.RootElement, "status").ToUpperInvariant();
                if (status is "ERROR" or "FAILED")
                {
                    _log.LogWarning("fal.ai job {Status}: {Body}", status, Trunc(raw, 400));
                    return null;
                }
                if (status != "COMPLETED") continue; // IN_QUEUE / IN_PROGRESS
            }
            catch (Exception ex) { _log.LogWarning(ex, "fal.ai status excepción (reintenta)"); continue; }

            // COMPLETED → traer el output.
            try
            {
                var resp = await _http.GetAsync(responseUrl, ct);
                var raw = await resp.Content.ReadAsStringAsync(ct);
                if (!resp.IsSuccessStatusCode) { _log.LogWarning("fal.ai response {Status}", resp.StatusCode); return null; }
                using var doc = JsonDocument.Parse(raw);
                var url = TryExtractVideoUrl(doc.RootElement);
                if (url == null) _log.LogWarning("fal.ai response sin video.url: {Body}", Trunc(raw, 400));
                return url;
            }
            catch (Exception ex) { _log.LogError(ex, "fal.ai response excepción"); return null; }
        }

        _log.LogWarning("fal.ai: timeout de polling ({N} intentos)", _opts.MaxPollAttempts);
        return null;
    }

    private static string Str(JsonElement e, string key) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? (v.GetString() ?? "") : "";

    /// <summary>Saca la URL del video del output. Forma típica: {"video":{"url":...}}; fallback recursivo.</summary>
    private static string? TryExtractVideoUrl(JsonElement el)
    {
        switch (el.ValueKind)
        {
            case JsonValueKind.Object:
                if (el.TryGetProperty("video", out var video) && video.ValueKind == JsonValueKind.Object
                    && video.TryGetProperty("url", out var vu) && vu.ValueKind == JsonValueKind.String)
                    return vu.GetString();
                foreach (var key in new[] { "video_url", "url" })
                    if (el.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String && IsVideoUrl(v.GetString()))
                        return v.GetString();
                foreach (var p in el.EnumerateObject())
                {
                    var found = TryExtractVideoUrl(p.Value);
                    if (found != null) return found;
                }
                return null;
            case JsonValueKind.Array:
                foreach (var item in el.EnumerateArray())
                {
                    var found = TryExtractVideoUrl(item);
                    if (found != null) return found;
                }
                return null;
            case JsonValueKind.String:
                var s = el.GetString();
                return IsVideoUrl(s) ? s : null;
            default:
                return null;
        }
    }

    private static bool IsVideoUrl(string? s)
    {
        if (string.IsNullOrWhiteSpace(s) || !s.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return false;
        var l = s.ToLowerInvariant();
        return l.Contains(".mp4") || l.Contains(".mov") || l.Contains(".webm");
    }

    private static string Trunc(string s, int n) => string.IsNullOrEmpty(s) || s.Length <= n ? s : s[..n];

    private async Task<AssetResult?> PersistAsync(string videoUrl, Guid? postId, CancellationToken ct)
    {
        byte[] bytes;
        try { bytes = await _http.GetByteArrayAsync(videoUrl, ct); }
        catch (Exception ex) { _log.LogError(ex, "fal.ai: no se pudo descargar {Url}", videoUrl); return null; }
        if (bytes is not { Length: > 0 }) return null;

        var asset = new SocialPostAsset
        {
            Id = Guid.NewGuid(),
            SocialPostId = postId,
            MimeType = "video/mp4",
            Content = bytes,
            SizeBytes = bytes.LongLength,
        };
        _db.SocialPostAssets.Add(asset);
        await _db.SaveChangesAsync(ct);
        var publicUrl = $"{_opts.PublicBaseUrl.TrimEnd('/')}/api/posteos/assets/{asset.Id}.mp4";
        return new AssetResult(publicUrl, null);
    }
}
