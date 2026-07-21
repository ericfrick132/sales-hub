using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace SalesHub.Infrastructure.Services.Social;

/// <summary>
/// Trae el logo de una marca desde su landing (best-effort) y compone logos reales
/// sobre las imágenes generadas por IA — porque los modelos texto→imagen NO dibujan
/// bien un logo/marca. El composite se hace con ffmpeg (ya está en el contenedor).
/// </summary>
public class BrandLogoService
{
    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<BrandLogoService> _log;

    private static readonly Regex AppleIcon = new("<link[^>]+rel=[\"'][^\"']*apple-touch-icon[^\"']*[\"'][^>]*>", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex IconLink = new("<link[^>]+rel=[\"'][^\"']*icon[^\"']*[\"'][^>]*>", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex OgImage = new("<meta[^>]+property=[\"']og:image[\"'][^>]*>", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex HrefAttr = new("href=[\"']([^\"']+)[\"']", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex ContentAttr = new("content=[\"']([^\"']+)[\"']", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public BrandLogoService(IHttpClientFactory httpFactory, ILogger<BrandLogoService> log)
    {
        _httpFactory = httpFactory; _log = log;
    }

    /// <summary>
    /// Best-effort: apple-touch-icon → favicon → og:image. Devuelve (bytes, mime) o null.
    /// El apple-touch-icon suele ser el ícono de marca cuadrado (lo mejor para overlay).
    /// </summary>
    public async Task<(byte[] Bytes, string Mime)?> FetchFromLandingAsync(string landingUrl, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(landingUrl)) return null;
        try
        {
            var http = _httpFactory.CreateClient();
            http.Timeout = TimeSpan.FromSeconds(15);
            http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (compatible; SalesHubBot/1.0)");
            var html = await http.GetStringAsync(landingUrl, ct);
            var baseUri = new Uri(landingUrl);

            foreach (var candidate in new[]
            {
                FirstHref(AppleIcon.Match(html)),
                FirstHref(IconLink.Match(html)),
                FirstContent(OgImage.Match(html)),
            })
            {
                if (string.IsNullOrWhiteSpace(candidate)) continue;
                if (candidate.EndsWith(".svg", StringComparison.OrdinalIgnoreCase)) continue; // no compositamos SVG
                var abs = new Uri(baseUri, candidate).ToString();
                var img = await DownloadImageAsync(http, abs, ct);
                if (img is not null) { _log.LogInformation("Logo de {Url} → {Src}", landingUrl, abs); return img; }
            }
            return null;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "No pude traer el logo de {Url}", landingUrl);
            return null;
        }
    }

    private static string? FirstHref(Match m) => m.Success ? HrefAttr.Match(m.Value) is { Success: true } h ? h.Groups[1].Value : null : null;
    private static string? FirstContent(Match m) => m.Success ? ContentAttr.Match(m.Value) is { Success: true } c ? c.Groups[1].Value : null : null;

    private async Task<(byte[], string)?> DownloadImageAsync(HttpClient http, string url, CancellationToken ct)
    {
        try
        {
            using var resp = await http.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode) return null;
            var mime = resp.Content.Headers.ContentType?.MediaType ?? "image/png";
            if (!mime.StartsWith("image/", StringComparison.OrdinalIgnoreCase)) return null;
            var bytes = await resp.Content.ReadAsByteArrayAsync(ct);
            return bytes.Length is > 0 and < 6 * 1024 * 1024 ? (bytes, mime) : null;
        }
        catch { return null; }
    }

    /// <summary>
    /// Lleva la imagen al tamaño EXACTO de publicación. Como los formatos de
    /// <c>AiImageGenerator.DimsFor</c> usan las mismas proporciones que GPT Image sabe
    /// generar (1:1 y 2:3), esto es un simple reescalado: no recorta ni deforma nada.
    /// El pad queda como red de seguridad — si algún día la proporción de origen no
    /// coincidiera, la imagen entra entera con banda en vez de salir cortada o estirada.
    /// Best-effort: si falla, devuelve la imagen original.
    /// </summary>
    public async Task<byte[]> NormalizeAsync(byte[] image, int width, int height, CancellationToken ct = default)
    {
        if (image.Length == 0) return image;
        var tmp = Path.Combine(Path.GetTempPath(), $"norm_{Guid.NewGuid():N}");
        var inPath = tmp + "_in.png";
        var outPath = tmp + "_out.png";
        try
        {
            await File.WriteAllBytesAsync(inPath, image, ct);
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "ffmpeg",
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            psi.ArgumentList.Add("-y");
            psi.ArgumentList.Add("-i"); psi.ArgumentList.Add(inPath);
            psi.ArgumentList.Add("-vf");
            psi.ArgumentList.Add(
                $"scale={width}:{height}:force_original_aspect_ratio=decrease," +
                $"pad={width}:{height}:(ow-iw)/2:(oh-ih)/2:color=white");
            psi.ArgumentList.Add(outPath);

            using var proc = System.Diagnostics.Process.Start(psi);
            if (proc is null) return image;
            var stderr = await proc.StandardError.ReadToEndAsync(ct);
            await proc.WaitForExitAsync(ct);
            if (proc.ExitCode != 0)
            {
                _log.LogWarning("ffmpeg normalize exit {Code}: {Err}", proc.ExitCode, stderr.Length > 300 ? stderr[..300] : stderr);
                return image;
            }
            var outBytes = await File.ReadAllBytesAsync(outPath, ct);
            return outBytes.Length > 0 ? outBytes : image;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Normalize de imagen falló");
            return image;
        }
        finally
        {
            foreach (var f in new[] { inPath, outPath })
                try { if (File.Exists(f)) File.Delete(f); } catch { /* no-op */ }
        }
    }

    /// <summary>
    /// Pega uno o más logos (PNG con transparencia idealmente) sobre la imagen base con ffmpeg.
    /// El logo de MARCA va abajo-derecha, escalado al ~16% del ancho. Los logos de FEATURE
    /// (MercadoPago/WhatsApp) van en una fila abajo-izquierda, más chicos. Best-effort: si
    /// algo falla, devuelve la imagen base sin tocar.
    /// </summary>
    public async Task<byte[]> CompositeAsync(byte[] baseImage, byte[]? brandLogo, IReadOnlyList<byte[]> featureLogos, CancellationToken ct = default)
    {
        if ((brandLogo is null || brandLogo.Length == 0) && featureLogos.Count == 0) return baseImage;

        var tmp = Path.Combine(Path.GetTempPath(), $"logo_{Guid.NewGuid():N}");
        var basePath = tmp + "_base.png";
        var outPath = tmp + "_out.png";
        var inputs = new List<string> { basePath };
        var writes = new List<string> { basePath };
        try
        {
            await File.WriteAllBytesAsync(basePath, baseImage, ct);

            var filters = new List<string>();
            var last = "0"; // stream de video actual (0 = base)
            var idx = 1;

            // Anchos fijos en px (las imágenes salen 1024-1080 de ancho): logo de marca
            // ~170px abajo-derecha; logos de feature ~120px en fila abajo-izquierda.
            if (brandLogo is { Length: > 0 })
            {
                var p = tmp + $"_brand.png"; await File.WriteAllBytesAsync(p, brandLogo, ct);
                inputs.Add(p); writes.Add(p);
                filters.Add($"[{idx}]scale=170:-1[bl]");
                filters.Add($"[{last}][bl]overlay=W-w-40:H-h-40[v{idx}]");
                last = $"v{idx}"; idx++;
            }

            var fx = 40; // margen izquierdo en px
            foreach (var fl in featureLogos)
            {
                if (fl.Length == 0) continue;
                var p = tmp + $"_feat{idx}.png"; await File.WriteAllBytesAsync(p, fl, ct);
                inputs.Add(p); writes.Add(p);
                filters.Add($"[{idx}]scale=120:-1[fl{idx}]");
                filters.Add($"[{last}][fl{idx}]overlay={fx}:H-h-40[v{idx}]");
                last = $"v{idx}"; fx += 150; idx++;
            }

            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "ffmpeg",
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            psi.ArgumentList.Add("-y");
            foreach (var inp in inputs) { psi.ArgumentList.Add("-i"); psi.ArgumentList.Add(inp); }
            psi.ArgumentList.Add("-filter_complex");
            psi.ArgumentList.Add(string.Join(";", filters));
            psi.ArgumentList.Add("-map"); psi.ArgumentList.Add($"[{last}]");
            psi.ArgumentList.Add(outPath);

            using var proc = System.Diagnostics.Process.Start(psi);
            if (proc is null) return baseImage;
            var stderr = await proc.StandardError.ReadToEndAsync(ct);
            await proc.WaitForExitAsync(ct);
            if (proc.ExitCode != 0)
            {
                _log.LogWarning("ffmpeg composite logo exit {Code}: {Err}", proc.ExitCode, stderr.Length > 300 ? stderr[..300] : stderr);
                return baseImage;
            }
            var outBytes = await File.ReadAllBytesAsync(outPath, ct);
            return outBytes.Length > 0 ? outBytes : baseImage;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Composite de logo falló");
            return baseImage;
        }
        finally
        {
            foreach (var f in writes.Append(outPath))
                try { if (File.Exists(f)) File.Delete(f); } catch { /* no-op */ }
        }
    }
}
