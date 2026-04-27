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

        var existing = await _db.Cities
            .Where(c => c.Country == country && c.GeonameId != null)
            .ToDictionaryAsync(c => c.GeonameId!.Value, ct);

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
            if (population < minPopulation) { skipped++; continue; }

            var provinceName = admin1.TryGetValue($"{country}.{admin1Code}", out var prov) ? prov : admin1Code;
            var bucket = BucketFor(population);

            if (existing.TryGetValue(geonameId, out var current))
            {
                current.City = name;
                current.Province = provinceName;
                current.Latitude = lat;
                current.Longitude = lng;
                current.Population = population;
                current.PopulationBucket = bucket;
                updated++;
            }
            else
            {
                _db.Cities.Add(new CityQueue
                {
                    Id = Guid.NewGuid(),
                    Country = country,
                    Province = provinceName,
                    City = name,
                    Latitude = lat,
                    Longitude = lng,
                    Population = population,
                    PopulationBucket = bucket,
                    GeonameId = geonameId
                });
                inserted++;
            }

            if ((inserted + updated) % 500 == 0) await _db.SaveChangesAsync(ct);
        }
        await _db.SaveChangesAsync(ct);
        _log.LogInformation("GeoNames import done: {Ins} inserted, {Upd} updated, {Skip} skipped", inserted, updated, skipped);
        return new ImportResult(inserted, updated, skipped);
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
