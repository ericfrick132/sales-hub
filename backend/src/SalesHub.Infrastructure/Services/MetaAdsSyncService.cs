using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using SalesHub.Core.Domain.Entities;
using SalesHub.Infrastructure.Persistence;

namespace SalesHub.Infrastructure.Services;

/// <summary>
/// Trae los anuncios de todas las cuentas publicitarias del token (Meta:AdsAccessToken) a
/// <see cref="MetaAd"/>, y reconoce de qué anuncio viene un mensaje por su texto pre-armado.
/// Por qué el texto: en las líneas por Evolution Meta casi nunca manda el referral del anuncio
/// (0 de 58 leads del 09-10/10/2026 lo trajeron), pero cada anuncio deja escrito un mensaje
/// distinto ("Tengo una barbería y quiero ver cómo TurnosPro contesta mi WhatsApp").
/// </summary>
public class MetaAdsSyncService
{
    private readonly ApplicationDbContext _db;
    private readonly IHttpClientFactory _http;
    private readonly IConfiguration _config;
    private readonly ILogger<MetaAdsSyncService> _log;

    public MetaAdsSyncService(ApplicationDbContext db, IHttpClientFactory http, IConfiguration config, ILogger<MetaAdsSyncService> log)
    {
        _db = db; _http = http; _config = config; _log = log;
    }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_config["Meta:AdsAccessToken"]);

    private string Version => string.IsNullOrWhiteSpace(_config["Meta:GraphVersion"]) ? "v21.0" : _config["Meta:GraphVersion"]!;

    /// <summary>Sincroniza todos los anuncios (menos borrados/archivados). Devuelve cuántos guardó.</summary>
    public async Task<int> SyncAsync(CancellationToken ct)
    {
        var token = _config["Meta:AdsAccessToken"];
        if (string.IsNullOrWhiteSpace(token)) return 0;
        var client = _http.CreateClient();

        var accounts = new List<string>();
        await PageAsync(client, $"https://graph.facebook.com/{Version}/me/adaccounts?fields=id&limit=100&access_token={Uri.EscapeDataString(token)}",
            el => accounts.Add(el.GetProperty("id").GetString()!), ct);

        var products = await _db.Products.AsNoTracking().Where(p => p.ProductKey != "")
            .Select(p => new { p.ProductKey, p.DisplayName }).ToListAsync(ct);
        var existing = await _db.MetaAds.ToDictionaryAsync(a => a.AdId, ct);
        var now = DateTimeOffset.UtcNow;
        var saved = 0;

        const string fields = "id,name,effective_status,created_time,creative{title,body,object_story_spec,video_id}";
        const string filter = "[{\"field\":\"effective_status\",\"operator\":\"NOT_IN\",\"value\":[\"DELETED\",\"ARCHIVED\"]}]";
        foreach (var acct in accounts)
        {
            var url = $"https://graph.facebook.com/{Version}/{acct}/ads?fields={Uri.EscapeDataString(fields)}"
                    + $"&filtering={Uri.EscapeDataString(filter)}&limit=100&access_token={Uri.EscapeDataString(token)}";
            await PageAsync(client, url, el =>
            {
                var id = el.GetProperty("id").GetString()!;
                if (!existing.TryGetValue(id, out var ad))
                {
                    ad = new MetaAd { AdId = id };
                    _db.MetaAds.Add(ad);
                    existing[id] = ad;
                }
                ad.AccountId = acct;
                ad.Name = Trunc(Str(el, "name"), 256) ?? string.Empty;
                ad.Status = Str(el, "effective_status") ?? string.Empty;
                ad.AdCreatedAt = DateTimeOffset.TryParse(Str(el, "created_time"), out var created) ? created : null;
                if (el.TryGetProperty("creative", out var cr))
                {
                    ad.Title = Trunc(Str(cr, "title"), 256);
                    ad.Body = Str(cr, "body");
                    ad.VideoId = null;
                    ReadStorySpec(cr, ad);
                    // Sin video_data (p. ej. creativo dinámico) queda el video_id del creativo.
                    ad.VideoId ??= Str(cr, "video_id");
                }
                ParseName(ad, products.Select(p => (p.ProductKey, p.DisplayName)).ToList());
                ad.BusinessRef = BusinessRefFromPrefill(ad.PrefillText);
                ad.SyncedAt = now;
                saved++;
            }, ct);
        }
        await _db.SaveChangesAsync(ct);
        _log.LogInformation("Meta ads: {N} anuncios sincronizados de {A} cuentas", saved, accounts.Count);
        return saved;
    }

    /// <summary>
    /// El anuncio del que viene un mensaje: por id (referral) o por el texto pre-armado CONTENIDO en
    /// el mensaje. Tolera tildes, mayúsculas, espacios dobles y que el lead agregue algo antes o
    /// después. Si cambió el rubro ("Tengo un estudio de uñas y capilares y quiero…"), alcanza con
    /// que contenga la parte del anuncio desde "y quiero" ("quiero que mis clientes reserven asi de
    /// rapido"), que es propia de cada anuncio. Si varios anuncios dejan el mismo texto (la misma copia
    /// en AR, UY y MX), gana el del país del celular del lead.
    /// </summary>
    public async Task<MetaAd?> MatchAsync(string? adId, string? text, CancellationToken ct, string? phone = null)
    {
        if (!string.IsNullOrWhiteSpace(adId))
        {
            var byId = await _db.MetaAds.AsNoTracking().FirstOrDefaultAsync(a => a.AdId == adId, ct);
            if (byId is not null) return byId;
        }
        var norm = IntentText.Normalize(text);
        if (norm.Length < 12) return null;
        var candidates = await _db.MetaAds.AsNoTracking().Where(a => a.PrefillText != null && a.PrefillText != "").ToListAsync(ct);
        var withText = candidates.Select(a => (Ad: a, P: IntentText.Normalize(a.PrefillText))).Where(x => x.P.Length >= 12).ToList();
        var full = withText.Where(x => norm.Contains(x.P)).ToList();
        var matches = full.Count > 0 ? full : withText.Where(x => PrefillTail(x.P) is { } tail && norm.Contains(tail)).ToList();
        if (matches.Count == 0) return null;

        var leadCountry = ArAreaCodes.Resolve(phone).Country;
        var adCountry = leadCountry is null || matches.Count == 1
            ? new Dictionary<string, string>()
            : await AdCountriesAsync(matches.Select(x => x.Ad).ToList(), ct);
        // Mismo país que el lead primero; después el activo, el pre-armado más largo y el más reciente (copias → la vigente).
        return matches
            .OrderByDescending(x => leadCountry is not null && adCountry.GetValueOrDefault(x.Ad.AdId) == leadCountry)
            .ThenByDescending(x => x.Ad.Status == "ACTIVE")
            .ThenByDescending(x => x.P.Length)
            .ThenByDescending(x => x.Ad.AdCreatedAt)
            .Select(x => x.Ad)
            .First();
    }

    /// <summary>
    /// País de cada anuncio: la etiqueta del nombre ("PlayCrew - MX - padel - …") o, si no tiene, el
    /// país donde más impresiones tuvo (meta_geo_daily). Sin datos queda afuera del diccionario.
    /// </summary>
    private async Task<Dictionary<string, string>> AdCountriesAsync(List<MetaAd> ads, CancellationToken ct)
    {
        var result = new Dictionary<string, string>();
        foreach (var ad in ads)
        {
            var tag = ad.Name.Split(" - ", StringSplitOptions.TrimEntries)
                .FirstOrDefault(p => p.Length == 2 && p.All(char.IsAsciiLetterUpper));
            if (tag is not null) result[ad.AdId] = tag;
        }
        var rest = ads.Select(a => a.AdId).Where(id => !result.ContainsKey(id)).ToList();
        if (rest.Count == 0) return result;
        var byGeo = await _db.MetaGeoDaily.AsNoTracking().Where(x => rest.Contains(x.AdId))
            .GroupBy(x => new { x.AdId, x.Country }).Select(g => new { g.Key.AdId, g.Key.Country, Impr = g.Sum(x => x.Impressions) })
            .ToListAsync(ct);
        foreach (var g in byGeo.GroupBy(x => x.AdId))
            result[g.Key] = g.MaxBy(x => x.Impr)!.Country;
        return result;
    }

    /// <summary>
    /// ¿El texto trae el pre-armado de algún anuncio? Mismo criterio que <see cref="MatchAsync"/>, pero con los
    /// anuncios cargados una sola vez (para revisar muchos mensajes, ej. la métrica de leads de anuncios).
    /// </summary>
    public async Task<Func<string?, bool>> PrefillMatcherAsync(CancellationToken ct)
    {
        var prefills = (await _db.MetaAds.AsNoTracking().Where(a => a.PrefillText != null && a.PrefillText != "")
                .Select(a => a.PrefillText).ToListAsync(ct))
            .Select(IntentText.Normalize).Where(p => p.Length >= 12).Distinct().ToList();
        var tails = prefills.Select(PrefillTail).OfType<string>().Distinct().ToList();
        return text =>
        {
            var norm = IntentText.Normalize(text);
            return norm.Length >= 12 && (prefills.Any(norm.Contains) || tails.Any(norm.Contains));
        };
    }

    /// <summary>"hola tengo un gimnasio y quiero conocer gymhero" → "quiero conocer gymhero" (null si es muy corto para identificar el anuncio).</summary>
    private static string? PrefillTail(string normalizedPrefill)
    {
        var i = normalizedPrefill.IndexOf(" y quiero ", StringComparison.Ordinal);
        if (i < 0) return null;
        var tail = normalizedPrefill[(i + 3)..];
        return tail.Length >= 18 ? tail : null;
    }

    // ── parsing ─────────────────────────────────────────────────────────────────────────

    private static void ReadStorySpec(JsonElement creative, MetaAd ad)
    {
        if (!creative.TryGetProperty("object_story_spec", out var oss)) return;
        JsonElement data = default;
        var found = false;
        foreach (var key in new[] { "video_data", "link_data", "photo_data" })
            if (oss.TryGetProperty(key, out data)) { found = true; break; }
        if (!found) return;

        ad.VideoId = Str(data, "video_id");
        if (data.TryGetProperty("call_to_action", out var cta) && cta.TryGetProperty("value", out var v))
        {
            var link = Str(v, "link") ?? string.Empty;
            var m = Regex.Match(link, @"phone=(\d+)");
            if (m.Success) ad.DestinationPhone = m.Groups[1].Value;
            var t = Regex.Match(link, @"[?&]text=([^&]+)");
            if (t.Success) ad.PrefillText = Trunc(Uri.UnescapeDataString(t.Groups[1].Value.Replace('+', ' ')), 512);
        }
        // El pre-armado del editor visual gana sobre el ?text= del link.
        var welcome = Str(data, "page_welcome_message");
        if (!string.IsNullOrWhiteSpace(welcome))
        {
            try
            {
                using var doc = JsonDocument.Parse(welcome);
                var content = doc.RootElement.GetProperty("text_format").GetProperty("message")
                    .GetProperty("autofill_message").GetProperty("content").GetString();
                if (!string.IsNullOrWhiteSpace(content)) ad.PrefillText = Trunc(content, 512);
            }
            catch (Exception) { /* otro formato de bienvenida: queda el del link */ }
        }
    }

    /// <summary>"TurnosPro - barberia - bot WhatsApp" → app, rubro y ángulo. Nombres con otro formato quedan con lo que se pueda.</summary>
    private static void ParseName(MetaAd ad, List<(string Key, string Display)> products)
    {
        var parts = ad.Name.Split(" - ", StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return;
        if (Slug(parts[0]).Length == 0) return;
        ad.ProductKey = ProductFromName(ad.Name, products);
        if (parts.Length >= 3)
        {
            ad.Rubro = Trunc(Slug(parts[1]), 80);
            var angle = Slug(parts[2]);
            ad.Angle = angle.Contains("bot") ? "bot-whatsapp"
                     : angle.StartsWith("antes") ? "antes-despues"
                     : Trunc(angle, 80);
        }
    }

    /// <summary>
    /// App de un anuncio/campaña por su nombre ("GymHero - Personas (CTWA)", "PlayCrew B2B - …"):
    /// lo de antes del primer " - " contra ProductKey/DisplayName; exacto primero, si no el que EMPIEZA con la app.
    /// </summary>
    public static string? ProductFromName(string? name, IReadOnlyList<(string Key, string Display)> products)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var parts = name.Split(" - ", StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return null;
        var app = Slug(parts[0]);
        if (app.Length == 0) return null;
        return products.Where(p => Slug(p.Key) == app || Slug(p.Display) == app)
            .Concat(products.Where(p => (Slug(p.Display).Length > 2 && app.StartsWith(Slug(p.Display)))
                                     || (Slug(p.Key).Length > 2 && app.StartsWith(Slug(p.Key)))))
            .Select(p => p.Key).FirstOrDefault();
    }

    /// <summary>"Hola! Tengo una barbería y quiero…" → "la barberia". Sin artículo ("Tengo canchas de fútbol") → null.</summary>
    public static string? BusinessRefFromPrefill(string? prefill)
    {
        if (string.IsNullOrWhiteSpace(prefill)) return null;
        var m = Regex.Match(CopyStyle.Clean(prefill).ToLowerInvariant(), @"tengo (un|una|unos|unas) (.+?) y quiero");
        if (!m.Success) return null;
        var article = m.Groups[1].Value switch { "un" => "el", "una" => "la", "unos" => "los", _ => "las" };
        return Trunc($"{article} {m.Groups[2].Value.Trim()}", 120);
    }

    /// <summary>"Hola! Tengo una barbería y quiero…" → "una barberia" (lo que dijo que tiene, tal cual, sin tildes).</summary>
    public static string? HasPhraseFromPrefill(string? prefill)
    {
        if (string.IsNullOrWhiteSpace(prefill)) return null;
        var m = Regex.Match(CopyStyle.Clean(prefill).ToLowerInvariant(), @"tengo (.+?) y quiero");
        return m.Success ? Trunc(m.Groups[1].Value.Trim(), 120) : null;
    }

    private static string Slug(string? s) => IntentText.Normalize(s).Replace(" ", "-");

    private async Task PageAsync(HttpClient client, string url, Action<JsonElement> each, CancellationToken ct)
    {
        string? next = url;
        var pages = 0;
        while (next is not null && pages++ < 50)
        {
            var resp = await client.GetAsync(next, ct);
            var json = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
            {
                _log.LogWarning("Meta ads: Graph {Status}: {Body}", (int)resp.StatusCode, json.Length > 300 ? json[..300] : json);
                return;
            }
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("data", out var data))
                foreach (var el in data.EnumerateArray()) each(el);
            next = doc.RootElement.TryGetProperty("paging", out var paging) ? Str(paging, "next") : null;
        }
    }

    private static string? Str(JsonElement el, string prop)
        => el.ValueKind == JsonValueKind.Object && el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static string? Trunc(string? s, int max) => s is null ? null : (s.Length <= max ? s : s[..max]);
}
