using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SalesHub.Core.Domain.Entities.Promos;
using SalesHub.Core.Domain.Entities.Social;
using SalesHub.Infrastructure.Options;
using SalesHub.Infrastructure.Persistence;

namespace SalesHub.Infrastructure.Services.Promos;

/// <summary>
/// Motor de promos por calendario. En cada tick, por cada PromoEvent habilitado cuya ventana
/// (Date - LeadDays … EndDate, días AR) incluye hoy, y por cada app del evento:
///   1) baja/guarda la imagen del evento (una sola vez),
///   2) genera el copy de la landing con Claude usando la ficha REAL del producto,
///   3) POST {app}/api/hub/promos → la app crea cupón + promo y devuelve su /promo,
///   4) deja la PromoCampaign Live (el SocialContentWorker fuerza 1 posteo de promo por día).
/// Al pasar EndDate: DELETE {app}/api/hub/promos/{slug} y la campaña queda Ended.
/// Contrato con las apps: docs/promo-calendar/CONTRATO-APPS.md.
/// Config: Promos:DefaultDiscountPercent (20), Promos:DefaultDurationMonths (2),
/// Promos:Apps:{productKey}:BaseUrl (si no, se deriva de TenantMetrics:Sources), key = Hub:ApiKey.
/// </summary>
public class PromoEngine
{
    public const string BioUtm = "utm_source=instagram&utm_medium=social&utm_content=bio";
    private const int MaxAttempts = 6;

    private readonly ApplicationDbContext _db;
    private readonly IHttpClientFactory _httpFactory;
    private readonly IConfiguration _config;
    private readonly ClaudeClient _claude;
    private readonly ImageGenOptions _img;
    private readonly ILogger<PromoEngine> _log;

    public PromoEngine(ApplicationDbContext db, IHttpClientFactory httpFactory, IConfiguration config, ClaudeClient claude,
        IOptions<ImageGenOptions> img, ILogger<PromoEngine> log)
    {
        _db = db; _httpFactory = httpFactory; _config = config; _claude = claude; _img = img.Value; _log = log;
    }

    public int DefaultDiscount => _config.GetValue<int?>("Promos:DefaultDiscountPercent") ?? 20;
    public int DefaultMonths => _config.GetValue<int?>("Promos:DefaultDurationMonths") ?? 2;

    public static DateOnly TodayAr() => LeadEntryService.ArDay(DateTimeOffset.UtcNow);

    /// <summary>Inicio del día AR (00:00) en UTC.</summary>
    public static DateTimeOffset ArStart(DateOnly d)
    {
        var local = d.ToDateTime(TimeOnly.MinValue);
        return new DateTimeOffset(local, LeadEntryService.ArTz.GetUtcOffset(local)).ToUniversalTime();
    }

    public static DateOnly WindowStart(PromoEvent e) => e.Date.AddDays(-Math.Max(0, e.LeadDays));
    public static DateTimeOffset StartsAt(PromoEvent e) => ArStart(WindowStart(e));
    public static DateTimeOffset EndsAt(PromoEvent e) => ArStart(e.EndDate.AddDays(1)).AddSeconds(-1);

    // ── Apps ──────────────────────────────────────────────────────────────

    /// <summary>ProductKey → base de la API de la app. Explícito en Promos:Apps o derivado de TenantMetrics:Sources.</summary>
    public Dictionary<string, string> AppBases()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in _config.GetSection("TenantMetrics:Sources").GetChildren())
        {
            var key = c["ProductKey"]; var url = (c["Url"] ?? "").Trim();
            if (string.IsNullOrWhiteSpace(key) || url == "") continue;
            var i = url.IndexOf("/api/", StringComparison.OrdinalIgnoreCase);
            map[key] = (i > 0 ? url[..i] : url).TrimEnd('/');
        }
        foreach (var c in _config.GetSection("Promos:Apps").GetChildren())
        {
            var url = (c["BaseUrl"] ?? "").Trim();
            if (url != "") map[c.Key] = url.TrimEnd('/');
        }
        return map;
    }

    public IReadOnlyList<string> AppsFor(PromoEvent e, Dictionary<string, string> bases) =>
        (e.Apps.Count == 0 ? bases.Keys.ToList() : e.Apps.Where(bases.ContainsKey).ToList())
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    // ── Tick ──────────────────────────────────────────────────────────────

    public record TickResult(int Launched, int Ended, int Errors);

    public async Task<TickResult> TickAsync(CancellationToken ct)
    {
        await PromoCalendarSeed.EnsureSeededAsync(_db, ct);
        var today = TodayAr();
        var bases = AppBases();
        int launched = 0, ended = 0, errors = 0;

        // 1) Terminar las vencidas.
        var nowUtc = DateTimeOffset.UtcNow;
        var toEnd = await _db.PromoCampaigns.Include(c => c.Event)
            .Where(c => c.Status == PromoCampaignStatus.Live && c.EndsAt < nowUtc)
            .ToListAsync(ct);
        foreach (var c in toEnd)
        {
            if (await EndAsync(c, bases, ct)) ended++;
        }

        // 2) Lanzar las que entran en ventana.
        var events = await _db.PromoEvents.Include(e => e.Campaigns)
            .Where(e => e.Enabled && e.EndDate >= today)
            .ToListAsync(ct);
        foreach (var ev in events.Where(e => WindowStart(e) <= today))
        {
            foreach (var app in AppsFor(ev, bases))
            {
                var existing = ev.Campaigns.FirstOrDefault(c => c.ProductKey.Equals(app, StringComparison.OrdinalIgnoreCase));
                if (existing is { Status: PromoCampaignStatus.Live or PromoCampaignStatus.Ended }) continue;
                if (existing is { Status: PromoCampaignStatus.Error } && existing.Attempts >= MaxAttempts) continue;
                var c = await LaunchAsync(ev, app, bases, ct);
                if (c.Status == PromoCampaignStatus.Live) launched++; else errors++;
            }
        }
        return new TickResult(launched, ended, errors);
    }

    // ── Lanzar una campaña ────────────────────────────────────────────────

    public async Task<PromoCampaign> LaunchAsync(PromoEvent ev, string productKey, Dictionary<string, string>? bases, CancellationToken ct)
    {
        bases ??= AppBases();
        var c = await _db.PromoCampaigns.FirstOrDefaultAsync(x => x.PromoEventId == ev.Id && x.ProductKey == productKey, ct);
        if (c == null)
        {
            c = new PromoCampaign { Id = Guid.NewGuid(), PromoEventId = ev.Id, ProductKey = productKey };
            _db.PromoCampaigns.Add(c);
        }
        c.CouponCode = CodeFor(ev, DefaultDiscount);
        c.DiscountPercent = ev.DiscountPercent ?? DefaultDiscount;
        c.DurationMonths = ev.DurationMonths ?? DefaultMonths;
        c.StartsAt = StartsAt(ev);
        c.EndsAt = EndsAt(ev);
        c.Attempts++;
        c.UpdatedAt = DateTimeOffset.UtcNow;

        try
        {
            if (!bases.TryGetValue(productKey, out var baseUrl))
                throw new InvalidOperationException($"No hay URL de la app '{productKey}' (Promos:Apps o TenantMetrics:Sources).");

            var imageUrl = await EnsureImageAsync(ev, ct);

            // El copy se genera una sola vez por campaña (si ya hay, se reusa en los reintentos).
            if (string.IsNullOrWhiteSpace(c.Headline))
            {
                var copy = await GenerateCopyAsync(ev, productKey, c.DiscountPercent, c.DurationMonths, c.CouponCode, ct);
                c.Headline = copy.Headline; c.Subheadline = copy.Subheadline; c.Bullets = copy.Bullets;
            }

            var landing = await PushToAppAsync(baseUrl, ev, c, imageUrl, ct);
            c.LandingUrl = landing;
            c.TrackedUrl = TrackedUrl(landing, ev.Slug, c.CouponCode);
            c.Status = PromoCampaignStatus.Live;
            c.Error = null;
            c.LaunchedAt ??= DateTimeOffset.UtcNow;
            _log.LogInformation("Promo {Slug} LIVE en {App}: {Url}", ev.Slug, productKey, c.TrackedUrl);
        }
        catch (Exception ex)
        {
            c.Status = PromoCampaignStatus.Error;
            c.Error = ex.Message.Length > 2000 ? ex.Message[..2000] : ex.Message;
            _log.LogWarning(ex, "Promo {Slug} falló en {App} (intento {N})", ev.Slug, productKey, c.Attempts);
        }
        await _db.SaveChangesAsync(ct);
        return c;
    }

    public async Task<bool> EndAsync(PromoCampaign c, Dictionary<string, string>? bases, CancellationToken ct)
    {
        bases ??= AppBases();
        var slug = c.Event?.Slug ?? await _db.PromoEvents.Where(e => e.Id == c.PromoEventId).Select(e => e.Slug).FirstAsync(ct);
        try
        {
            if (bases.TryGetValue(c.ProductKey, out var baseUrl))
            {
                using var req = new HttpRequestMessage(HttpMethod.Delete, $"{baseUrl}/api/hub/promos/{Uri.EscapeDataString(slug)}");
                AddKey(req);
                var resp = await Http().SendAsync(req, ct);
                if (!resp.IsSuccessStatusCode && (int)resp.StatusCode != 404)
                    throw new InvalidOperationException($"DELETE promo HTTP {(int)resp.StatusCode}");
            }
            c.Status = PromoCampaignStatus.Ended;
            c.EndedAt = DateTimeOffset.UtcNow;
            c.UpdatedAt = DateTimeOffset.UtcNow;
            await _db.SaveChangesAsync(ct);
            return true;
        }
        catch (Exception ex)
        {
            // El cupón igual vence solo (ExpiresAt = endsAt); reintentamos el DELETE el próximo tick.
            _log.LogWarning(ex, "No pude cerrar la promo {Slug} en {App}", slug, c.ProductKey);
            c.Error = ex.Message;
            await _db.SaveChangesAsync(ct);
            return false;
        }
    }

    // ── Piezas ────────────────────────────────────────────────────────────

    private HttpClient Http()
    {
        var h = _httpFactory.CreateClient("promos");
        h.Timeout = TimeSpan.FromSeconds(40);
        return h;
    }

    private void AddKey(HttpRequestMessage req)
    {
        var key = _config["Hub:ApiKey"];
        if (string.IsNullOrWhiteSpace(key)) throw new InvalidOperationException("Hub:ApiKey no configurado");
        req.Headers.Add("X-Api-Key", key);
    }

    private async Task<string> PushToAppAsync(string baseUrl, PromoEvent ev, PromoCampaign c, string? imageUrl, CancellationToken ct)
    {
        var payload = new
        {
            slug = ev.Slug,
            code = c.CouponCode,
            title = ev.Title,
            headline = c.Headline,
            subheadline = c.Subheadline,
            bullets = c.Bullets,
            description = ev.Description,
            discountPercent = c.DiscountPercent,
            durationMonths = c.DurationMonths,
            startsAt = c.StartsAt.UtcDateTime,
            endsAt = c.EndsAt.UtcDateTime,
            imageUrl,
            imageCredit = ev.ImageCredit,
        };
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/api/hub/promos") { Content = JsonContent.Create(payload) };
        AddKey(req);
        var resp = await Http().SendAsync(req, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"POST {baseUrl}/api/hub/promos → HTTP {(int)resp.StatusCode}: {Trunc(body, 300)}");
        using var doc = JsonDocument.Parse(body);
        var landing = doc.RootElement.TryGetProperty("landingUrl", out var l) ? l.GetString() : null;
        if (string.IsNullOrWhiteSpace(landing)) throw new InvalidOperationException("La app no devolvió landingUrl");
        return landing.Trim();
    }

    public static string TrackedUrl(string landing, string slug, string code)
    {
        var sep = landing.Contains('?') ? "&" : "?";
        return $"{landing}{sep}utm_source=instagram&utm_medium=social&utm_campaign={Uri.EscapeDataString(slug)}&utm_content=bio&cupon={Uri.EscapeDataString(code)}";
    }

    public static string BioUrl(string landing)
    {
        var sep = landing.Contains('?') ? "&" : "?";
        return $"{landing}{sep}{BioUtm}";
    }

    private static readonly HashSet<string> CodeStopWords = new(StringComparer.OrdinalIgnoreCase)
    { "dia", "del", "de", "la", "el", "los", "las", "mundial", "y", "a", "ano", "san" };

    /// <summary>Código del cupón: el override del evento, o la palabra clave del slug + el %, ej. "cyber-monday-2026" → CYBER20.</summary>
    public static string CodeFor(PromoEvent ev, int defaultDiscount)
    {
        if (!string.IsNullOrWhiteSpace(ev.CouponCode)) return ev.CouponCode.Trim().ToUpperInvariant();
        var pct = ev.DiscountPercent ?? defaultDiscount;
        var word = ev.Slug.Split('-', StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault(w => !CodeStopWords.Contains(w) && !w.All(char.IsDigit)) ?? "PROMO";
        word = new string(word.Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();
        if (word.Length > 10) word = word[..10];
        return $"{word}{pct}";
    }

    /// <summary>Baja la foto pública del evento una sola vez y la guarda como asset (URL estable nuestra).</summary>
    public async Task<string?> EnsureImageAsync(PromoEvent ev, CancellationToken ct)
    {
        if (ev.ImageAssetId is { } id) return AssetUrl(id);
        if (string.IsNullOrWhiteSpace(ev.ImageSourceUrl)) return null;

        using var req = new HttpRequestMessage(HttpMethod.Get, ev.ImageSourceUrl);
        req.Headers.UserAgent.ParseAdd("SalesHubPromos/1.0 (+https://sales.efcloud.tech)");
        var resp = await Http().SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode) throw new InvalidOperationException($"No pude bajar la imagen ({(int)resp.StatusCode})");
        var bytes = await resp.Content.ReadAsByteArrayAsync(ct);
        var mime = resp.Content.Headers.ContentType?.MediaType ?? "image/jpeg";
        if (!mime.StartsWith("image/")) throw new InvalidOperationException($"La imagen no es una imagen ({mime})");

        var asset = new SocialPostAsset { Id = Guid.NewGuid(), MimeType = mime, Content = bytes, SizeBytes = bytes.LongLength };
        _db.SocialPostAssets.Add(asset);
        ev.ImageAssetId = asset.Id;
        ev.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);
        return AssetUrl(asset.Id);
    }

    public string AssetUrl(Guid id) => $"{_img.PublicBaseUrl.TrimEnd('/')}/api/posteos/assets/{id}.jpg";

    public record PromoCopy(string Headline, string Subheadline, List<string> Bullets);

    /// <summary>
    /// Copy de la landing con Claude, anclado a la ficha REAL del producto (PostingProfile.LandingKnowledge)
    /// para no prometer features que no existen. Sin Claude → copy por defecto.
    /// </summary>
    public async Task<PromoCopy> GenerateCopyAsync(PromoEvent ev, string productKey, int pct, int months, string code, CancellationToken ct)
    {
        var mesesTxt = months == 1 ? "el primer mes" : $"los primeros {months} meses";
        var fallback = new PromoCopy(
            $"{ev.Title}: {pct}% off {mesesTxt}",
            $"Usá el código {code} y empezá a ordenar tu negocio con descuento. Válido hasta el {ev.EndDate:dd/MM}.",
            new List<string>());
        if (!_claude.IsConfigured) return fallback;

        var profile = await _db.PostingProfiles.AsNoTracking().FirstOrDefaultAsync(p => p.ProductKey == productKey, ct);
        var sys = new StringBuilder();
        sys.AppendLine($"Escribís el copy de una landing de promo para el producto '{productKey}', un software B2B (le vende a dueños de negocios, no a consumidores).");
        if (profile != null)
        {
            sys.AppendLine($"Audiencia: {profile.TargetAudience}");
            sys.AppendLine($"Voz de marca: {profile.BrandVoice}");
            if (!string.IsNullOrWhiteSpace(profile.LandingKnowledge))
            {
                sys.AppendLine("FICHA REAL DEL PRODUCTO (usá SOLO esto para beneficios; si algo no está acá, no existe):");
                sys.AppendLine(profile.LandingKnowledge);
            }
        }
        sys.AppendLine("Reglas: español rioplatense con voseo, ortografía perfecta, sin emojis, sin signos de apertura '¿' ni '¡'. " +
                       "Nada de precios (los pone la página), nada de 'gratis para siempre', nada de 'se cobra solo'. " +
                       "Conectá la fecha con el día a día del dueño del negocio con naturalidad, sin forzar. " +
                       "Respondé SOLO un JSON: {\"headline\": \"máx 9 palabras, incluye el % off\", \"subheadline\": \"1-2 frases, máx 30 palabras\", \"bullets\": [\"3 beneficios reales y concretos, máx 12 palabras cada uno\"]}");
        var user = $"Evento: {ev.Title} ({ev.Date:dd/MM/yyyy}{(ev.EndDate != ev.Date ? $" al {ev.EndDate:dd/MM}" : "")}). {ev.Description}\n" +
                   $"Promo: {pct}% off {mesesTxt} con el código {code}, válido hasta el {ev.EndDate:dd/MM}.";
        try
        {
            var raw = await _claude.CompleteAsync(sys.ToString(), user, 800, null, "promos", ct);
            var m = raw == null ? null : Regex.Match(raw, @"\{[\s\S]*\}");
            if (m is not { Success: true }) return fallback;
            using var doc = JsonDocument.Parse(m.Value);
            var r = doc.RootElement;
            var head = r.TryGetProperty("headline", out var h) ? h.GetString() : null;
            var sub = r.TryGetProperty("subheadline", out var s) ? s.GetString() : null;
            var bullets = r.TryGetProperty("bullets", out var b) && b.ValueKind == JsonValueKind.Array
                ? b.EnumerateArray().Select(x => x.GetString() ?? "").Where(x => x != "").Take(4).ToList()
                : new List<string>();
            return new PromoCopy(
                string.IsNullOrWhiteSpace(head) ? fallback.Headline : head.Trim(),
                string.IsNullOrWhiteSpace(sub) ? fallback.Subheadline : sub.Trim(),
                bullets);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Copy de promo con Claude falló ({Slug}/{App}) — uso el default", ev.Slug, productKey);
            return fallback;
        }
    }

    /// <summary>Pista para el generador de posteos mientras la campaña está Live.</summary>
    public static string PostHint(PromoEvent ev, PromoCampaign c)
    {
        var meses = c.DurationMonths == 1 ? "el primer mes" : $"los primeros {c.DurationMonths} meses";
        return $"PROMO ACTIVA — este posteo ES el anuncio de la promo de {ev.Title} ({ev.Description}). " +
               $"Oferta: {c.DiscountPercent}% off {meses} con el código {c.CouponCode}, válida hasta el {ev.EndDate:dd/MM}. " +
               $"El caption TIENE que nombrar el código {c.CouponCode} y el {c.DiscountPercent}% y cerrar con 'link en bio'. " +
               $"El overlay de la imagen: corto, con el % y el código (ej. '{c.DiscountPercent}% OFF con {c.CouponCode}'). No inventes precios.";
    }

    private static string Trunc(string s, int n) => s.Length <= n ? s : s[..n];
}
