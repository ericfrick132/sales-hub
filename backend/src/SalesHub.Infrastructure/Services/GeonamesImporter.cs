using System.Globalization;
using System.IO.Compression;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SalesHub.Core.Domain.Entities;
using SalesHub.Core.Domain.Enums;
using SalesHub.Infrastructure.Persistence;

namespace SalesHub.Infrastructure.Services;

/// <summary>
/// One-shot importer that pulls the GeoNames country dump (free, public domain) and
/// populates <see cref="CityQueue"/> with every populated place that has a known
/// population — plus lat/lng for map rendering. Idempotent on re-run: upserts by
/// geonameId so the admin can hit "Importar" safely to refresh bucket/population.
///
/// Source: https://download.geonames.org/export/dump/ (e.g. AR.zip, MX.zip, CO.zip).
/// Admin-level codes: https://download.geonames.org/export/dump/admin1CodesASCII.txt
/// </summary>
public class GeonamesImporter
{
    private readonly HttpClient _http;
    private readonly ApplicationDbContext _db;
    private readonly ILogger<GeonamesImporter> _log;
    private const string CountryDumpUrl = "https://download.geonames.org/export/dump/{0}.zip";
    private const string Admin1Url = "https://download.geonames.org/export/dump/admin1CodesASCII.txt";

    public GeonamesImporter(HttpClient http, ApplicationDbContext db, ILogger<GeonamesImporter> log)
    {
        _http = http;
        _db = db;
        _log = log;
        _http.Timeout = TimeSpan.FromMinutes(3);
    }

    public record ImportResult(int Inserted, int Updated, int Skipped);

    public async Task<ImportResult> ImportAsync(string country, int minPopulation = 500, CancellationToken ct = default)
    {
        country = country.ToUpperInvariant();
        _log.LogInformation("GeoNames import starting for {Country} (minPop={Min})", country, minPopulation);

        var admin1 = await FetchAdmin1Map(country, ct);
        _log.LogInformation("Loaded {N} admin1 (province) names for {Country}", admin1.Count, country);

        var dumpUrl = string.Format(CountryDumpUrl, country);
        using var resp = await _http.GetAsync(dumpUrl, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        using var zipStream = await resp.Content.ReadAsStreamAsync(ct);
        using var archive = new ZipArchive(zipStream, ZipArchiveMode.Read);

        var entry = archive.GetEntry($"{country}.txt")
            ?? throw new InvalidOperationException($"{country}.txt not found in dump");
        using var reader = new StreamReader(entry.Open(), Encoding.UTF8);

        var rows = await _db.Cities.Where(c => c.Country == country).ToListAsync(ct);
        var byGeoname = rows.Where(c => c.GeonameId != null).ToDictionary(c => c.GeonameId!.Value);
        // La tabla no admite dos ciudades con el mismo nombre en la misma provincia
        // (ix_cities_queue_country_province_city). El dump sí las trae — varios "San José"
        // en una provincia — y las ciudades cargadas a mano no tienen geonameId: insertarlas
        // de nuevo rompía el import entero en el primer lote y el mapa quedaba vacío.
        var byName = rows.ToDictionary(c => NameKey(c.Province, c.City), StringComparer.Ordinal);
        // Las cargadas a mano se reconocen sin importar tildes ("Cordoba" = "Córdoba").
        var seededByNorm = rows.Where(c => c.GeonameId is null)
            .GroupBy(c => NormKey(c.Province, c.City))
            .ToDictionary(g => g.Key, g => g.First());

        int inserted = 0, updated = 0, skipped = 0;
        string? line;
        while ((line = await reader.ReadLineAsync(ct)) != null)
        {
            ct.ThrowIfCancellationRequested();
            var cols = line.Split('\t');
            if (cols.Length < 15) { skipped++; continue; }

            var featureClass = cols[6];  // P = populated place
            if (featureClass != "P") { skipped++; continue; }
            var featureCode = cols[7];   // PPL, PPLA, PPLC, PPLL, PPLX, PPLF, PPLR, PPLS, PPLG, PPLH, PPLW, PPLQ
            if (featureCode is "PPLH" or "PPLQ" or "PPLW") { skipped++; continue; } // historical/destroyed/abandoned

            if (!int.TryParse(cols[0], out var geonameId)) { skipped++; continue; }
            var name = cols[1];
            if (!double.TryParse(cols[4], NumberStyles.Float, CultureInfo.InvariantCulture, out var lat)) { skipped++; continue; }
            if (!double.TryParse(cols[5], NumberStyles.Float, CultureInfo.InvariantCulture, out var lng)) { skipped++; continue; }
            var admin1Code = cols[10];
            _ = int.TryParse(cols[14], out var population);

            var provinceName = CanonicalProvince(country,
                admin1.TryGetValue($"{country}.{admin1Code}", out var prov) ? prov : admin1Code);
            // La capital: los leads la traen como "CABA", no como "Buenos Aires".
            if (country == "AR" && featureCode == "PPLC") name = "CABA";
            var bucket = BucketFor(population);
            var key = NameKey(provinceName, name);

            if (!byGeoname.ContainsKey(geonameId)
                && seededByNorm.TryGetValue(NormKey(provinceName, name), out var seeded) && seeded.GeonameId is null)
            {
                // Ciudad cargada a mano: se completa con coordenadas aunque GeoNames no tenga su
                // población (pasa con varios partidos). Sin población se respeta el tamaño del seed.
                seeded.Latitude = lat;
                seeded.Longitude = lng;
                seeded.GeonameId = geonameId;
                if (population >= minPopulation) { seeded.Population = population; seeded.PopulationBucket = bucket; }
                byGeoname[geonameId] = seeded;
                updated++;
                if ((inserted + updated) % 500 == 0) await _db.SaveChangesAsync(ct);
                continue;
            }
            if (population < minPopulation) { skipped++; continue; }

            if (byGeoname.TryGetValue(geonameId, out var current))
            {
                // Re-import: si GeoNames le cambió el nombre y el nuevo ya lo usa otra fila, se
                // queda con el nombre viejo y sólo refresca coordenadas y población.
                var oldKey = NameKey(current.Province, current.City);
                if (key != oldKey && !byName.ContainsKey(key))
                {
                    byName.Remove(oldKey);
                    current.City = name;
                    current.Province = provinceName;
                    byName[key] = current;
                }
                Apply(current, lat, lng, population, bucket, geonameId);
                updated++;
            }
            else if (byName.TryGetValue(key, out var sameName))
            {
                if (sameName.GeonameId is null || population > (sameName.Population ?? 0))
                {
                    // Homónima en la misma provincia: gana la más poblada.
                    if (sameName.GeonameId is { } prevId) byGeoname.Remove(prevId);
                    Apply(sameName, lat, lng, population, bucket, geonameId);
                    byGeoname[geonameId] = sameName;
                    updated++;
                }
                else
                {
                    skipped++;
                    continue;
                }
            }
            else
            {
                var city = new CityQueue
                {
                    Id = Guid.NewGuid(),
                    Country = country,
                    Province = provinceName,
                    City = name,
                };
                Apply(city, lat, lng, population, bucket, geonameId);
                _db.Cities.Add(city);
                byGeoname[geonameId] = city;
                byName[key] = city;
                inserted++;
            }

            if ((inserted + updated) % 500 == 0) await _db.SaveChangesAsync(ct);
        }
        await _db.SaveChangesAsync(ct);
        _log.LogInformation("GeoNames import done: {Ins} inserted, {Upd} updated, {Skip} skipped", inserted, updated, skipped);
        return new ImportResult(inserted, updated, skipped);
    }

    private static string NameKey(string province, string city) => $"{province}\u0001{city}";

    private static string NormKey(string province, string city) => $"{Fold(province)}\u0001{Fold(city)}";

    private static string Fold(string text) => SellerZones.Fold(text);

    /// <summary>
    /// Provincias argentinas como las escriben los leads ("Córdoba", "Tucumán"). GeoNames las
    /// trae sin tildes ("Cordoba") y a la capital como "Buenos Aires F.D.": si se guardaran así,
    /// asignar una provincia en el mapa de zonas no matchearía con ningún lead.
    /// </summary>
    private static readonly string[] ArProvinces =
    {
        "Buenos Aires", "Catamarca", "Chaco", "Chubut", "Córdoba", "Corrientes", "Entre Ríos",
        "Formosa", "Jujuy", "La Pampa", "La Rioja", "Mendoza", "Misiones", "Neuquén", "Río Negro",
        "Salta", "San Juan", "San Luis", "Santa Cruz", "Santa Fe", "Santiago del Estero",
        "Tierra del Fuego", "Tucumán",
    };

    private static string CanonicalProvince(string country, string name)
    {
        if (country != "AR") return name;
        if (Fold(name).StartsWith("buenos aires f")) return "Buenos Aires";
        var folded = Fold(name);
        return ArProvinces.FirstOrDefault(p => Fold(p) == folded || folded.StartsWith(Fold(p) + " ")) ?? name;
    }

    private static void Apply(CityQueue c, double lat, double lng, int population, PopulationBucket bucket, int geonameId)
    {
        c.Latitude = lat;
        c.Longitude = lng;
        c.Population = population;
        c.PopulationBucket = bucket;
        c.GeonameId = geonameId;
    }

    private async Task<Dictionary<string, string>> FetchAdmin1Map(string country, CancellationToken ct)
    {
        var map = new Dictionary<string, string>();
        using var resp = await _http.GetAsync(Admin1Url, ct);
        resp.EnsureSuccessStatusCode();
        var text = await resp.Content.ReadAsStringAsync(ct);
        foreach (var line in text.Split('\n'))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var cols = line.Split('\t');
            if (cols.Length < 2) continue;
            var key = cols[0];
            if (!key.StartsWith(country + ".", StringComparison.Ordinal)) continue;
            map[key] = cols[1];
        }
        return map;
    }

    private static PopulationBucket BucketFor(int population) => population switch
    {
        >= 1_000_000 => PopulationBucket.Mega,
        >= 300_000 => PopulationBucket.Big,
        >= 50_000 => PopulationBucket.Medium,
        >= 10_000 => PopulationBucket.Small,
        _ => PopulationBucket.Town
    };
}
