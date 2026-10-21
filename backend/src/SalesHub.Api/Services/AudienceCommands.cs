using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SalesHub.Core.Abstractions;
using SalesHub.Core.Domain.Entities;
using SalesHub.Infrastructure.Persistence;

namespace SalesHub.Api.Services;

/// <summary>
/// Comandos de consola para la tabla <c>audience_leads</c> (listados de la competencia para públicos
/// de Meta; ver <see cref="AudienceLead"/>). Corren dentro del contenedor de la API con su config,
/// sin levantar el server ni los workers:
///   dotnet SalesHub.Api.dll audience-import /tmp/audience.json
///   dotnet SalesHub.Api.dll audience-places [--dataset crossfy-activos] [--max 200] [--no-google]
/// audience-import recibe [{dataset, competitor, productKey, segment, externalId, name, …, raw}] y hace
/// upsert por (dataset, externalId) sin pisar un teléfono ya conseguido con un null.
/// audience-places completa teléfonos faltantes: primero gratis contra la tabla leads (mismo Instagram,
/// o mismo nombre a menos de 1 km) y después con Google Places (~USD 0,03 por búsqueda; cada fila se
/// busca una sola vez). Nunca crea leads ni encola mensajes.
/// </summary>
public static class AudienceCommands
{
    public static bool IsCommand(string[] args) =>
        args.Length > 0 && args[0] is "audience-import" or "audience-places";

    public static async Task<int> RunAsync(IServiceProvider services, string[] args)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return args[0] == "audience-import"
            ? await ImportAsync(db, args[1])
            : await PlacesAsync(db, scope.ServiceProvider.GetRequiredService<IGooglePlacesEnricher>(), args);
    }

    private record Row(string Dataset, string Competitor, string ProductKey, string? Segment, string ExternalId,
        string Name, string? Category, string? Address, string? City, string? Province, string? PostalCode,
        string? Country, double? Latitude, double? Longitude, string? Phone, string? PhoneType, bool WhatsappProbable,
        string? PhoneSource, string? Instagram, string? Website, string? Email, JsonElement? Raw);

    private static async Task<int> ImportAsync(ApplicationDbContext db, string path)
    {
        var rows = JsonSerializer.Deserialize<List<Row>>(await File.ReadAllTextAsync(path),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new();
        int inserted = 0, updated = 0;
        foreach (var chunk in rows.Chunk(500))
        {
            var datasets = chunk.Select(r => r.Dataset).Distinct().ToList();
            var ids = chunk.Select(r => r.ExternalId).ToList();
            var existing = await db.AudienceLeads
                .Where(a => datasets.Contains(a.Dataset) && ids.Contains(a.ExternalId))
                .ToDictionaryAsync(a => (a.Dataset, a.ExternalId));
            foreach (var r in chunk)
            {
                if (!existing.TryGetValue((r.Dataset, r.ExternalId), out var a))
                {
                    a = new AudienceLead { Dataset = r.Dataset, ExternalId = r.ExternalId };
                    db.AudienceLeads.Add(a);
                    existing[(r.Dataset, r.ExternalId)] = a;
                    inserted++;
                }
                else updated++;
                a.Competitor = r.Competitor;
                a.ProductKey = r.ProductKey;
                a.Segment = r.Segment ?? "usa";
                a.Name = r.Name;
                a.Category = r.Category;
                a.Address = r.Address;
                a.City = r.City;
                a.Province = r.Province;
                a.PostalCode = r.PostalCode;
                a.Country = r.Country;
                a.Latitude = r.Latitude ?? a.Latitude;
                a.Longitude = r.Longitude ?? a.Longitude;
                if (!string.IsNullOrWhiteSpace(r.Phone))
                {
                    a.Phone = r.Phone;
                    a.PhoneType = r.PhoneType;
                    a.WhatsappProbable = r.WhatsappProbable;
                    a.PhoneSource = r.PhoneSource ?? "competidor";
                }
                a.Instagram = NormalizeInstagram(r.Instagram) ?? a.Instagram;
                a.Website = r.Website ?? a.Website;
                a.Email = r.Email ?? a.Email;
                a.RawJson = r.Raw?.GetRawText();
                a.UpdatedAt = DateTimeOffset.UtcNow;
            }
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();
        }
        Console.WriteLine($"audience-import: {rows.Count} filas, {inserted} nuevas, {updated} actualizadas");
        return 0;
    }

    private static async Task<int> PlacesAsync(ApplicationDbContext db, IGooglePlacesEnricher places, string[] args)
    {
        string? Opt(string name) => args.SkipWhile(a => a != name).Skip(1).FirstOrDefault();
        var dataset = Opt("--dataset");
        var max = int.TryParse(Opt("--max"), out var m) ? m : 100;
        var useGoogle = !args.Contains("--no-google");

        var pending = await db.AudienceLeads
            .Where(a => a.Phone == null && a.PlacesCheckedAt == null && (dataset == null || a.Dataset == dataset))
            .OrderBy(a => a.Dataset).ThenBy(a => a.Name)
            .ToListAsync();
        int fromLeads = 0, fromGoogle = 0, searched = 0;

        foreach (var a in pending)
        {
            // 1) Gratis: la base de leads ya tiene miles de negocios con teléfono sacados de Google Maps.
            var match = await MatchLeadAsync(db, a);
            if (match is not null)
            {
                a.Phone = match.Value.Phone;
                a.PlaceId ??= match.Value.PlaceId;
                a.PhoneSource = "lead";
                fromLeads++;
            }
            else if (useGoogle && searched < max)
            {
                searched++;
                var p = await places.EnrichAsync(a.Name, a.Address, a.City, CountryName(a.Country), "es");
                a.PlacesCheckedAt = DateTimeOffset.UtcNow;
                if (p is not null && IsSamePlace(a, p) && !string.IsNullOrWhiteSpace(p.Phone))
                {
                    a.Phone = Digits(p.Phone);
                    a.PlaceId = p.PlaceId;
                    a.Website ??= p.Website;
                    a.Latitude ??= p.Latitude;
                    a.Longitude ??= p.Longitude;
                    a.PhoneSource = "google_places";
                    fromGoogle++;
                }
            }
            else continue;
            a.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();
        }
        Console.WriteLine($"audience-places: {pending.Count} sin teléfono; {fromLeads} desde leads, " +
                          $"{fromGoogle} desde Google ({searched} búsquedas)");
        return 0;
    }

    private static async Task<(string Phone, string? PlaceId)?> MatchLeadAsync(ApplicationDbContext db, AudienceLead a)
    {
        if (!string.IsNullOrWhiteSpace(a.Instagram))
        {
            var byIg = await db.Leads
                .Where(l => l.InstagramHandle != null && l.InstagramHandle.ToLower() == a.Instagram
                            && (l.WhatsappPhone != null || l.RawPhone != null))
                .Select(l => new { Phone = l.WhatsappPhone ?? l.RawPhone, l.PlaceId }).FirstOrDefaultAsync();
            if (byIg is not null) return (Digits(byIg.Phone!), byIg.PlaceId);
        }
        if (a.Latitude is null || a.Longitude is null) return null;
        const double box = 0.01; // ~1 km
        var near = await db.Leads
            .Where(l => (l.WhatsappPhone != null || l.RawPhone != null) && l.Latitude != null && l.Longitude != null
                        && l.Latitude > a.Latitude - box && l.Latitude < a.Latitude + box
                        && l.Longitude > a.Longitude - box && l.Longitude < a.Longitude + box)
            .Select(l => new { l.Name, Phone = l.WhatsappPhone ?? l.RawPhone, l.PlaceId }).ToListAsync();
        var key = NormalizeName(a.Name);
        var hit = near.FirstOrDefault(l => key.Length >= 4 && NormalizeName(l.Name) == key);
        return hit is null ? null : (Digits(hit.Phone!), hit.PlaceId);
    }

    /// <summary>Google devuelve el primer candidato aunque sea otro negocio: se acepta solo si queda cerca o el nombre coincide.</summary>
    private static bool IsSamePlace(AudienceLead a, EnrichedPlace p)
    {
        if (a.Latitude is { } la && a.Longitude is { } lo && p.Latitude is { } pla && p.Longitude is { } plo)
            return Km(la, lo, pla, plo) < 1.5;
        // Sin coordenadas propias: al menos que la dirección de Google sea de la misma ciudad.
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

    private static string Digits(string s) => "+" + new string(s.Where(char.IsDigit).ToArray()).TrimStart('0');

    private static string CountryName(string? iso) => iso switch
    {
        "MX" => "México", "UY" => "Uruguay", "CL" => "Chile", "PE" => "Perú", _ => "Argentina"
    };

    private static string? NormalizeInstagram(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var m = System.Text.RegularExpressions.Regex.Match(s, @"instagram\.com/([A-Za-z0-9_.]+)");
        var h = (m.Success ? m.Groups[1].Value : s).Trim().TrimStart('@').Trim('.').ToLowerInvariant();
        return h.Length == 0 || h is "p" or "reel" ? null : h;
    }

    private static readonly string[] Generic =
        { "crossfit", "cross", "box", "gym", "gimnasio", "training", "club", "fitness", "centro", "studio", "estudio", "the", "de", "el", "la" };

    private static string NormalizeName(string? s)
    {
        var d = (s ?? "").ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var plain = new string(d.Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark).ToArray());
        var words = System.Text.RegularExpressions.Regex.Split(plain, "[^a-z0-9]+").Where(w => w.Length > 0 && !Generic.Contains(w));
        return string.Concat(words);
    }
}
