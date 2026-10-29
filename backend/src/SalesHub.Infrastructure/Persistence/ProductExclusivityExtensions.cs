using Microsoft.EntityFrameworkCore;
using SalesHub.Core.Domain.Entities;

namespace SalesHub.Infrastructure.Persistence;

/// <summary>
/// Productos exclusivos (<see cref="Product.Exclusive"/>): solo los trabaja quien los tiene en
/// su whitelist. Un vendedor catch-all (whitelist vacía) NO los cubre — ni en el reparto, ni en
/// el rescate del rebalancer, ni en el pool.
/// </summary>
public static class ProductExclusivityExtensions
{
    public static async Task<HashSet<string>> ExclusiveProductKeysAsync(this ApplicationDbContext db, CancellationToken ct = default)
        => (await db.Products.AsNoTracking().Where(p => p.Exclusive).Select(p => p.ProductKey).ToListAsync(ct))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>¿Este vendedor puede tomar leads de este producto? Whitelist explícita = solo esos;
    /// whitelist vacía = todo menos los exclusivos.</summary>
    public static bool Covers(this Seller s, string productKey, IReadOnlySet<string> exclusive)
        => s.VerticalsWhitelist is { Count: > 0 }
            ? s.VerticalsWhitelist.Contains(productKey)
            : !exclusive.Contains(productKey);
}
