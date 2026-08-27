using Microsoft.EntityFrameworkCore;
using SalesHub.Infrastructure.Persistence;

namespace SalesHub.Infrastructure.Services;

/// <summary>
/// Convierte las calificaciones humanas de conversaciones (👍/👎 + comentario) en un bloque
/// de "aprendizajes" para el system prompt del agente de ventas, por producto. Cacheado
/// unos minutos: el prompt se arma en cada respuesta y el feedback cambia poco.
/// </summary>
public class ConversationFeedbackProvider
{
    private const int MaxItems = 12;
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(3);
    private static readonly Dictionary<string, (DateTimeOffset at, string block)> Cache = new();
    private static readonly object Gate = new();

    private readonly ApplicationDbContext _db;
    public ConversationFeedbackProvider(ApplicationDbContext db) => _db = db;

    public async Task<string> BuildBlockAsync(string productKey, CancellationToken ct)
    {
        lock (Gate)
        {
            if (Cache.TryGetValue(productKey, out var hit) && hit.at + Ttl > DateTimeOffset.UtcNow) return hit.block;
        }
        var items = await _db.ConversationFeedbacks.AsNoTracking()
            .Where(f => f.ProductKey == productKey && f.Note != null && f.Note != "")
            .OrderByDescending(f => f.CreatedAt)
            .Take(MaxItems)
            .Select(f => new { f.Rating, f.Note, f.RatedMessage })
            .ToListAsync(ct);
        var block = string.Empty;
        if (items.Count > 0)
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine();
            sb.AppendLine("APRENDIZAJES DEL EQUIPO (calificaciones humanas de charlas reales de este producto — respetalos):");
            foreach (var f in items)
            {
                var tag = f.Rating > 0 ? "BIEN" : f.Rating < 0 ? "MAL" : "NOTA";
                var ctx = string.IsNullOrWhiteSpace(f.RatedMessage) ? string.Empty
                    : $" (sobre la respuesta: \"{Trunc(f.RatedMessage!, 140)}\")";
                sb.AppendLine($"- [{tag}] {Trunc(f.Note!, 300)}{ctx}");
            }
            block = sb.ToString();
        }
        lock (Gate) Cache[productKey] = (DateTimeOffset.UtcNow, block);
        return block;
    }

    public static void Invalidate(string productKey)
    {
        lock (Gate) Cache.Remove(productKey);
    }

    private static string Trunc(string s, int n) => s.Length <= n ? s : s[..n] + "…";
}
