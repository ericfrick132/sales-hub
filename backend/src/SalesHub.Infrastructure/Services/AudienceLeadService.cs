using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SalesHub.Core.Abstractions;
using SalesHub.Core.Domain.Entities;
using SalesHub.Infrastructure.Persistence;

namespace SalesHub.Infrastructure.Services;

/// <summary>Fila de un listado de la competencia tal como la arma audience_export.py.</summary>
public record AudienceRow(string Dataset, string Competitor, string ProductKey, string? Segment, string ExternalId,
    string Name, string? Category, string? Address, string? City, string? Province, string? PostalCode,
    string? Country, double? Latitude, double? Longitude, string? Phone, string? PhoneType, bool WhatsappProbable,
    string? PhoneSource, string? Instagram, string? Website, string? Email, JsonElement? Raw);

/// <summary>Un paso de la búsqueda de un negocio, con su fecha.</summary>
/// <param name="Step">ig_profile, ig_search, web (extensión) · leads, google (servidor).</param>
/// <param name="Result">ok, no_phone, not_found, no_match, blocked, login, error, distinto…</param>
/// <param name="Detail">@handle, host de la web, fuente del número o mensaje de error.</param>
public record FinderStep(string Step, string Result, string? Detail, DateTimeOffset At);

/// <summary>Resultado de la extensión "Buscar WhatsApp" para un negocio.</summary>
/// <param name="Status">ok (encontró número) / google (no lo encontró en IG ni en la web) / error /
/// blocked o login (IG frenó a mitad: solo se guarda el historial, el negocio sigue pendiente).</param>
/// <param name="Source">instagram_link, instagram_bio, web_whatsapp, web_tel, web_texto.</param>
public record FinderResult(string Dataset, string ExternalId, string Status, string? Phone, string? Source,
    string? Instagram, string? Website, List<FinderStep>? Steps = null, DateTimeOffset? At = null);

public record AudienceSummaryRow(string ProductKey, string Competitor, string Dataset, string Segment, string? Country,
    int Total, int WithPhone, int Whatsapp, int FromCompetitor, int FromInstagram, int FromWeb, int FromLeads,
    int FromGoogle, int FinderDone, int FinderPending, int GooglePending);

public record AudienceRecentRow(string Name, string Dataset, string? City, string? Country, string? Phone,
    bool WhatsappProbable, string? PhoneSource, string? Instagram, DateTimeOffset UpdatedAt);

/// <summary>Último paso por el que pasó un negocio (pantalla de Públicos, "Movimientos").</summary>
public record AudienceEventRow(string Name, string ProductKey, string Dataset, string? City, string? FinderStatus,
    string? Phone, string? PhoneSource, DateTimeOffset? CheckedAt, List<FinderStep> Steps);

/// <summary>
/// Tabla <c>audience_leads</c>: listados de la competencia para públicos de Meta (ver <see cref="AudienceLead"/>).
/// El WhatsApp que falta se busca en este orden: Instagram → web (extensión de Chrome, que manda acá
/// cada resultado) → leads de la base (gratis) → Google Places (pago, solo sobre lo que la extensión ya
/// descartó). Nada de esto crea leads ni encola mensajes.
/// </summary>
public class AudienceLeadService
{
    private readonly ApplicationDbContext _db;
    private readonly IGooglePlacesEnricher _places;
    private readonly IPhoneNormalizer _phone;
    private readonly ILogger<AudienceLeadService> _log;

    public AudienceLeadService(ApplicationDbContext db, IGooglePlacesEnricher places, IPhoneNormalizer phone,
        ILogger<AudienceLeadService> log)
    {
        _db = db; _places = places; _phone = phone; _log = log;
    }

    /// <summary>Clave fija para que la extensión mande resultados sin login: HMAC de la clave del JWT.</summary>
    public static string IngestKey(string jwtSigningKey) =>
        Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(jwtSigningKey), "audience-ingest"u8.ToArray()))[..32]
            .ToLowerInvariant();

    /// <summary>Upsert por (dataset, externalId). Un teléfono ya conseguido no se pisa con un null.</summary>
    public async Task<(int Inserted, int Updated)> ImportAsync(IReadOnlyList<AudienceRow> rows, CancellationToken ct = default)
    {
        int inserted = 0, updated = 0;
        foreach (var chunk in rows.Chunk(500))
        {
            var datasets = chunk.Select(r => r.Dataset).Distinct().ToList();
            var ids = chunk.Select(r => r.ExternalId).ToList();
            var existing = await _db.AudienceLeads
                .Where(a => datasets.Contains(a.Dataset) && ids.Contains(a.ExternalId))
                .ToDictionaryAsync(a => (a.Dataset, a.ExternalId), ct);
            foreach (var r in chunk)
            {
                if (!existing.TryGetValue((r.Dataset, r.ExternalId), out var a))
                {
                    a = new AudienceLead { Dataset = r.Dataset, ExternalId = r.ExternalId };
                    _db.AudienceLeads.Add(a);
                    existing[(r.Dataset, r.ExternalId)] = a;
                    inserted++;
                }
                else updated++;
                a.Competitor = r.Competitor;
                a.ProductKey = r.ProductKey;
                a.Segment = r.Segment ?? "usa";
                a.Name = Cut(r.Name, 256)!;
                a.Category = Cut(r.Category, 256);
                a.Address = Cut(r.Address, 512) ?? a.Address;
                a.City = Cut(r.City, 128) ?? a.City;
                a.Province = Cut(r.Province, 128) ?? a.Province;
                a.PostalCode = Cut(r.PostalCode, 16) ?? a.PostalCode;
                a.Country = r.Country ?? a.Country;
                a.Latitude = r.Latitude ?? a.Latitude;
                a.Longitude = r.Longitude ?? a.Longitude;
                if (!string.IsNullOrWhiteSpace(r.Phone))
                {
                    a.Phone = Cut(r.Phone, 32);
                    a.PhoneType = r.PhoneType;
                    a.WhatsappProbable = r.WhatsappProbable;
                    a.PhoneSource = Cut(r.PhoneSource ?? "competidor", 64);
                }
                a.Instagram = Cut(NormalizeInstagram(r.Instagram), 128) ?? a.Instagram;
                a.Website = Cut(r.Website, 512) ?? a.Website;
                a.Email = Cut(r.Email, 256) ?? a.Email;
                a.RawJson = r.Raw?.GetRawText() ?? a.RawJson;
                a.UpdatedAt = DateTimeOffset.UtcNow;
            }
            await _db.SaveChangesAsync(ct);
            _db.ChangeTracker.Clear();
        }
        return (inserted, updated);
    }

    public record FinderItem(string Dataset, string ExternalId, string Name, string? City, string? Country,
        string? Instagram, string? Website);

    /// <summary>
    /// Orden en que la extensión recorre los listados ("app:dataset"; el mismo dataset puede estar en dos
    /// apps, como AgendaPro). Eric 21/10: primero TurnosPro con AgendaPro.
    /// </summary>
    private static readonly string[] FinderPriority =
    {
        "turnospro:agendapro-ar", "turnospro:fresha-ar", "turnospro:booksy-ar", "turnospro:booksy-mx",
        "gymhero:crossfy-activos", "gymhero:crossfy-sin-clases", "gymhero:agendapro-ar", "gymhero:mindbody-mx-uy",
        "gymhero:totalpass-mx", "playcrew:atc-sin-uso", "playcrew:atc-online", "gymhero:wellhub-ar", "gymhero:wellhub-mx",
        "playcrew:playtomic-mx", "gymhero:fitpass-mx", "gymhero:apps-marca-blanca-ar-uy"
    };

    /// <summary>
    /// Próximos negocios para la extensión: sin teléfono y sin pasar todavía por Instagram/web. Los que
    /// tienen Instagram van primero dentro de cada listado. <paramref name="skip"/> = claves
    /// "dataset|id" que la extensión ya tiene en cola o procesó y todavía no llegaron acá.
    /// </summary>
    public async Task<List<FinderItem>> FinderQueueAsync(int limit, IReadOnlyCollection<string> skip, CancellationToken ct = default)
    {
        var groups = await _db.AudienceLeads.Where(a => a.Phone == null && a.FinderStatus == null)
            .Select(a => new { a.ProductKey, a.Dataset }).Distinct().ToListAsync(ct);
        var ordered = groups.OrderBy(g => Array.IndexOf(FinderPriority, $"{g.ProductKey}:{g.Dataset}") is var i && i >= 0 ? i : 99)
            .ThenBy(g => g.ProductKey).ThenBy(g => g.Dataset);
        var skipSet = skip.ToHashSet();
        var items = new List<FinderItem>();
        foreach (var g in ordered)
        {
            var rows = await _db.AudienceLeads
                .Where(a => a.ProductKey == g.ProductKey && a.Dataset == g.Dataset && a.Phone == null && a.FinderStatus == null)
                .OrderBy(a => a.Instagram == null).ThenBy(a => a.Name)
                .Take(limit + skipSet.Count)
                .Select(a => new FinderItem(a.Dataset, a.ExternalId, a.Name, a.City, a.Country, a.Instagram, a.Website))
                .ToListAsync(ct);
            items.AddRange(rows.Where(r => !skipSet.Contains($"{r.Dataset}|{r.ExternalId}")));
            if (items.Count >= limit) break;
        }
        return items.Take(limit).ToList();
    }

    /// <summary>Lo que manda la extensión. Un número ya cargado (del competidor u otra fuente) no se pisa.</summary>
    public async Task<int> ApplyFinderResultsAsync(IReadOnlyList<FinderResult> results, CancellationToken ct = default)
    {
        var n = 0;
        foreach (var r in results)
        {
            var a = await _db.AudienceLeads.FirstOrDefaultAsync(x => x.Dataset == r.Dataset && x.ExternalId == r.ExternalId, ct);
            if (a is null) continue;
            AppendLog(a, r.Steps);
            n++;
            // Bloqueo, login o error que se reintenta: el negocio sigue en la cola; solo queda el historial.
            if (r.Status is "blocked" or "login" or "retry")
            {
                a.UpdatedAt = DateTimeOffset.UtcNow;
                continue;
            }
            a.FinderStatus = r.Status is "ok" or "google" or "error" ? r.Status : "error";
            a.FinderCheckedAt = r.At ?? DateTimeOffset.UtcNow;
            a.Instagram ??= Cut(NormalizeInstagram(r.Instagram), 128);
            a.Website ??= Cut(r.Website, 512);
            if (r.Status == "ok" && a.Phone is null && !string.IsNullOrWhiteSpace(r.Phone))
                SetPhone(a, r.Phone, r.Source ?? "instagram_bio",
                    waLink: r.Source is "instagram_link" or "web_whatsapp");
            a.UpdatedAt = DateTimeOffset.UtcNow;
        }
        await _db.SaveChangesAsync(ct);
        return n;
    }

    /// <summary>
    /// Completa teléfonos de lo que la extensión ya descartó (FinderStatus = google): primero gratis contra
    /// la tabla leads (mismo Instagram, o mismo nombre a menos de 1 km) y después con Google Places
    /// (~USD 0,03 por búsqueda; cada fila se busca una sola vez). <paramref name="onlyFinderDone"/> = false
    /// salta el orden (para datasets que no van a pasar por la extensión).
    /// </summary>
    public async Task<(int Pending, int FromLeads, int FromGoogle, int Searched)> PlacesAsync(
        string? dataset, int maxGoogle, bool useGoogle = true, bool onlyFinderDone = true, CancellationToken ct = default)
    {
        var pending = await _db.AudienceLeads
            .Where(a => a.Phone == null && a.PlacesCheckedAt == null
                        && (dataset == null || a.Dataset == dataset)
                        && (!onlyFinderDone || a.FinderStatus == "google"))
            .OrderBy(a => a.Dataset).ThenBy(a => a.Name)
            .ToListAsync(ct);
        int fromLeads = 0, fromGoogle = 0, searched = 0;

        foreach (var a in pending)
        {
            var match = await MatchLeadAsync(a, ct);
            if (match is not null)
            {
                SetPhone(a, match.Value.Phone, "lead", waLink: false);
                a.PlaceId ??= match.Value.PlaceId;
                AppendLog(a, new() { new("leads", "ok", "mismo negocio en la base de leads", DateTimeOffset.UtcNow) });
                fromLeads++;
            }
            else if (useGoogle && searched < maxGoogle)
            {
                searched++;
                var p = await _places.EnrichAsync(a.Name, a.Address, a.City, CountryName(a.Country), "es", ct);
                a.PlacesCheckedAt = DateTimeOffset.UtcNow;
                var (result, detail) =
                    p is null ? ("no_match", "Google no lo encontró")
                    : !IsSamePlace(a, p) ? ("distinto", $"otro lugar: {Cut(p.FormattedAddress, 120)}")
                    : string.IsNullOrWhiteSpace(p.Phone) ? ("no_phone", "en Maps sin teléfono")
                    : ("ok", "Google Maps");
                if (result == "ok")
                {
                    SetPhone(a, p!.Phone!, "google_places", waLink: false);
                    a.PlaceId = p.PlaceId;
                    a.Website ??= Cut(p.Website, 512);
                    a.Latitude ??= p.Latitude;
                    a.Longitude ??= p.Longitude;
                    fromGoogle++;
                }
                AppendLog(a, new() { new("google", result, detail, DateTimeOffset.UtcNow) });
            }
            else continue;
            a.UpdatedAt = DateTimeOffset.UtcNow;
            await _db.SaveChangesAsync(ct);
        }
        _log.LogInformation("audience-places {Dataset}: {Pending} pendientes, {Leads} desde leads, {Google} desde Google ({Searched} búsquedas)",
            dataset ?? "*", pending.Count, fromLeads, fromGoogle, searched);
        return (pending.Count, fromLeads, fromGoogle, searched);
    }

    public async Task<List<AudienceSummaryRow>> SummaryAsync(CancellationToken ct = default)
    {
        return await _db.AudienceLeads
            .GroupBy(a => new { a.ProductKey, a.Competitor, a.Dataset, a.Segment, a.Country })
            .Select(g => new AudienceSummaryRow(
                g.Key.ProductKey, g.Key.Competitor, g.Key.Dataset, g.Key.Segment, g.Key.Country,
                g.Count(),
                g.Count(a => a.Phone != null),
                g.Count(a => a.WhatsappProbable),
                g.Count(a => a.PhoneSource == "competidor" || (a.PhoneSource != null && a.PhoneSource.StartsWith("cruce:"))),
                g.Count(a => a.PhoneSource != null && a.PhoneSource.StartsWith("instagram")),
                g.Count(a => a.PhoneSource != null && a.PhoneSource.StartsWith("web")),
                g.Count(a => a.PhoneSource == "lead"),
                g.Count(a => a.PhoneSource == "google_places"),
                g.Count(a => a.FinderStatus != null),
                g.Count(a => a.Phone == null && a.FinderStatus == null),
                g.Count(a => a.Phone == null && a.FinderStatus == "google" && a.PlacesCheckedAt == null)))
            .OrderBy(r => r.ProductKey).ThenBy(r => r.Dataset).ThenBy(r => r.Country)
            .ToListAsync(ct);
    }

    /// <summary>Últimos números conseguidos después de la carga (extensión, leads o Google).</summary>
    public Task<List<AudienceRecentRow>> RecentAsync(int limit, CancellationToken ct = default) =>
        _db.AudienceLeads
            .Where(a => a.Phone != null && a.PhoneSource != null && a.PhoneSource != "competidor" && !a.PhoneSource.StartsWith("cruce:"))
            .OrderByDescending(a => a.UpdatedAt)
            .Take(limit)
            .Select(a => new AudienceRecentRow(a.Name, a.Dataset, a.City, a.Country, a.Phone, a.WhatsappProbable,
                a.PhoneSource, a.Instagram, a.UpdatedAt))
            .ToListAsync(ct);

    /// <summary>Últimos negocios por los que pasó la búsqueda, con todos sus pasos.</summary>
    public async Task<List<AudienceEventRow>> EventsAsync(int limit, CancellationToken ct = default)
    {
        var rows = await _db.AudienceLeads
            .Where(a => a.FinderLog != null)
            .OrderByDescending(a => a.UpdatedAt)
            .Take(limit)
            .Select(a => new { a.Name, a.ProductKey, a.Dataset, a.City, a.FinderStatus, a.Phone, a.PhoneSource, a.FinderCheckedAt, a.FinderLog })
            .ToListAsync(ct);
        return rows.Select(r => new AudienceEventRow(r.Name, r.ProductKey, r.Dataset, r.City, r.FinderStatus, r.Phone,
            r.PhoneSource, r.FinderCheckedAt, ReadLog(r.FinderLog))).ToList();
    }

    private static readonly JsonSerializerOptions LogJson = new(JsonSerializerDefaults.Web);

    private static List<FinderStep> ReadLog(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new();
        try { return JsonSerializer.Deserialize<List<FinderStep>>(json, LogJson) ?? new(); }
        catch (JsonException) { return new(); }
    }

    /// <summary>Agrega pasos al historial del negocio (se guardan los últimos 60).</summary>
    private static void AppendLog(AudienceLead a, List<FinderStep>? steps)
    {
        if (steps is null || steps.Count == 0) return;
        var log = ReadLog(a.FinderLog);
        log.AddRange(steps.Select(s => s with { Detail = Cut(s.Detail, 200), Result = Cut(s.Result, 32)!, Step = Cut(s.Step, 32)! }));
        a.FinderLog = JsonSerializer.Serialize(log.TakeLast(60), LogJson);
    }

    /// <summary>
    /// Guarda el número en E.164. Un link de WhatsApp es WhatsApp seguro; en Argentina un número
    /// marcado como celular (con 15 o 9) también. El resto queda sin clasificar.
    /// </summary>
    private void SetPhone(AudienceLead a, string raw, string source, bool waLink)
    {
        var prefix = a.Country switch { "MX" => "52", "UY" => "598", "CL" => "56", "PE" => "51", _ => "54" };
        var digits = new string(raw.Where(char.IsDigit).ToArray());
        var normalized = waLink && digits.Length >= 10 ? digits : _phone.NormalizeBusiness(raw, prefix) ?? digits;
        if (normalized.Length < 8) return;
        var mobile = waLink || (prefix == "54" && normalized.StartsWith("549"));
        a.Phone = Cut("+" + normalized.TrimStart('+'), 32);
        a.PhoneType = mobile ? "celular" : "desconocido";
        a.WhatsappProbable = mobile;
        a.PhoneSource = Cut(source, 64);
    }

    private async Task<(string Phone, string? PlaceId)?> MatchLeadAsync(AudienceLead a, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(a.Instagram))
        {
            var byIg = await _db.Leads
                .Where(l => l.InstagramHandle != null && l.InstagramHandle.ToLower() == a.Instagram
                            && (l.WhatsappPhone != null || l.RawPhone != null))
                .Select(l => new { Phone = l.WhatsappPhone ?? l.RawPhone, l.PlaceId }).FirstOrDefaultAsync(ct);
            if (byIg is not null) return (byIg.Phone!, byIg.PlaceId);
        }
        if (a.Latitude is null || a.Longitude is null) return null;
        const double box = 0.01; // ~1 km
        var near = await _db.Leads
            .Where(l => (l.WhatsappPhone != null || l.RawPhone != null) && l.Latitude != null && l.Longitude != null
                        && l.Latitude > a.Latitude - box && l.Latitude < a.Latitude + box
                        && l.Longitude > a.Longitude - box && l.Longitude < a.Longitude + box)
            .Select(l => new { l.Name, Phone = l.WhatsappPhone ?? l.RawPhone, l.PlaceId }).ToListAsync(ct);
        var key = NormalizeName(a.Name);
        var hit = near.FirstOrDefault(l => key.Length >= 4 && NormalizeName(l.Name) == key);
        return hit is null ? null : (hit.Phone!, hit.PlaceId);
    }

    /// <summary>Google devuelve el primer candidato aunque sea otro negocio: se acepta solo si queda cerca o es de la misma ciudad.</summary>
    private static bool IsSamePlace(AudienceLead a, EnrichedPlace p)
    {
        if (a.Latitude is { } la && a.Longitude is { } lo && p.Latitude is { } pla && p.Longitude is { } plo)
            return Km(la, lo, pla, plo) < 1.5;
        var city = NormalizeName(a.City);
        return city.Length >= 4 && NormalizeName(p.FormattedAddress).Contains(city);
    }

    private static double Km(double a, double b, double c, double d)
    {
        double R(double x) => x * Math.PI / 180;
        var h = Math.Sin(R(c - a) / 2) * Math.Sin(R(c - a) / 2)
                + Math.Cos(R(a)) * Math.Cos(R(c)) * Math.Sin(R(d - b) / 2) * Math.Sin(R(d - b) / 2);
        return 12742 * Math.Asin(Math.Sqrt(h));
    }

    private static string? Cut(string? s, int max) => s is null ? null : s.Length <= max ? s : s[..max];

    private static string CountryName(string? iso) => iso switch
    {
        "MX" => "México", "UY" => "Uruguay", "CL" => "Chile", "PE" => "Perú", _ => "Argentina"
    };

    private static string? NormalizeInstagram(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var m = Regex.Match(s, @"instagram\.com/([A-Za-z0-9_.]+)");
        var h = (m.Success ? m.Groups[1].Value : s).Trim().TrimStart('@').Trim('.').ToLowerInvariant();
        return h.Length == 0 || h is "p" or "reel" ? null : h;
    }

    private static readonly string[] Generic =
        { "crossfit", "cross", "box", "gym", "gimnasio", "training", "club", "fitness", "centro", "studio", "estudio", "the", "de", "el", "la" };

    private static string NormalizeName(string? s)
    {
        var d = (s ?? "").ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var plain = new string(d.Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark).ToArray());
        return string.Concat(Regex.Split(plain, "[^a-z0-9]+").Where(w => w.Length > 0 && !Generic.Contains(w)));
    }
}
