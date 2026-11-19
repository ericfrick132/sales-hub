using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SalesHub.Core.Abstractions;
using SalesHub.Core.Domain.Entities;
using SalesHub.Infrastructure.Persistence;

namespace SalesHub.Infrastructure.Services.Outbound;

/// <summary>
/// "Nutre" a un lead para el puntaje de prospecto: lee su web (gratis, HTTP plano) buscando el link
/// de WhatsApp (wa.me), Instagram, Facebook y si reserva con un sistema de la competencia; lo cruza con
/// los listados de la competencia (audience_leads: Fresha, AgendaPro, Booksy) por teléfono; decide de
/// dónde salió el número; y recalcula el puntaje. No manda nada ni gasta créditos de Apify/Google.
/// </summary>
public partial class ProspectEnricher
{
    private readonly HttpClient _http;
    private readonly ApplicationDbContext _db;
    private readonly IPhoneNormalizer _phone;
    private readonly ILogger<ProspectEnricher> _log;

    public ProspectEnricher(HttpClient http, ApplicationDbContext db, IPhoneNormalizer phone, ILogger<ProspectEnricher> log)
    {
        _http = http; _db = db; _phone = phone; _log = log;
        _http.Timeout = TimeSpan.FromSeconds(12);
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0 Safari/537.36");
    }

    [GeneratedRegex(@"(?:wa\.me/|api\.whatsapp\.com/send/?\?phone=|whatsapp\.com/send/?\?phone=|wa\.link/)(\+?\d{8,15})", RegexOptions.IgnoreCase)]
    private static partial Regex WaLinkRx();

    [GeneratedRegex(@"instagram\.com/([A-Za-z0-9_\.]{2,30})", RegexOptions.IgnoreCase)]
    private static partial Regex InstagramRx();

    [GeneratedRegex(@"https?://(?:www\.|m\.)?facebook\.com/(?:pg/)?([A-Za-z0-9\.\-_]{2,})", RegexOptions.IgnoreCase)]
    private static partial Regex FacebookRx();

    private static readonly HashSet<string> IgNoise = new(StringComparer.OrdinalIgnoreCase)
        { "p", "explore", "reel", "reels", "stories", "accounts", "about", "legal", "developer", "direct", "tv" };

    /// <summary>Lo que el puntaje necesita del resto de la base, cargado una vez por tanda.</summary>
    public sealed class Batch
    {
        /// <summary>Últimos 8 dígitos del teléfono → listado de la competencia donde aparece.</summary>
        public Dictionary<string, string> CompetitorBySuffix { get; init; } = new();
        /// <summary>Clave de cadena (ProspectScorer.ChainKey) → cuántos leads del producto la comparten.</summary>
        public Dictionary<string, int> ChainCounts { get; init; } = new();
    }

    public async Task<Batch> LoadBatchAsync(string productKey, CancellationToken ct)
    {
        var phones = await _db.AudienceLeads.AsNoTracking()
            .Where(a => a.ProductKey == productKey && a.Phone != null)
            .Select(a => new { a.Phone, a.Dataset })
            .ToListAsync(ct);
        var competitor = new Dictionary<string, string>();
        foreach (var p in phones)
        {
            var s = Suffix(p.Phone);
            if (s is not null) competitor.TryAdd(s, DatasetName(p.Dataset));
        }

        var names = await _db.Leads.AsNoTracking()
            .Where(l => l.ProductKey == productKey)
            .Select(l => l.Name)
            .ToListAsync(ct);
        var chains = names.Select(ProspectScorer.ChainKey).Where(k => k.Length > 0)
            .GroupBy(k => k).Where(g => g.Count() >= 2)
            .ToDictionary(g => g.Key, g => g.Count());

        return new Batch { CompetitorBySuffix = competitor, ChainCounts = chains };
    }

    /// <summary>Lee la web, cruza con la competencia y recalcula. No guarda: lo persiste quien llama.</summary>
    public async Task EnrichAsync(Lead lead, Batch batch, CancellationToken ct)
    {
        string? waFromWeb = null;
        string? platform = ProspectScorer.BookingPlatformIn(lead.Website);

        var kind = ProspectScorer.WebsiteKind(lead.Website);
        if (kind == "instagram" && string.IsNullOrWhiteSpace(lead.InstagramHandle))
        {
            var m = InstagramRx().Match(lead.Website!);
            if (m.Success && !IgNoise.Contains(m.Groups[1].Value)) lead.InstagramHandle = m.Groups[1].Value.TrimEnd('.');
        }
        if (kind == "facebook" && string.IsNullOrWhiteSpace(lead.FacebookUrl)) lead.FacebookUrl = lead.Website;

        // Solo se lee la web propia o el link en bio: a Instagram/Facebook no se les puede leer sin login.
        if (kind is "propia" or "link-en-bio" or "reservas" && Uri.TryCreate(lead.Website, UriKind.Absolute, out var url))
        {
            var html = await FetchAsync(url, ct);
            if (html is not null)
            {
                platform ??= ProspectScorer.BookingPlatformIn(html);
                var wa = WaLinkRx().Match(html);
                if (wa.Success) waFromWeb = wa.Groups[1].Value;
                if (string.IsNullOrWhiteSpace(lead.InstagramHandle))
                {
                    foreach (Match m in InstagramRx().Matches(html))
                    {
                        var h = m.Groups[1].Value.TrimEnd('.');
                        if (h.Length >= 3 && !IgNoise.Contains(h)) { lead.InstagramHandle = h; break; }
                    }
                }
                if (string.IsNullOrWhiteSpace(lead.FacebookUrl))
                {
                    foreach (Match m in FacebookRx().Matches(html))
                    {
                        var slug = m.Groups[1].Value;
                        if (slug is "sharer" or "tr" or "dialog" or "plugins" or "sharer.php") continue;
                        lead.FacebookUrl = $"https://facebook.com/{slug}";
                        break;
                    }
                }
            }
        }

        // Sin teléfono de Maps pero con WhatsApp publicado en la web: ese es el número.
        if (string.IsNullOrWhiteSpace(lead.WhatsappPhone) && waFromWeb is not null)
        {
            lead.RawPhone ??= waFromWeb;
            lead.WhatsappPhone = _phone.NormalizeBusiness(waFromWeb, "54");
        }

        lead.PhoneOrigin = PhoneOriginFor(lead, waFromWeb);

        // Cruce con los listados de la competencia (si aparece ahí, reserva con ese sistema).
        var suffix = Suffix(lead.WhatsappPhone);
        if (platform is null && suffix is not null && batch.CompetitorBySuffix.TryGetValue(suffix, out var ds))
            platform = ds;
        lead.BookingPlatform = platform;

        lead.ProspectEnrichedAt = DateTimeOffset.UtcNow;
        Rescore(lead, batch);
    }

    /// <summary>Recalcula el puntaje con lo que el lead ya tiene (sin leer la web).</summary>
    public static void Rescore(Lead lead, Batch batch)
    {
        if (lead.ProspectEnrichedAt is null && lead.PhoneOrigin is null)
            lead.PhoneOrigin = PhoneOriginFor(lead, null);
        var key = ProspectScorer.ChainKey(lead.Name);
        var same = key.Length > 0 && batch.ChainCounts.TryGetValue(key, out var c) ? c : 1;
        var r = ProspectScorer.Score(lead, new ProspectScorer.Context(same));
        lead.ProspectScore = r.Score;
        lead.ProspectRubro = r.RubroKey;
        lead.ProspectScoreDetail = ProspectScorer.DetailJson(r);
        lead.ProspectScoredAt = DateTimeOffset.UtcNow;
    }

    private static readonly PhoneNormalizerShim Normalizer = new();

    private static string? PhoneOriginFor(Lead lead, string? waFromWeb)
    {
        if (string.IsNullOrWhiteSpace(lead.WhatsappPhone)) return null;
        var mine = Suffix(lead.WhatsappPhone);
        if (waFromWeb is not null && Suffix(waFromWeb) == mine) return "whatsapp-publicado";
        if (!string.IsNullOrWhiteSpace(lead.InstagramPhone) && Suffix(lead.InstagramPhone) == mine) return "instagram";
        if (waFromWeb is not null && string.IsNullOrWhiteSpace(lead.RawPhone)) return "whatsapp-publicado";
        return Normalizer.IsMobile(lead.RawPhone ?? lead.WhatsappPhone) ? "maps-celular" : "maps-fijo";
    }

    /// <summary>La detección de celular de PhoneNormalizer es pura; se usa sin pasar por DI.</summary>
    private sealed class PhoneNormalizerShim
    {
        private readonly PhoneNormalizer _n = new();
        public bool IsMobile(string? raw) => _n.IsMarkedArMobile(raw);
    }

    private async Task<string?> FetchAsync(Uri url, CancellationToken ct)
    {
        try
        {
            using var resp = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!resp.IsSuccessStatusCode) return null;
            var type = resp.Content.Headers.ContentType?.MediaType ?? "";
            if (type.Length > 0 && !type.Contains("html", StringComparison.OrdinalIgnoreCase)) return null;
            var html = await resp.Content.ReadAsStringAsync(ct);
            return html.Length > 600_000 ? html[..600_000] : html;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _log.LogDebug(ex, "Prospecto: no pude leer {Url}", url);
            return null;
        }
    }

    /// <summary>Últimos 8 dígitos: la parte estable del número en todos los formatos.</summary>
    public static string? Suffix(string? phone)
    {
        var d = new string((phone ?? "").Where(char.IsDigit).ToArray());
        return d.Length >= 8 ? d[^8..] : null;
    }

    private static string DatasetName(string dataset) => dataset.ToLowerInvariant() switch
    {
        var d when d.StartsWith("agendapro") => "AgendaPro (listado)",
        var d when d.StartsWith("fresha") => "Fresha (listado)",
        var d when d.StartsWith("booksy") => "Booksy (listado)",
        var d => $"{d} (listado)",
    };
}
