using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;
using SalesHub.Core.Domain.Entities;
using SalesHub.Infrastructure.Persistence;

namespace SalesHub.Infrastructure.Services;

/// <summary>
/// Las localidades de un vendedor. Hay dos maneras de asignarle zonas y las dos cuentan:
/// <list type="bullet">
/// <item>/map pinta localidades (<see cref="SellerLocality"/>, por gid2).</item>
/// <item>/sellers/zones guarda nombres de provincias o ciudades (<c>Seller.RegionsAssigned</c>).</item>
/// </list>
/// Antes "Capturar de Maps" sólo miraba la primera: un vendedor con zonas cargadas en
/// /sellers/zones veía "¡Estás al día!" y además no podía guardar lo que capturaba.
/// </summary>
public static class SellerZones
{
    /// <summary>
    /// Cómo se llama cada provincia en el catálogo de localidades cuando el nombre guardado
    /// en /sellers/zones es otro (el catálogo de ciudades de GeoNames dice "Buenos Aires F.D.",
    /// y el de localidades tiene "La Roja" por La Rioja).
    /// </summary>
    private static readonly Dictionary<string, string> ProvinceAliases = new()
    {
        ["buenos aires f.d."] = "ciudad autonoma de buenos aires",
        ["caba"] = "ciudad autonoma de buenos aires",
        ["capital federal"] = "ciudad autonoma de buenos aires",
        ["la rioja"] = "la roja",
    };

    public static async Task<List<Locality>> LocalitiesForAsync(ApplicationDbContext db, Guid sellerId, CancellationToken ct)
    {
        var painted = await db.SellerLocalities.AsNoTracking()
            .Where(sl => sl.SellerId == sellerId)
            .Select(sl => sl.Locality!)
            .ToListAsync(ct);

        var regions = await db.Sellers.AsNoTracking().Where(s => s.Id == sellerId)
            .Select(s => s.RegionsAssigned).FirstOrDefaultAsync(ct);
        if (regions is not { Count: > 0 }) return painted;

        var wanted = regions.Select(Fold).Where(r => r.Length > 0)
            .Select(r => ProvinceAliases.GetValueOrDefault(r, r))
            .ToHashSet();

        // Sólo los países donde se vende: "Córdoba" o "Buenos Aires" también existen afuera.
        var countries = await db.Products.AsNoTracking().Where(p => p.Active)
            .Select(p => p.Country).Distinct().ToListAsync(ct);
        var candidates = await db.Localities.AsNoTracking()
            .Where(l => countries.Contains(l.CountryCode))
            .ToListAsync(ct);

        var byProvince = candidates.Where(l => wanted.Contains(Fold(l.AdminLevel1Name))).ToList();

        // Una ciudad con el mismo nombre en varias provincias ("San Fernando" está en Buenos
        // Aires y en Chaco): se queda con la de la provincia donde ya tiene otras zonas.
        var byCity = candidates.Where(l => wanted.Contains(Fold(l.Name)))
            .GroupBy(l => Fold(l.Name)).ToList();
        var knownProvinces = byProvince.Concat(byCity.Where(g => g.Count() == 1).Select(g => g.First()))
            .Concat(painted)
            .Select(l => l.AdminLevel1Gid).ToHashSet();
        var cities = byCity.SelectMany(g =>
        {
            if (g.Count() == 1) return g;
            var inKnown = g.Where(l => knownProvinces.Contains(l.AdminLevel1Gid)).ToList();
            return inKnown.Count > 0 ? inKnown : g;
        });

        return painted.Concat(byProvince).Concat(cities)
            .GroupBy(l => l.Gid2)
            .Select(g => g.First())
            .ToList();
    }

    public static async Task<bool> OwnsAsync(ApplicationDbContext db, Guid sellerId, string gid2, CancellationToken ct) =>
        (await LocalitiesForAsync(db, sellerId, ct)).Any(l => l.Gid2 == gid2);

    /// <summary>Minúsculas y sin tildes, para comparar nombres escritos distinto.</summary>
    public static string Fold(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var ch in text.Trim().ToLowerInvariant().Normalize(NormalizationForm.FormD))
            if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark) sb.Append(ch);
        return sb.ToString().Normalize(NormalizationForm.FormC);
    }
}
