using Microsoft.EntityFrameworkCore;

namespace SalesHub.Infrastructure.Persistence;

public static class RuntimeFlagExtensions
{
    /// <summary>
    /// Devuelve si un runner está prendido: si hay fila en RuntimeFlags manda esa (la UI),
    /// si no, cae al fallback (el valor de config / env var).
    /// </summary>
    public static async Task<bool> IsFlagOnAsync(this ApplicationDbContext db, string key, bool fallback, CancellationToken ct = default)
    {
        var flag = await db.RuntimeFlags.AsNoTracking().FirstOrDefaultAsync(f => f.Key == key, ct);
        return flag?.Enabled ?? fallback;
    }
}
