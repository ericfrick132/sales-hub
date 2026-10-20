using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using SalesHub.Infrastructure.Persistence;

namespace SalesHub.Infrastructure.Services;

/// <summary>
/// Miniatura y video de cada anuncio para el tablero de rendimiento (Marketing → Rendimiento de anuncios).
/// Se piden en vivo a Meta porque sus URLs vencen (las del video a los ~4 días): miniaturas en lote
/// (?ids=, 50 por pedido, cache 6 h) y la URL del video recién cuando se reproduce (cache 2 h).
/// </summary>
public class MetaAdMediaService
{
    private const int Batch = 50;

    private readonly ApplicationDbContext _db;
    private readonly IHttpClientFactory _http;
    private readonly IConfiguration _config;
    private readonly IMemoryCache _cache;
    private readonly ILogger<MetaAdMediaService> _log;

    public MetaAdMediaService(ApplicationDbContext db, IHttpClientFactory http, IConfiguration config, IMemoryCache cache, ILogger<MetaAdMediaService> log)
    {
        _db = db; _http = http; _config = config; _cache = cache; _log = log;
    }

    private string? Token => _config["Meta:AdsAccessToken"];
    private string Version => string.IsNullOrWhiteSpace(_config["Meta:GraphVersion"]) ? "v21.0" : _config["Meta:GraphVersion"]!;

    /// <summary>Miniatura vertical (540×960) del creativo de cada anuncio. Los que Meta no devuelve quedan afuera.</summary>
    public async Task<Dictionary<string, string>> ThumbnailsAsync(IEnumerable<string> adIds, CancellationToken ct)
    {
        var result = new Dictionary<string, string>();
        var missing = new List<string>();
        foreach (var id in adIds.Where(IsGraphId).Distinct())
        {
            if (_cache.TryGetValue("meta-ad-thumb:" + id, out string? url) && url is not null) result[id] = url;
            else missing.Add(id);
        }
        if (missing.Count == 0 || string.IsNullOrWhiteSpace(Token)) return result;

        var client = _http.CreateClient();
        const string fields = "creative.thumbnail_width(540).thumbnail_height(960){thumbnail_url}";
        foreach (var chunk in missing.Chunk(Batch))
        {
            var url = $"https://graph.facebook.com/{Version}/?ids={string.Join(',', chunk)}"
                    + $"&fields={Uri.EscapeDataString(fields)}&access_token={Uri.EscapeDataString(Token!)}";
            using var doc = await GetAsync(client, url, ct);
            if (doc is null) continue;
            foreach (var ad in doc.RootElement.EnumerateObject())
            {
                if (!ad.Value.TryGetProperty("creative", out var cr) || Str(cr, "thumbnail_url") is not { } thumb) continue;
                result[ad.Name] = thumb;
                _cache.Set("meta-ad-thumb:" + ad.Name, thumb, TimeSpan.FromHours(6));
            }
        }
        return result;
    }

    /// <summary>URL reproducible del video del anuncio (null si el anuncio no es de video o Meta no la da).</summary>
    public async Task<string?> VideoUrlAsync(string adId, CancellationToken ct)
    {
        if (!IsGraphId(adId) || string.IsNullOrWhiteSpace(Token)) return null;
        if (_cache.TryGetValue("meta-ad-video:" + adId, out string? cached)) return cached;

        var videoId = await _db.MetaAds.AsNoTracking().Where(a => a.AdId == adId).Select(a => a.VideoId).FirstOrDefaultAsync(ct);
        var client = _http.CreateClient();
        if (string.IsNullOrWhiteSpace(videoId))
        {
            // Anuncio todavía sin sync (o de antes de guardar VideoId): se lee del creativo.
            using var adDoc = await GetAsync(client,
                $"https://graph.facebook.com/{Version}/{adId}?fields={Uri.EscapeDataString("creative{video_id,object_story_spec{video_data{video_id}}}")}&access_token={Uri.EscapeDataString(Token!)}", ct);
            if (adDoc?.RootElement.TryGetProperty("creative", out var cr) == true)
                videoId = cr.TryGetProperty("object_story_spec", out var oss) && oss.TryGetProperty("video_data", out var vd)
                    ? Str(vd, "video_id") ?? Str(cr, "video_id")
                    : Str(cr, "video_id");
        }
        if (string.IsNullOrWhiteSpace(videoId) || !IsGraphId(videoId)) return null;

        using var doc = await GetAsync(client,
            $"https://graph.facebook.com/{Version}/{videoId}?fields=source&access_token={Uri.EscapeDataString(Token!)}", ct);
        var source = doc is null ? null : Str(doc.RootElement, "source");
        if (source is not null) _cache.Set("meta-ad-video:" + adId, source, TimeSpan.FromHours(2));
        return source;
    }

    private async Task<JsonDocument?> GetAsync(HttpClient client, string url, CancellationToken ct)
    {
        var resp = await client.GetAsync(url, ct);
        var json = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
        {
            _log.LogWarning("Meta ad media: Graph {Status}: {Body}", (int)resp.StatusCode, json.Length > 300 ? json[..300] : json);
            return null;
        }
        return JsonDocument.Parse(json);
    }

    /// <summary>Los ids de Graph son numéricos: evita meter cualquier cosa en la URL.</summary>
    private static bool IsGraphId(string? id) => !string.IsNullOrEmpty(id) && id.Length <= 40 && id.All(char.IsAsciiDigit);

    private static string? Str(JsonElement el, string prop)
        => el.ValueKind == JsonValueKind.Object && el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
