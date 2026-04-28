using Microsoft.EntityFrameworkCore;
using SalesHub.Core.Abstractions;
using SalesHub.Core.Domain.Entities;
using SalesHub.Core.Domain.Enums;
using SalesHub.Infrastructure.Persistence;

namespace SalesHub.Infrastructure.Services;

/// <summary>
/// Región-aware assignment. Vendedores son remotos, pero el admin puede asignarles
/// territorios de trabajo (regions_assigned = lista de provincias). Si la provincia
/// del lead matchea con algún vendedor, ese vendedor gana. Si no, round-robin entre
/// los vendedores "sin región" (catch-all).
/// </summary>
public class LeadAssigner : ILeadAssigner
{
    private readonly ApplicationDbContext _db;

    public LeadAssigner(ApplicationDbContext db) { _db = db; }

    public Task<Guid?> PickSellerForProductAsync(string productKey, CancellationToken ct = default)
        => PickAsync(productKey, province: null, city: null, ct);

    /// <summary>Resolves the best seller for a lead given its product vertical and the lead's
    /// city/province. RegionsAssigned can hold either provinces or cities (e.g. "Rosario", "CABA",
    /// "Morón") — we match against both so a city assignment wins for a city-level lead.</summary>
    public Task<Guid?> PickForLeadAsync(string productKey, string? province, string? city = null, CancellationToken ct = default)
        => PickAsync(productKey, province, city, ct);

    private async Task<Guid?> PickAsync(string productKey, string? province, string? city, CancellationToken ct)
    {
        var candidates = await _db.Sellers
            .Where(s => s.IsActive && s.Role == SellerRole.Seller)
            .ToListAsync(ct);

        candidates = candidates
            .Where(s => s.VerticalsWhitelist == null
                     || s.VerticalsWhitelist.Count == 0
                     || s.VerticalsWhitelist.Contains(productKey))
            .ToList();
        if (candidates.Count == 0) return null;

        List<Seller> pool;
        var hasLocation = !string.IsNullOrWhiteSpace(province) || !string.IsNullOrWhiteSpace(city);
        if (hasLocation)
        {
            // Match the lead's city/province (case-insensitive) against the seller's RegionsAssigned.
            // City-level matches (Rosario, CABA, Morón…) win over province-level ones implicitly,
            // because if any seller has the exact city tag they'll be in the owners pool.
            bool Matches(Seller s) =>
                s.RegionsAssigned != null && s.RegionsAssigned.Any(r =>
                    (!string.IsNullOrWhiteSpace(city) && r.Equals(city, StringComparison.OrdinalIgnoreCase)) ||
                    (!string.IsNullOrWhiteSpace(province) && r.Equals(province, StringComparison.OrdinalIgnoreCase)));

            var owners = candidates.Where(Matches).ToList();
            if (owners.Count > 0)
            {
                pool = owners;
            }
            else
            {
                // Fallback: vendedores "catch-all" (sin regiones asignadas) se llevan lo no asignado.
                var catchAll = candidates.Where(s => s.RegionsAssigned == null || s.RegionsAssigned.Count == 0).ToList();
                pool = catchAll.Count > 0 ? catchAll : candidates;
            }
        }
        else
        {
            pool = candidates;
        }

        // Round-robin dentro del pool elegido: el que menos leads tomó en 24h.
        var since = DateTimeOffset.UtcNow.AddHours(-24);
        var counts = await _db.Leads
            .Where(l => l.SellerId != null && l.AssignedAt >= since)
            .GroupBy(l => l.SellerId!.Value)
            .Select(g => new { SellerId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.SellerId, x => x.Count, ct);

        return pool
            .OrderBy(s => counts.TryGetValue(s.Id, out var c) ? c : 0)
            .ThenBy(_ => Guid.NewGuid())
            .First().Id;
    }

    public async Task AssignAsync(Lead lead, Guid sellerId, CancellationToken ct = default)
    {
        lead.SellerId = sellerId;
        lead.AssignedAt = DateTimeOffset.UtcNow;
        lead.Status = LeadStatus.Assigned;
        await _db.SaveChangesAsync(ct);
    }
}
