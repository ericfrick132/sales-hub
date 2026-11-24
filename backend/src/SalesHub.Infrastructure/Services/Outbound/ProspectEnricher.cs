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
        { "p", "explore", "reel", "reels", "stories", "accounts", "about", "legal", "developer", "direct", "tv", "whatsapp", "graphql", "instagram" };

    /// <summary>Links de la bio que no tiene sentido abrir (redes, no dicen cómo toman turnos).</summary>
    private static readonly string[] SocialHosts =
        { "threads.net", "threads.com", "tiktok.com", "youtube.com", "youtu.be", "facebook.com", "fb.com", "twitter.com", "x.com/", "spotify.com", "instagram.com" };

    /// <summary>Lo que el puntaje necesita del resto de la base, cargado una vez por tanda.</summary>
    public sealed class Batch
    {
        /// <summary>Últimos 8 dígitos del teléfono → listado de la competencia donde aparece.</summary>
        public Dictionary<string, string> CompetitorBySuffix { get; init; } = new();
        /// <summary>Clave de cadena (ProspectScorer.ChainKey) → cuántos leads del producto la comparten.</summary>
        public Dictionary<string, int> ChainCounts { get; init; } = new();
        /// <summary>Cuenta de Instagram → cuántos leads del producto la comparten (sucursales de una cadena).</summary>
        public Dictionary<string, int> InstagramCounts { get; init; } = new(StringComparer.OrdinalIgnoreCase);
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
            var name = DatasetName(p.Dataset);
            if (s is not null && name is not null) competitor.TryAdd(s, name);
        }

        var names = await _db.Leads.AsNoTracking()
            .Where(l => l.ProductKey == productKey)
            .Select(l => l.Name)
            .ToListAsync(ct);
        var chains = names.Select(n => ProspectScorer.ChainKey(n, productKey)).Where(k => k.Length > 0)
            .GroupBy(k => k).Where(g => g.Count() >= 2)
            .ToDictionary(g => g.Key, g => g.Count());

        var handles = await _db.Leads.AsNoTracking()
            .Where(l => l.ProductKey == productKey && l.InstagramHandle != null && l.InstagramHandle != "")
            .Select(l => l.InstagramHandle!)
            .ToListAsync(ct);
        var igCounts = handles.GroupBy(h => h.Trim().TrimStart('@').ToLowerInvariant())
            .Where(g => g.Count() >= 2)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);

        return new Batch { CompetitorBySuffix = competitor, ChainCounts = chains, InstagramCounts = igCounts };
    }

    /// <summary>Lee la web, cruza con la competencia y recalcula. No guarda: lo persiste quien llama.</summary>
    public async Task EnrichAsync(Lead lead, Batch batch, CancellationToken ct)
    {
        string? waFromWeb = null;
        string? platform = ProspectScorer.BookingPlatformIn(lead.Website, lead.ProductKey);

        var kind = ProspectScorer.WebsiteKind(lead.Website, lead.ProductKey);
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
                platform ??= ProspectScorer.BookingPlatformIn(html, lead.ProductKey);
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

        // Lo verificado en Instagram no lo pisa la web (salvo que la web también publique el WhatsApp).
        var fromWeb = PhoneOriginFor(lead, waFromWeb);
        if (lead.PhoneOrigin != "whatsapp-instagram" || fromWeb == "whatsapp-publicado") lead.PhoneOrigin = fromWeb;

        // Cruce con los listados de la competencia (si aparece ahí, reserva con ese sistema).
        var suffix = Suffix(lead.WhatsappPhone);
        if (platform is null && suffix is not null && batch.CompetitorBySuffix.TryGetValue(suffix, out var ds))
            platform = ds;
        lead.BookingPlatform = platform;

        lead.ProspectEnrichedAt = DateTimeOffset.UtcNow;
        Rescore(lead, batch);
    }

    /// <summary>
    /// Lo que se leyó del perfil de Instagram del negocio (con la sesión del Chrome de Eric, desde Claude in
    /// Chrome): el texto de la cabecera (bio incluida), los links de la bio ya decodificados ("wa:549..."
    /// para los de WhatsApp) y los seguidores.
    /// </summary>
    public sealed record InstagramProfile(Guid LeadId, string Handle, bool Found, string? Header,
        List<string>? Links, int? Followers);

    [GeneratedRegex(@"\b(whats?app|whatsap|wh?sp|wpp|wapp|wa)\b", RegexOptions.IgnoreCase)]
    private static partial Regex MentionsWhatsappRx();

    [GeneratedRegex(@"(\+?\d[\d\s\-\.\(\)]{7,}\d)")]
    private static partial Regex LoosePhoneRx();

    private static readonly string[] LinkHubs =
        { "linktr.ee", "linktree", "beacons.ai", "taplink", "bio.link", "linkin.bio", "lnk.bio", "campsite.bio", "canva.site", "my.canva", "wa.link", "bit.ly", "msha.ke", "carrd.co", "solo.to", "agora.red" };

    /// <summary>
    /// Aplica el perfil de Instagram: si el link de la bio (o lo que hay detrás de un Linktree/Canva/wa.link)
    /// es un WhatsApp, ese es el número donde toman turnos → "whatsapp-instagram" (verificado). Si la bio dice
    /// WhatsApp con un número, también. Si el link es AgendaPro/Fresha/etc., usa la competencia. Recalcula.
    /// No guarda: lo persiste quien llama.
    /// </summary>
    public async Task ApplyInstagramAsync(Lead lead, InstagramProfile p, Batch batch, CancellationToken ct)
    {
        lead.InstagramCheckedAt = DateTimeOffset.UtcNow;
        lead.UpdatedAt = DateTimeOffset.UtcNow;
        if (!p.Found)
        {
            Rescore(lead, batch);
            return;
        }
        if (string.IsNullOrWhiteSpace(lead.InstagramHandle)) lead.InstagramHandle = p.Handle.Trim().TrimStart('@');
        if (p.Followers is not null) lead.InstagramFollowers = p.Followers;

        var links = (p.Links ?? new()).Select(l => (l ?? "").Trim()).Where(l => l.Length > 0).Distinct().ToList();
        if (links.Count > 0) lead.InstagramBioLink = links[0].Length > 512 ? links[0][..512] : links[0];

        string? wa = null;
        string? platform = null;
        foreach (var link in links)
        {
            if (link.StartsWith("wa:", StringComparison.Ordinal)) { wa ??= link[3..]; continue; }
            platform ??= ProspectScorer.BookingPlatformIn(link, lead.ProductKey);
            var m = WaLinkRx().Match(link);
            if (m.Success) wa ??= m.Groups[1].Value;
        }

        // Linktree, web de Canva, wa.link...: se abre (gratis, HTTP plano) y se busca el WhatsApp adentro.
        foreach (var link in links.Where(l => !l.StartsWith("wa:", StringComparison.Ordinal)
                     && !SocialHosts.Any(h => l.Contains(h, StringComparison.OrdinalIgnoreCase))))
        {
            if (wa is not null && platform is not null) break;
            var lower = link.ToLowerInvariant();
            if (!LinkHubs.Any(lower.Contains) && ProspectScorer.WebsiteKind(link, lead.ProductKey) != "propia") continue;
            if (!Uri.TryCreate(link.StartsWith("http") ? link : "https://" + link, UriKind.Absolute, out var url)) continue;
            var (finalUrl, html) = await FetchWithUrlAsync(url, ct);
            var hay = $"{finalUrl} {html}";
            platform ??= ProspectScorer.BookingPlatformIn(hay, lead.ProductKey);
            var m = WaLinkRx().Match(hay);
            if (m.Success) wa ??= m.Groups[1].Value;
        }

        var header = p.Header ?? "";
        platform ??= ProspectScorer.BookingPlatformIn(header, lead.ProductKey);
        var saysWhatsapp = MentionsWhatsappRx().IsMatch(header);
        if (wa is null && saysWhatsapp)
        {
            // "Turnos por WhatsApp 11 2345-6789": el número de la bio.
            var phone = LoosePhoneRx().Matches(header).Select(x => x.Value)
                .FirstOrDefault(x => x.Count(char.IsDigit) is >= 10 and <= 13);
            if (phone is not null) wa = phone;
        }

        if (wa is not null)
        {
            var norm = _phone.NormalizeBusiness(wa, "54");
            if (!string.IsNullOrWhiteSpace(norm))
            {
                lead.InstagramPhone = norm;
                if (Suffix(norm) != Suffix(lead.WhatsappPhone))
                {
                    // Se le escribe al número que publican para WhatsApp (salvo que ya sea de otro lead de la app).
                    var taken = await _db.Leads.AnyAsync(l => l.ProductKey == lead.ProductKey && l.WhatsappPhone == norm && l.Id != lead.Id, ct);
                    if (!taken)
                    {
                        lead.RawPhone ??= wa;
                        lead.WhatsappPhone = norm;
                        lead.WhatsappValidated = false;
                        lead.WhatsappJid = null;
                    }
                }
                if (Suffix(norm) == Suffix(lead.WhatsappPhone) && lead.PhoneOrigin != "whatsapp-publicado")
                    lead.PhoneOrigin = "whatsapp-instagram";
            }
        }
        else if (saysWhatsapp && !string.IsNullOrWhiteSpace(lead.WhatsappPhone) && lead.PhoneOrigin != "whatsapp-publicado")
        {
            // La bio dice WhatsApp pero sin número: toman turnos por WhatsApp, al número de Maps.
            lead.PhoneOrigin = "whatsapp-instagram";
        }

        if (platform is not null) lead.BookingPlatform ??= platform;
        Rescore(lead, batch);
    }

    private async Task<(string FinalUrl, string Html)> FetchWithUrlAsync(Uri url, CancellationToken ct)
    {
        try
        {
            using var resp = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            var final = resp.RequestMessage?.RequestUri?.ToString() ?? url.ToString();
            if (!resp.IsSuccessStatusCode) return (final, "");
            var type = resp.Content.Headers.ContentType?.MediaType ?? "";
            if (type.Length > 0 && !type.Contains("html", StringComparison.OrdinalIgnoreCase)) return (final, "");
            var html = await resp.Content.ReadAsStringAsync(ct);
            return (final, html.Length > 600_000 ? html[..600_000] : html);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _log.LogDebug(ex, "Prospecto: no pude abrir el link de la bio {Url}", url);
            return (url.ToString(), "");
        }
    }

    /// <summary>Recalcula el puntaje con lo que el lead ya tiene (sin leer la web).</summary>
    public static void Rescore(Lead lead, Batch batch)
    {
        if (lead.ProspectEnrichedAt is null && lead.PhoneOrigin is null)
            lead.PhoneOrigin = PhoneOriginFor(lead, null);
        var key = ProspectScorer.ChainKey(lead.Name, lead.ProductKey);
        var same = key.Length > 0 && batch.ChainCounts.TryGetValue(key, out var c) ? c : 1;
        // La misma cuenta de Instagram en varias sucursales también es cadena (Depilife, Definit, Umara...).
        var ig = (lead.InstagramHandle ?? "").Trim().TrimStart('@');
        if (ig.Length > 0 && batch.InstagramCounts.TryGetValue(ig, out var n) && n > same) same = n;
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

    /// <summary>
    /// El sistema que usa quien figura en ese listado, o null si el listado no dice que use uno: los de beneficios
    /// (Wellhub, Gympass, TotalPass, Fitpass no son el sistema del gimnasio) y los de quienes ya se fueron
    /// ("crossfy-sin-clases", "atc-sin-uso": al revés, son buenos prospectos).
    /// </summary>
    private static string? DatasetName(string dataset) => dataset.ToLowerInvariant() switch
    {
        var d when d.Contains("wellhub") || d.Contains("gympass") || d.Contains("totalpass") || d.Contains("fitpass") => null,
        var d when d.Contains("sin-") => null,
        var d when d.StartsWith("agendapro") => "AgendaPro (listado)",
        var d when d.StartsWith("fresha") => "Fresha (listado)",
        var d when d.StartsWith("booksy") => "Booksy (listado)",
        var d when d.StartsWith("crossfy") => "Crossfy (listado)",
        var d when d.StartsWith("accesogym") => "AccesoGym (listado)",
        var d when d.StartsWith("mindbody") => "Mindbody (listado)",
        var d => $"{d} (listado)",
    };
}
