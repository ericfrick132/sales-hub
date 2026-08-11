using System.Globalization;
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
    private readonly ElevenLabsClient _tts;
    private readonly ILogger<FalVideoGenerator> _log;

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    public FalVideoGenerator(HttpClient http, IOptions<FalOptions> opts, ApplicationDbContext db, ElevenLabsClient tts, ILogger<FalVideoGenerator> log)
    {
        _http = http;
        _opts = opts.Value;
        _db = db;
        _tts = tts;
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
        return url == null ? null : await PersistAsync(url, null, null, ct);
    }

    public async Task<AssetResult?> GenerateForPostAsync(PostingProfile profile, SocialPost post, CancellationToken ct = default)
    {
        if (post.AssetKind != SocialAssetKind.Video) return null;
        var prompt = BuildBrandPrompt(profile, post);
        // Todas las proporciones de acá son nativas de Seedance, así que el video sale ya
        // en el encuadre final y no pasa por ningún reencuadre posterior. El carrusel va
        // en 1:1 para que coincida con las imágenes del feed (ver AiImageGenerator.DimsFor);
        // 3:4 además caía fuera del rango que acepta el feed de Instagram y lo recortaba él.
        var aspect = post.Format switch
        {
            SocialPostFormat.Story or SocialPostFormat.Reel or SocialPostFormat.Video => "9:16",
            SocialPostFormat.Carousel => "1:1",
            _ => "16:9",
        };
        var url = await GenerateVideoUrlAsync(prompt, aspect, ct);
        if (url == null) return null;
        return await PersistAsync(url, post.Id, post.NarrationText, ct);
    }

    /// <summary>
    /// Video de PRUEBA para el editor de estilo (/posteos): mismo prompt de marca que un
    /// post real pero con un ImageStyle dado (aunque no esté guardado) y sin crear SocialPost.
    /// </summary>
    public async Task<(AssetResult Asset, string Prompt)?> GenerateSampleAsync(
        PostingProfile profile, string? styleOverride, string visualPrompt, CancellationToken ct = default)
    {
        var styled = new PostingProfile
        {
            BrandColorsJson = profile.BrandColorsJson,
            BrandVoice = profile.BrandVoice,
            ImageStyle = styleOverride ?? profile.ImageStyle,
        };
        var prompt = BuildBrandPrompt(styled, new SocialPost { Prompt = visualPrompt, Concept = visualPrompt });
        var url = await GenerateVideoUrlAsync(prompt, "9:16", ct);
        if (url == null) return null;
        var asset = await PersistAsync(url, null, null, ct);
        return (asset, prompt);
    }

    private static string BuildBrandPrompt(PostingProfile profile, SocialPost post)
    {
        var sb = new StringBuilder();
        sb.Append(string.IsNullOrWhiteSpace(post.Prompt) ? post.Concept : post.Prompt);
        sb.Append(". Short-form social video, dynamic, modern, eye-catching motion.");
        // Misma dirección de arte editable que usa AiImageGenerator (Posteos → marca).
        if (!string.IsNullOrWhiteSpace(profile.ImageStyle))
            sb.Append($" ART DIRECTION (must follow strictly): {profile.ImageStyle.Trim()}.");
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

    private async Task<AssetResult?> PersistAsync(string videoUrl, Guid? postId, string? narration, CancellationToken ct)
    {
        byte[] bytes;
        try { bytes = await _http.GetByteArrayAsync(videoUrl, ct); }
        catch (Exception ex) { _log.LogError(ex, "fal.ai: no se pudo descargar {Url}", videoUrl); return null; }
        if (bytes is not { Length: > 0 }) return null;

        // Narración (voz en off): generamos el mp3 con ElevenLabs y lo muxeamos al video.
        // Si algo falla, el video sale mudo (best-effort, nunca rompe el asset).
        if (!string.IsNullOrWhiteSpace(narration) && _tts.IsConfigured)
        {
            var audio = await _tts.SynthesizeAsync(narration, ct: ct);
            if (audio is { Length: > 0 })
            {
                var muxed = await MuxAudioAsync(bytes, audio, ct);
                if (muxed is { Length: > 0 }) bytes = muxed;
                else _log.LogWarning("Mux de narración falló — el video sale mudo");
            }
        }

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

    /// <summary>
    /// Pega la narración (mp3) sobre el video (mp4) con ffmpeg.
    ///
    /// Antes esto usaba <c>-shortest</c>, que corta al más corto de los dos: si la
    /// narración duraba más que el video la frase quedaba cortada a mitad de palabra,
    /// y si duraba menos se recortaba el video. Los dos casos se veían como "el video
    /// termina mal" (verificado 11-ago-2026: los mp4 salían con voz a -7.7 dB de pico
    /// en el último cuarto de segundo).
    ///
    /// Ahora el resultado dura lo que dure el MÁS LARGO: si sobra narración se congela
    /// el último frame (tpad) hasta que termine de hablar, y si sobra video se rellena
    /// el audio con silencio (apad). Siempre queda una cola corta para que no corte seco.
    /// Usa archivos temporales porque ffmpeg no muxea bien dos streams por stdin.
    /// </summary>
    private async Task<byte[]?> MuxAudioAsync(byte[] video, byte[] audio, CancellationToken ct)
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"vid_{Guid.NewGuid():N}");
        var vin = tmp + ".mp4";
        var ain = tmp + ".mp3";
        var vout = tmp + "_out.mp4";
        try
        {
            await File.WriteAllBytesAsync(vin, video, ct);
            await File.WriteAllBytesAsync(ain, audio, ct);

            var vdur = await ProbeDurationAsync(vin, ct);
            var adur = await ProbeDurationAsync(ain, ct);
            // Cola de respiro para que la última sílaba no quede pegada al corte.
            const double tail = 0.4;
            // A partir de acá el frame congelado se nota feo: no lo recortamos (cortar
            // sería volver al bug), pero lo avisamos para ajustar el largo de la narración.
            const double padRuidoso = 8.0;

            var args = new List<string> { "-y", "-i", vin, "-i", ain, "-map", "0:v:0", "-map", "1:a:0" };
            var pad = (adur > 0 && vdur > 0) ? adur + tail - vdur : 0;
            if (pad > 0.05)
            {
                // Congelamos el último frame hasta cubrir TODA la narración: obliga a
                // re-encodear el video (no hay copy). Nunca acotamos por debajo del audio,
                // porque un video más corto que su audio vuelve a cortar la frase.
                args.AddRange(new[] { "-vf", $"tpad=stop_mode=clone:stop_duration={pad.ToString("0.###", CultureInfo.InvariantCulture)}" });
                args.AddRange(new[] { "-c:v", "libx264", "-preset", "veryfast", "-crf", "20", "-pix_fmt", "yuv420p" });
                if (pad > padRuidoso)
                    _log.LogWarning("Narración {A:0.#}s contra un video de {V:0.#}s → {Pad:0.#}s de frame congelado; conviene acortar el guion o subir Fal__DurationSeconds", adur, vdur, pad);
            }
            else if (vdur > 0)
            {
                // El video manda: se copia tal cual y el audio se rellena con silencio
                // hasta esa duración. whole_dur va SIEMPRE acotado: un apad pelado genera
                // audio infinito y, sin -shortest, ffmpeg no termina nunca.
                args.AddRange(new[] { "-c:v", "copy", "-af",
                    $"apad=whole_dur={vdur.ToString("0.###", CultureInfo.InvariantCulture)}" });
            }
            else
            {
                // Sin duración de video no podemos acotar el pad: volvemos al corte simple.
                args.AddRange(new[] { "-c:v", "copy", "-shortest" });
            }
            args.AddRange(new[] { "-c:a", "aac", "-b:a", "128k", "-movflags", "+faststart", vout });

            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "ffmpeg",
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            foreach (var arg in args) psi.ArgumentList.Add(arg);

            using var proc = System.Diagnostics.Process.Start(psi);
            if (proc is null) return null;
            var stderr = await proc.StandardError.ReadToEndAsync(ct);
            await proc.WaitForExitAsync(ct);
            if (proc.ExitCode != 0)
            {
                _log.LogWarning("ffmpeg mux exit {Code}: {Err}", proc.ExitCode, Trunc(stderr, 400));
                return null;
            }
            _log.LogInformation("Mux ok: video {V:0.#}s + narración {A:0.#}s → +{Pad:0.#}s de cola", vdur, adur, Math.Max(pad, 0));
            return await File.ReadAllBytesAsync(vout, ct);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "ffmpeg mux excepción");
            return null;
        }
        finally
        {
            foreach (var f in new[] { vin, ain, vout })
                try { if (File.Exists(f)) File.Delete(f); } catch { /* no-op */ }
        }
    }

    /// <summary>Duración en segundos de un archivo con ffprobe. 0 si no se puede leer.</summary>
    private async Task<double> ProbeDurationAsync(string path, CancellationToken ct)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "ffprobe",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            foreach (var a in new[] { "-v", "error", "-show_entries", "format=duration",
                                      "-of", "default=noprint_wrappers=1:nokey=1", path })
                psi.ArgumentList.Add(a);

            using var proc = System.Diagnostics.Process.Start(psi);
            if (proc is null) return 0;
            var outp = await proc.StandardOutput.ReadToEndAsync(ct);
            await proc.WaitForExitAsync(ct);
            return double.TryParse(outp.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : 0;
        }
        catch (Exception ex)
        {
            // Sin duración caemos al camino "el video manda", que es el de antes.
            _log.LogWarning(ex, "ffprobe falló para {Path}", path);
            return 0;
        }
    }
}
