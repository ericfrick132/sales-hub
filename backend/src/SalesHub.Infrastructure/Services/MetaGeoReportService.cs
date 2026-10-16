using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using SalesHub.Core.Domain.Entities;
using SalesHub.Core.Domain.Enums;
using SalesHub.Infrastructure.Persistence;

namespace SalesHub.Infrastructure.Services;

/// <summary>Formato del anuncio inferido de su nombre ("GymHero - musculacion - antes y después").</summary>
public static class AdFormat
{
    public const string Bot = "bot";
    public const string AntesDespues = "antes-despues";
    public const string Pov = "pov";
    public const string MismaCuadra = "misma-cuadra";
    public const string Otro = "otro";

    public static readonly IReadOnlyDictionary<string, string> Labels = new Dictionary<string, string>
    {
        [Bot] = "Bot", [AntesDespues] = "Antes y después", [Pov] = "POV", [MismaCuadra] = "Misma cuadra", [Otro] = "Otro",
    };

    public static string Infer(string? adName)
    {
        var n = " " + ArRegions.Key(adName) + " ";
        if (n.Contains(" misma cuadra ")) return MismaCuadra;
        if (n.Contains(" pov ")) return Pov;
        if (n.Contains(" antes ") && n.Contains(" despues ")) return AntesDespues;
        if (n.Contains(" bot ")) return Bot;
        return Otro;
    }
}

/// <summary>Filtros del mapa de anuncios (todos opcionales; listas vacías = sin filtro).</summary>
public record MetaGeoQuery(
    DateOnly? From, DateOnly? To,
    IReadOnlyList<string> Products, IReadOnlyList<string> CampaignIds, IReadOnlyList<string> AdIds,
    IReadOnlyList<string> Formats, IReadOnlyList<string> Regions);

/// <summary>Métricas de una porción (total, provincia, anuncio, formato, día). Los ratios son null sin denominador.</summary>
public record GeoMetrics(
    decimal Spend, long Impressions, long Reach, bool ReachExact, double? Frequency,
    long Clicks, long UniqueClicks, long LinkClicks, double? Ctr, decimal? Cpc, decimal? Cpm,
    long VideoViews3s, double? HookRate,
    long Conversations, decimal? CostPerConversation, long FirstReplies, long Depth2, long Depth3,
    int Leads, int LeadsQualified, int LeadsClosed, decimal? CostPerLead);

/// <summary>
/// Arma el mapa de anuncios por provincia sobre <see cref="MetaGeoDaily"/> + los leads de sales-hub
/// que vinieron de anuncios (provincia por código de área, ver <see cref="ArAreaCodes"/>).
/// El alcance del rango se pide en vivo a Meta (cache 15 min); si falla, suma diaria marcada aprox.
/// </summary>
public class MetaGeoReportService
{
    private static readonly LeadStatus[] Qualified = { LeadStatus.Interested, LeadStatus.DemoScheduled, LeadStatus.Closed };

    private readonly ApplicationDbContext _db;
    private readonly MetaGeoSyncService _sync;
    private readonly IMemoryCache _cache;
    private readonly ILogger<MetaGeoReportService> _log;

    public MetaGeoReportService(ApplicationDbContext db, MetaGeoSyncService sync, IMemoryCache cache, ILogger<MetaGeoReportService> log)
    {
        _db = db; _sync = sync; _cache = cache; _log = log;
    }

    // ── filtros ─────────────────────────────────────────────────────────────────────────

    public async Task<object> FiltersAsync(CancellationToken ct)
    {
        var q = _db.MetaGeoDaily.AsNoTracking();
        var campaigns = await q.GroupBy(x => x.CampaignId)
            .Select(g => new { id = g.Key, name = g.Max(x => x.CampaignName), productKey = g.Max(x => x.ProductKey),
                               spend = g.Sum(x => x.Spend), lastDate = g.Max(x => x.Date) })
            .ToListAsync(ct);
        var ads = await q.GroupBy(x => x.AdId)
            .Select(g => new { id = g.Key, name = g.Max(x => x.AdName), productKey = g.Max(x => x.ProductKey),
                               campaignId = g.Max(x => x.CampaignId), spend = g.Sum(x => x.Spend), lastDate = g.Max(x => x.Date) })
            .ToListAsync(ct);
        var regions = await q.GroupBy(x => new { x.Region, x.Country }).Select(g => new { g.Key.Region, g.Key.Country }).ToListAsync(ct);
        var minDate = await q.MinAsync(x => (DateOnly?)x.Date, ct);
        var maxDate = await q.MaxAsync(x => (DateOnly?)x.Date, ct);
        var lastSyncAt = await q.MaxAsync(x => (DateTimeOffset?)x.SyncedAt, ct);
        var keys = campaigns.Select(c => c.productKey).Concat(ads.Select(a => a.productKey))
            .Where(k => k != null).Distinct().ToList();
        var products = await _db.Products.AsNoTracking().Where(p => keys.Contains(p.ProductKey))
            .Select(p => new { key = p.ProductKey, displayName = p.DisplayName }).ToListAsync(ct);

        return new
        {
            configured = _sync.IsConfigured,
            lastSyncAt,
            minDate,
            maxDate,
            today = MetaGeoSyncService.TodayAr(),
            products = products.OrderBy(p => p.displayName),
            campaigns = campaigns.OrderByDescending(c => c.lastDate).ThenByDescending(c => c.spend),
            ads = ads.Select(a => new { a.id, a.name, a.productKey, a.campaignId, format = AdFormat.Infer(a.name), a.spend, a.lastDate })
                .OrderByDescending(a => a.lastDate).ThenByDescending(a => a.spend),
            formats = AdFormat.Labels.Select(kv => new { key = kv.Key, label = kv.Value }),
            // Todas las provincias (aunque no tengan datos) + lo que vino de afuera.
            regions = ArRegions.All.Select(r => new { name = r, country = "AR" })
                .Concat(regions.Where(r => !(r.Country == "AR" && ArRegions.Canonical(r.Region) is not null))
                    .Select(r => new { name = r.Region, country = r.Country }))
                .OrderBy(r => r.country != "AR").ThenBy(r => r.name),
        };
    }

    // ── resumen ─────────────────────────────────────────────────────────────────────────

    public async Task<object> SummaryAsync(MetaGeoQuery q, CancellationToken ct)
    {
        var today = MetaGeoSyncService.TodayAr();
        var to = q.To ?? today;
        var from = q.From ?? to.AddDays(-29);
        if (from > to) (from, to) = (to, from);
        var days = to.DayNumber - from.DayNumber + 1;

        // 1) Filas del rango con los filtros "de anuncio" (app, campaña, anuncio, formato).
        var rowsQ = _db.MetaGeoDaily.AsNoTracking().Where(x => x.Date >= from && x.Date <= to);
        if (q.Products.Count > 0) rowsQ = rowsQ.Where(x => x.ProductKey != null && q.Products.Contains(x.ProductKey));
        if (q.CampaignIds.Count > 0) rowsQ = rowsQ.Where(x => q.CampaignIds.Contains(x.CampaignId));
        if (q.AdIds.Count > 0) rowsQ = rowsQ.Where(x => q.AdIds.Contains(x.AdId));
        var rows = await rowsQ.ToListAsync(ct);
        if (q.Formats.Count > 0) rows = rows.Where(x => q.Formats.Contains(AdFormat.Infer(x.AdName))).ToList();

        var regionKeys = q.Regions.Select(ArRegions.Key).Where(k => k.Length > 0).ToHashSet();
        bool InRegion(string region) => regionKeys.Count == 0 || regionKeys.Contains(ArRegions.Key(region));

        // 2) Leads de sales-hub que vinieron de un anuncio, con los mismos filtros.
        var adInfo = await _db.MetaGeoDaily.AsNoTracking().GroupBy(x => x.AdId)
            .Select(g => new { AdId = g.Key, Name = g.Max(x => x.AdName), CampaignId = g.Max(x => x.CampaignId),
                               CampaignName = g.Max(x => x.CampaignName), ProductKey = g.Max(x => x.ProductKey) })
            .ToDictionaryAsync(x => x.AdId, ct);
        var metaAdNames = await _db.MetaAds.AsNoTracking().ToDictionaryAsync(a => a.AdId, a => a.Name, ct);
        var fromUtc = new DateTimeOffset(from.ToDateTime(TimeOnly.MinValue), TimeSpan.FromHours(-3));
        var toUtc = new DateTimeOffset(to.AddDays(1).ToDateTime(TimeOnly.MinValue), TimeSpan.FromHours(-3));
        var leadsQ = _db.Leads.AsNoTracking().Where(l => l.AdId != null && l.CreatedAt >= fromUtc && l.CreatedAt < toUtc);
        if (q.Products.Count > 0) leadsQ = leadsQ.Where(l => q.Products.Contains(l.ProductKey));
        if (q.AdIds.Count > 0) leadsQ = leadsQ.Where(l => q.AdIds.Contains(l.AdId!));
        var rawLeads = await leadsQ
            .Select(l => new { l.AdId, l.ProductKey, l.WhatsappPhone, l.RawPhone, l.Province, l.CreatedAt, l.Status })
            .ToListAsync(ct);
        var leads = rawLeads.Select(l =>
        {
            adInfo.TryGetValue(l.AdId!, out var info);
            var (country, province) = ArAreaCodes.Resolve(l.WhatsappPhone ?? l.RawPhone);
            province ??= country is null or "AR" ? ArRegions.Canonical(l.Province) : null;
            var name = info?.Name ?? metaAdNames.GetValueOrDefault(l.AdId!) ?? string.Empty;
            return new
            {
                AdId = l.AdId!, l.ProductKey, CampaignId = info?.CampaignId, Format = AdFormat.Infer(name),
                Country = country ?? "AR", Province = province,
                Date = DateOnly.FromDateTime(l.CreatedAt.ToOffset(TimeSpan.FromHours(-3)).DateTime), l.Status,
            };
        })
        .Where(l => q.CampaignIds.Count == 0 || (l.CampaignId != null && q.CampaignIds.Contains(l.CampaignId)))
        .Where(l => q.Formats.Count == 0 || q.Formats.Contains(l.Format))
        .ToList();

        // 3) Alcance exacto en vivo (por provincia y total) para estos filtros.
        var live = await LiveReachAsync(q, rows, from, to, ct);

        // ── por provincia (el mapa no se filtra por provincia: la selección se resalta) ──
        var regionRows = rows.GroupBy(x => (x.Region, x.Country)).ToDictionary(g => g.Key, g => g.ToList());
        foreach (var r in ArRegions.All) regionRows.TryAdd((r, "AR"), new List<MetaGeoDaily>());
        var leadsByProvince = leads.Where(l => l.Country == "AR" && l.Province != null).GroupBy(l => l.Province!)
            .ToDictionary(g => g.Key, g => g.Select(l => l.Status).ToList());
        var regions = regionRows.Select(kv =>
        {
            var (region, country) = kv.Key;
            var isAr = country == "AR" && ArRegions.Canonical(region) is not null;
            var acc = Acc.Of(kv.Value);
            if (isAr && leadsByProvince.TryGetValue(region, out var st)) acc.AddLeads(st);
            long? exact = live is not null ? live.ByRegion.GetValueOrDefault(region) : null;
            return new { region, country, inArgentina = isAr, selected = regionKeys.Contains(ArRegions.Key(region)), m = acc.ToMetrics(exact) };
        })
        .Where(r => r.inArgentina || r.m.Impressions > 0 || r.m.Spend > 0)
        .OrderByDescending(r => r.m.Spend).ThenBy(r => r.region)
        .ToList();

        // ── el resto sí respeta la provincia elegida ──
        var fr = rows.Where(x => InRegion(x.Region)).ToList();
        var fl = leads.Where(l => regionKeys.Count == 0 || (l.Province != null && regionKeys.Contains(ArRegions.Key(l.Province)))).ToList();

        var totalAcc = Acc.Of(fr);
        totalAcc.AddLeads(fl.Select(l => l.Status));
        long? totalReach = live is null ? null
            : regionKeys.Count == 0 ? live.Total
            : live.ByRegion.Where(kv => regionKeys.Contains(ArRegions.Key(kv.Key))).Sum(kv => kv.Value);
        var totals = totalAcc.ToMetrics(totalReach, exact: live is not null && regionKeys.Count == 0);

        var leadsByDay = fl.GroupBy(l => l.Date).ToDictionary(g => g.Key, g => g.Count());
        var rowsByDay = fr.GroupBy(x => x.Date).ToDictionary(g => g.Key, g => g.ToList());
        var daily = Enumerable.Range(0, days).Select(i => from.AddDays(i)).Select(d =>
        {
            var dr = rowsByDay.GetValueOrDefault(d) ?? new List<MetaGeoDaily>();
            return new
            {
                date = d,
                spend = dr.Sum(x => x.Spend),
                impressions = dr.Sum(x => x.Impressions),
                clicks = dr.Sum(x => x.Clicks),
                conversations = dr.Sum(x => x.MessagingConnections),
                leads = leadsByDay.GetValueOrDefault(d),
            };
        }).ToList();

        var leadsByAd = fl.GroupBy(l => l.AdId).ToDictionary(g => g.Key, g => g.Select(l => l.Status).ToList());
        var rowsByAd = fr.GroupBy(x => x.AdId).ToDictionary(g => g.Key, g => g.OrderBy(x => x.Date).ToList());
        var adIds = rowsByAd.Keys.Concat(leadsByAd.Keys).Distinct();
        var ads = adIds.Select(id =>
        {
            var ar = rowsByAd.GetValueOrDefault(id) ?? new List<MetaGeoDaily>();
            var acc = Acc.Of(ar);
            if (leadsByAd.TryGetValue(id, out var st)) acc.AddLeads(st);
            var info = adInfo.GetValueOrDefault(id);
            var name = ar.LastOrDefault()?.AdName ?? info?.Name ?? metaAdNames.GetValueOrDefault(id) ?? id;
            return new
            {
                adId = id,
                adName = name,
                campaignId = ar.LastOrDefault()?.CampaignId ?? info?.CampaignId,
                campaignName = ar.LastOrDefault()?.CampaignName ?? info?.CampaignName,
                productKey = ar.LastOrDefault()?.ProductKey ?? info?.ProductKey ?? fl.FirstOrDefault(l => l.AdId == id)?.ProductKey,
                format = AdFormat.Infer(name),
                m = acc.ToMetrics(null),
            };
        }).OrderByDescending(a => a.m.Spend).ToList();

        var formats = ads.GroupBy(a => a.format).Select(g =>
        {
            var ids = g.Select(a => a.adId).ToHashSet();
            var acc = Acc.Of(fr.Where(x => ids.Contains(x.AdId)));
            acc.AddLeads(fl.Where(l => ids.Contains(l.AdId)).Select(l => l.Status));
            return new { format = g.Key, label = AdFormat.Labels.GetValueOrDefault(g.Key, g.Key), ads = ids.Count, m = acc.ToMetrics(null) };
        }).OrderByDescending(f => f.m.Spend).ToList();

        return new
        {
            from, to, days,
            reachSource = live is null ? "daily-sum" : "meta-live",
            lastSyncAt = await _db.MetaGeoDaily.AsNoTracking().MaxAsync(x => (DateTimeOffset?)x.SyncedAt, ct),
            totals,
            spendPerDay = days > 0 ? Math.Round(totals.Spend / days, 2) : 0,
            leadsByStatus = fl.GroupBy(l => l.Status.ToString()).ToDictionary(g => g.Key, g => g.Count()),
            leadsUnknownRegion = fl.Count(l => l.Country == "AR" && l.Province == null),
            leadsOutsideAr = fl.GroupBy(l => l.Country).Where(g => g.Key != "AR").Select(g => new { country = g.Key, leads = g.Count() }),
            daily,
            regions,
            ads,
            formats,
        };
    }

    private async Task<MetaGeoSyncService.LiveReach?> LiveReachAsync(MetaGeoQuery q, List<MetaGeoDaily> rows, DateOnly from, DateOnly to, CancellationToken ct)
    {
        if (rows.Count == 0) return null;
        var adFiltered = q.Products.Count > 0 || q.CampaignIds.Count > 0 || q.AdIds.Count > 0 || q.Formats.Count > 0;
        var byAccount = rows.GroupBy(x => x.AccountId).ToDictionary(
            g => g.Key,
            g => adFiltered ? (IReadOnlyCollection<string>?)g.Select(x => x.AdId).Distinct().OrderBy(x => x).ToList() : null);
        var key = "meta-geo-reach:" + from + ":" + to + ":" + string.Join("|",
            byAccount.OrderBy(kv => kv.Key).Select(kv => kv.Key + "=" + (kv.Value is null ? "*" : string.Join(",", kv.Value))));
        if (_cache.TryGetValue(key, out MetaGeoSyncService.LiveReach? cached)) return cached;
        try
        {
            var live = await _sync.LiveReachAsync(byAccount, from, to, ct);
            if (live is not null) _cache.Set(key, live, TimeSpan.FromMinutes(15));
            return live;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex, "Meta geo: alcance en vivo falló, se usa la suma diaria");
            return null;
        }
    }

    /// <summary>Acumulador de métricas de varias filas diarias + leads.</summary>
    private sealed class Acc
    {
        decimal _spend; long _impr, _reach, _clicks, _uclicks, _link, _video, _conv, _first, _d2, _d3;
        int _leads, _qualified, _closed;

        public static Acc Of(IEnumerable<MetaGeoDaily> rows)
        {
            var a = new Acc();
            foreach (var r in rows)
            {
                a._spend += r.Spend; a._impr += r.Impressions; a._reach += r.Reach; a._clicks += r.Clicks;
                a._uclicks += r.UniqueClicks; a._link += r.LinkClicks; a._video += r.VideoViews3s;
                a._conv += r.MessagingConnections; a._first += r.FirstReplies; a._d2 += r.Depth2; a._d3 += r.Depth3;
            }
            return a;
        }

        public void AddLeads(IEnumerable<LeadStatus> statuses)
        {
            foreach (var s in statuses)
            {
                _leads++;
                if (Qualified.Contains(s)) _qualified++;
                if (s == LeadStatus.Closed) _closed++;
            }
        }

        /// <param name="exactReach">Alcance único de Meta; null = suma de los diarios (aprox.).</param>
        public GeoMetrics ToMetrics(long? exactReach, bool? exact = null)
        {
            var reach = exactReach ?? _reach;
            return new GeoMetrics(
                Spend: Math.Round(_spend, 2), Impressions: _impr, Reach: reach, ReachExact: exact ?? exactReach is not null,
                Frequency: reach > 0 ? Math.Round((double)_impr / reach, 2) : null,
                Clicks: _clicks, UniqueClicks: _uclicks, LinkClicks: _link,
                Ctr: _impr > 0 ? Math.Round(100.0 * _clicks / _impr, 2) : null,
                Cpc: _clicks > 0 ? Math.Round(_spend / _clicks, 4) : null,
                Cpm: _impr > 0 ? Math.Round(_spend * 1000 / _impr, 4) : null,
                VideoViews3s: _video,
                HookRate: _impr > 0 ? Math.Round(100.0 * _video / _impr, 2) : null,
                Conversations: _conv,
                CostPerConversation: _conv > 0 ? Math.Round(_spend / _conv, 4) : null,
                FirstReplies: _first, Depth2: _d2, Depth3: _d3,
                Leads: _leads, LeadsQualified: _qualified, LeadsClosed: _closed,
                CostPerLead: _leads > 0 ? Math.Round(_spend / _leads, 4) : null);
        }
    }
}
