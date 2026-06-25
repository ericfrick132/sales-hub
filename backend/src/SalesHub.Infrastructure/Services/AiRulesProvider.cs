using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SalesHub.Core.Domain.Entities;
using SalesHub.Infrastructure.Persistence;

namespace SalesHub.Infrastructure.Services;

/// <summary>
/// Provee el bloque de "reglas duras" (AiRule) que se inyecta al system prompt de la
/// IA de ventas. Singleton con caché de 30s (no querea la DB en cada sugerencia). El
/// controller llama Invalidate() al editar para que el cambio se vea al toque.
/// </summary>
public class AiRulesProvider
{
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<AiRulesProvider> _log;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private List<AiRule> _cache = new();
    private DateTimeOffset _expires = DateTimeOffset.MinValue;

    public AiRulesProvider(IServiceScopeFactory scopes, ILogger<AiRulesProvider> log)
    {
        _scopes = scopes;
        _log = log;
    }

    /// <summary>Reglas activas aplicables (globales + las del producto si se pasa) formateadas como bloque.</summary>
    public async Task<string> GetBlockAsync(string? productKey, CancellationToken ct = default)
    {
        var rules = await GetActiveAsync(ct);
        var applicable = rules
            .Where(r => r.ProductKey == null
                        || (productKey != null && string.Equals(r.ProductKey, productKey, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(r => r.SortOrder).ThenBy(r => r.CreatedAt)
            .Select(r => r.Text.Trim())
            .Where(t => t.Length > 0)
            .ToList();

        if (applicable.Count == 0) return string.Empty;

        var sb = new StringBuilder();
        sb.AppendLine();
        sb.AppendLine("REGLAS DURAS (obligatorias, tienen prioridad sobre todo lo anterior):");
        foreach (var t in applicable) sb.AppendLine($"- {t}");
        return sb.ToString();
    }

    public void Invalidate() => _expires = DateTimeOffset.MinValue;

    private async Task<List<AiRule>> GetActiveAsync(CancellationToken ct)
    {
        if (DateTimeOffset.UtcNow < _expires) return _cache;
        await _lock.WaitAsync(ct);
        try
        {
            if (DateTimeOffset.UtcNow < _expires) return _cache;
            using var scope = _scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            _cache = await db.Set<AiRule>().AsNoTracking().Where(r => r.IsActive).ToListAsync(ct);
            _expires = DateTimeOffset.UtcNow.AddSeconds(30);
            return _cache;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "No pude cargar AiRules; uso caché previa");
            return _cache;
        }
        finally
        {
            _lock.Release();
        }
    }
}
