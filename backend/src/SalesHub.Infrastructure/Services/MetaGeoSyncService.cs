using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using SalesHub.Core.Domain.Entities;
using SalesHub.Infrastructure.Persistence;

namespace SalesHub.Infrastructure.Services;

/// <summary>
/// Rendimiento de los anuncios de Meta por provincia → <see cref="MetaGeoDaily"/> (mapa de anuncios).
/// Por cuenta del token (Meta:AdsAccessToken): insights level=ad, breakdowns=country,region,
/// time_increment=1. Cada corrida re-sincroniza los últimos 3 días (Meta corrige la atribución) y
/// una cuenta sin filas hace backfill de 90 días. Además pide en vivo el alcance exacto de un rango
/// (el reach diario no se puede sumar).
/// </summary>
public class MetaGeoSyncService
{
    public const int BackfillDays = 90;
    public const int ResyncDays = 3;
    /// <summary>Cada cuánto corre sola (la dispara <c>MetaAdsSyncWorker</c>, que late cada hora).</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromHours(6);

    private const string Fields =
        "campaign_id,campaign_name,adset_id,adset_name,ad_id,ad_name,reach,impressions,spend,clicks,unique_clicks,inline_link_clicks,actions";

    private readonly ApplicationDbContext _db;
    private readonly IHttpClientFactory _http;
    private readonly IConfiguration _config;
    private readonly ILogger<MetaGeoSyncService> _log;

    public MetaGeoSyncService(ApplicationDbContext db, IHttpClientFactory http, IConfiguration config, ILogger<MetaGeoSyncService> log)
    {
        _db = db; _http = http; _config = config; _log = log;
    }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_config["Meta:AdsAccessToken"]);

    private string Version => string.IsNullOrWhiteSpace(_config["Meta:GraphVersion"]) ? "v21.0" : _config["Meta:GraphVersion"]!;
    private string Token => _config["Meta:AdsAccessToken"] ?? string.Empty;

    /// <summary>Hoy en Argentina (las cuentas publicitarias están en hora de Buenos Aires).</summary>
    public static DateOnly TodayAr()
    {
        TimeZoneInfo tz;
        try { tz = TimeZoneInfo.FindSystemTimeZoneById("America/Argentina/Buenos_Aires"); }
        catch { tz = TimeZoneInfo.CreateCustomTimeZone("AR", TimeSpan.FromHours(-3), "AR", "AR"); }
        return DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, tz).DateTime);
    }

    /// <summary>¿Pasó <see cref="Interval"/> desde la última sync (o nunca corrió)?</summary>
    public async Task<bool> IsDueAsync(CancellationToken ct)
    {
        var last = await _db.MetaGeoDaily.AsNoTracking().MaxAsync(x => (DateTimeOffset?)x.SyncedAt, ct);
        return last is null || DateTimeOffset.UtcNow - last.Value >= Interval;
    }

    public record SyncResult(int Accounts, int Rows, int Removed, DateOnly? From, DateOnly To);

    /// <summary>
    /// Sincroniza todas las cuentas. <paramref name="days"/> fuerza la ventana (1..90); si es null,
    /// 3 días (o 90 si la cuenta todavía no tiene filas).
    /// </summary>
    public async Task<SyncResult> SyncAsync(int? days, CancellationToken ct)
    {
        var token = Token;
        var today = TodayAr();
        if (string.IsNullOrWhiteSpace(token)) return new SyncResult(0, 0, 0, null, today);
        var client = _http.CreateClient();

        var accounts = await AccountsAsync(client, ct);
        var products = (await _db.Products.AsNoTracking().Where(p => p.ProductKey != "")
            .Select(p => new { p.ProductKey, p.DisplayName }).ToListAsync(ct))
            .Select(p => (p.ProductKey, p.DisplayName)).ToList();
        var adProducts = await _db.MetaAds.AsNoTracking().Where(a => a.ProductKey != null)
            .ToDictionaryAsync(a => a.AdId, a => a.ProductKey!, ct);

        int rows = 0, removed = 0;
        DateOnly? minFrom = null;
        foreach (var acct in accounts)
        {
            var hasRows = await _db.MetaGeoDaily.AnyAsync(x => x.AccountId == acct, ct);
            var span = Math.Clamp(days ?? (hasRows ? ResyncDays : BackfillDays), 1, BackfillDays);
            var from = today.AddDays(-(span - 1));
            if (minFrom is null || from < minFrom) minFrom = from;
            // Ventanas de 10 días: respuestas chicas y si una falla no se pierde todo.
            for (var wFrom = from; wFrom <= today; wFrom = wFrom.AddDays(10))
            {
                var wTo = wFrom.AddDays(9) > today ? today : wFrom.AddDays(9);
                var (n, r) = await SyncWindowAsync(client, acct, wFrom, wTo, products, adProducts, ct);
                rows += n; removed += r;
            }
        }
        _log.LogInformation("Meta geo: {Rows} filas ({Removed} borradas) de {A} cuentas desde {From}", rows, removed, accounts.Count, minFrom);
        return new SyncResult(accounts.Count, rows, removed, minFrom, today);
    }

    private async Task<(int Rows, int Removed)> SyncWindowAsync(HttpClient client, string acct, DateOnly from, DateOnly to,
        List<(string Key, string Display)> products, Dictionary<string, string> adProducts, CancellationToken ct)
    {
        var url = InsightsUrl(acct, "ad", "country,region", from, to, Fields, filtering: null, timeIncrement: true);
        var fetched = new List<JsonElement>();
        var ok = await PageAsync(client, url, el => fetched.Add(el.Clone()), ct);
        if (!ok) return (0, 0); // no tocamos lo guardado si Meta falló

        var existing = (await _db.MetaGeoDaily
                .Where(x => x.AccountId == acct && x.Date >= from && x.Date <= to).ToListAsync(ct))
            .GroupBy(x => (x.Date, x.AdId, x.Region)).ToDictionary(g => g.Key, g => g.First());
        var seen = new HashSet<(DateOnly, string, string)>();
        var now = DateTimeOffset.UtcNow;

        foreach (var el in fetched)
        {
            var adId = Str(el, "ad_id");
            if (string.IsNullOrEmpty(adId) || !DateOnly.TryParse(Str(el, "date_start"), CultureInfo.InvariantCulture, out var date)) continue;
            var rawRegion = Str(el, "region") ?? "Unknown";
            var country = (Str(el, "country") ?? string.Empty).ToUpperInvariant();
            var canonical = ArRegions.Canonical(rawRegion);
            if (country.Length == 0) country = canonical is not null ? "AR" : "??";
            var region = Trunc(country == "AR" && canonical is not null ? canonical
                : rawRegion == "Unknown" ? $"Sin región ({country})" : rawRegion, 120)!;
            var key = (date, adId, region);
            if (!seen.Add(key)) continue;

            if (!existing.TryGetValue(key, out var row))
            {
                row = new MetaGeoDaily { Date = date, AdId = adId, Region = region, AccountId = acct };
                _db.MetaGeoDaily.Add(row);
                existing[key] = row;
            }
            row.AccountId = acct;
            row.Country = Trunc(country, 8)!;
            row.CampaignId = Str(el, "campaign_id") ?? string.Empty;
            row.CampaignName = Trunc(Str(el, "campaign_name"), 256) ?? string.Empty;
            row.AdsetId = Str(el, "adset_id") ?? string.Empty;
            row.AdsetName = Trunc(Str(el, "adset_name"), 256) ?? string.Empty;
            row.AdName = Trunc(Str(el, "ad_name"), 256) ?? string.Empty;
            row.ProductKey = MetaAdsSyncService.ProductFromName(row.CampaignName, products)
                          ?? MetaAdsSyncService.ProductFromName(row.AdName, products)
                          ?? (adProducts.TryGetValue(adId, out var pk) ? pk : null);
            row.Reach = Long(el, "reach");
            row.Impressions = Long(el, "impressions");
            row.Spend = Dec(el, "spend");
            row.Clicks = Long(el, "clicks");
            row.UniqueClicks = Long(el, "unique_clicks");
            var actions = Actions(el);
            row.LinkClicks = el.TryGetProperty("inline_link_clicks", out _) ? Long(el, "inline_link_clicks") : actions.GetValueOrDefault("link_click");
            row.VideoViews3s = actions.GetValueOrDefault("video_view");
            row.MessagingConnections = actions.GetValueOrDefault("onsite_conversion.total_messaging_connection");
            row.FirstReplies = actions.GetValueOrDefault("onsite_conversion.messaging_first_reply");
            row.Depth2 = actions.GetValueOrDefault("onsite_conversion.messaging_user_depth_2_message_send");
            row.Depth3 = actions.GetValueOrDefault("onsite_conversion.messaging_user_depth_3_message_send");
            row.SyncedAt = now;
        }

        // Lo que Meta ya no devuelve para esa ventana (atribución corregida) se va.
        var stale = existing.Where(kv => !seen.Contains(kv.Key) && _db.Entry(kv.Value).State != EntityState.Added)
            .Select(kv => kv.Value).ToList();
        _db.MetaGeoDaily.RemoveRange(stale);
        await _db.SaveChangesAsync(ct);
        _db.ChangeTracker.Clear();
        return (seen.Count, stale.Count);
    }

    // ── alcance exacto en vivo ──────────────────────────────────────────────────────────

    public record LiveReach(long Total, Dictionary<string, long> ByRegion);

    /// <summary>
    /// Alcance único del rango, total y por región (claves canónicas como en <see cref="MetaGeoDaily.Region"/>),
    /// pidiéndolo a Meta level=account con filtering por ad.id. <paramref name="adIdsByAccount"/> null en una
    /// cuenta = sin filtro (toda la cuenta). Entre cuentas distintas se suma (no hay forma de deduplicar).
    /// Devuelve null si Meta falla en alguna cuenta (la UI cae a la suma diaria y marca "aprox.").
    /// </summary>
    public async Task<LiveReach?> LiveReachAsync(IReadOnlyDictionary<string, IReadOnlyCollection<string>?> adIdsByAccount,
        DateOnly from, DateOnly to, CancellationToken ct)
    {
        if (!IsConfigured) return null;
        var client = _http.CreateClient();
        long total = 0;
        var byRegion = new Dictionary<string, long>();
        foreach (var (acct, adIds) in adIdsByAccount)
        {
            string? filtering = null;
            if (adIds is not null)
            {
                if (adIds.Count == 0) continue;
                filtering = JsonSerializer.Serialize(new[] { new { field = "ad.id", @operator = "IN", value = adIds } });
            }
            var ok = await PageAsync(client, InsightsUrl(acct, "account", null, from, to, "reach", filtering, false),
                el => total += Long(el, "reach"), ct);
            if (!ok) return null;
            ok = await PageAsync(client, InsightsUrl(acct, "account", "country,region", from, to, "reach", filtering, false), el =>
            {
                var raw = Str(el, "region") ?? "Unknown";
                var country = (Str(el, "country") ?? string.Empty).ToUpperInvariant();
                var canonical = ArRegions.Canonical(raw);
                var region = (country is "AR" or "") && canonical is not null ? canonical
                    : raw == "Unknown" ? $"Sin región ({(country.Length == 0 ? "??" : country)})" : raw;
                byRegion[region] = byRegion.GetValueOrDefault(region) + Long(el, "reach");
            }, ct);
            if (!ok) return null;
        }
        return new LiveReach(total, byRegion);
    }

    // ── conversaciones en vivo ──────────────────────────────────────────────────────────

    /// <summary>
    /// Conversaciones de WhatsApp que Meta cuenta por día y app (onsite_conversion.messaging_conversation_started_7d,
    /// level=campaign), pedidas en vivo: meta_geo_daily se sincroniza cada 6 h y para "hoy" queda atrasado.
    /// App por el nombre de la campaña ("sin-app" si no se reconoce). null si Meta falla en alguna cuenta.
    /// </summary>
    public async Task<Dictionary<DateOnly, Dictionary<string, int>>?> LiveConversationsAsync(DateOnly from, DateOnly to, CancellationToken ct)
    {
        if (!IsConfigured) return null;
        var client = _http.CreateClient();
        var products = (await _db.Products.AsNoTracking().Where(p => p.ProductKey != "")
            .Select(p => new { p.ProductKey, p.DisplayName }).ToListAsync(ct))
            .Select(p => (p.ProductKey, p.DisplayName)).ToList();
        var result = new Dictionary<DateOnly, Dictionary<string, int>>();
        foreach (var acct in await AccountsAsync(client, ct))
        {
            var ok = await PageAsync(client, InsightsUrl(acct, "campaign", null, from, to, "campaign_name,actions", null, true), el =>
            {
                if (!DateOnly.TryParse(Str(el, "date_start"), CultureInfo.InvariantCulture, out var date)) return;
                var n = (int)Actions(el).GetValueOrDefault("onsite_conversion.messaging_conversation_started_7d");
                if (n == 0) return;
                var app = MetaAdsSyncService.ProductFromName(Str(el, "campaign_name"), products) ?? "sin-app";
                var day = result.TryGetValue(date, out var d) ? d : result[date] = new Dictionary<string, int>();
                day[app] = day.GetValueOrDefault(app) + n;
            }, ct);
            if (!ok) return null;
        }
        return result;
    }

    // ── Graph ───────────────────────────────────────────────────────────────────────────

    private async Task<List<string>> AccountsAsync(HttpClient client, CancellationToken ct)
    {
        var accounts = new List<string>();
        await PageAsync(client, $"https://graph.facebook.com/{Version}/me/adaccounts?fields=id&limit=100&access_token={Uri.EscapeDataString(Token)}",
            el => accounts.Add(el.GetProperty("id").GetString()!), ct);
        return accounts;
    }

    private string InsightsUrl(string acct, string level, string? breakdowns, DateOnly from, DateOnly to, string fields,
        string? filtering, bool timeIncrement)
    {
        var range = $"{{\"since\":\"{from:yyyy-MM-dd}\",\"until\":\"{to:yyyy-MM-dd}\"}}";
        var url = $"https://graph.facebook.com/{Version}/{acct}/insights?level={level}"
                + $"&fields={Uri.EscapeDataString(fields)}&time_range={Uri.EscapeDataString(range)}&limit=500"
                + $"&access_token={Uri.EscapeDataString(Token)}";
        if (breakdowns is not null) url += $"&breakdowns={Uri.EscapeDataString(breakdowns)}";
        if (timeIncrement) url += "&time_increment=1";
        if (filtering is not null) url += $"&filtering={Uri.EscapeDataString(filtering)}";
        return url;
    }

    /// <summary>
    /// Recorre el paginado. Ante el rate limit de Meta (códigos 4, 17, 32, 613, 80004) espera y
    /// reintenta hasta 3 veces. Devuelve false si terminó con error (lo leído hasta ahí no sirve).
    /// </summary>
    private async Task<bool> PageAsync(HttpClient client, string url, Action<JsonElement> each, CancellationToken ct)
    {
        string? next = url;
        var pages = 0;
        var retries = 0;
        while (next is not null && pages < 400)
        {
            var resp = await client.GetAsync(next, ct);
            var json = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
            {
                if (IsRateLimit(json) && retries++ < 3)
                {
                    _log.LogWarning("Meta geo: rate limit, reintento {N} en {S}s", retries, 60 * retries);
                    await Task.Delay(TimeSpan.FromSeconds(60 * retries), ct);
                    continue;
                }
                _log.LogWarning("Meta geo: Graph {Status}: {Body}", (int)resp.StatusCode, json.Length > 300 ? json[..300] : json);
                return false;
            }
            pages++;
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("data", out var data))
                foreach (var el in data.EnumerateArray()) each(el);
            next = doc.RootElement.TryGetProperty("paging", out var paging) ? Str(paging, "next") : null;
            if (next is not null) await Task.Delay(200, ct);
        }
        return true;
    }

    private static bool IsRateLimit(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("error", out var err) || !err.TryGetProperty("code", out var code)) return false;
            return code.TryGetInt32(out var c) && c is 4 or 17 or 32 or 613 or 80004;
        }
        catch (JsonException) { return false; }
    }

    private static Dictionary<string, long> Actions(JsonElement el)
    {
        var d = new Dictionary<string, long>();
        if (!el.TryGetProperty("actions", out var arr) || arr.ValueKind != JsonValueKind.Array) return d;
        foreach (var a in arr.EnumerateArray())
        {
            var type = Str(a, "action_type");
            if (type is null) continue;
            d[type] = d.GetValueOrDefault(type) + Long(a, "value");
        }
        return d;
    }

    private static string? Str(JsonElement el, string prop)
        => el.ValueKind == JsonValueKind.Object && el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static long Long(JsonElement el, string prop)
    {
        if (!el.TryGetProperty(prop, out var v)) return 0;
        if (v.ValueKind == JsonValueKind.Number) return v.TryGetInt64(out var n) ? n : (long)v.GetDouble();
        return decimal.TryParse(v.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var d) ? (long)Math.Round(d) : 0;
    }

    private static decimal Dec(JsonElement el, string prop)
    {
        if (!el.TryGetProperty(prop, out var v)) return 0;
        if (v.ValueKind == JsonValueKind.Number) return v.GetDecimal();
        return decimal.TryParse(v.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var d) ? d : 0;
    }

    private static string? Trunc(string? s, int max) => s is null ? null : (s.Length <= max ? s : s[..max]);
}
