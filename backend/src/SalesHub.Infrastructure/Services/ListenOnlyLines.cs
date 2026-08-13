using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SalesHub.Infrastructure.Persistence;

namespace SalesHub.Infrastructure.Services;

/// <summary>
/// Qué líneas están marcadas como "solo escuchar". Se consulta en cada envío, así que
/// va cacheado unos segundos: el costo de un envío bloqueado de más (o de menos) por la
/// ventana del cache es despreciable frente a pegarle a la DB en cada mensaje.
///
/// Vive acá y no en el servicio que envía a propósito: el candado tiene que estar en el
/// punto más bajo posible para que ningún camino nuevo (bot, cadencia, respuesta manual,
/// onboarding) lo esquive por olvido.
/// </summary>
public class ListenOnlyLines
{
    private readonly IServiceScopeFactory _scopes;
    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(30);

    private HashSet<string> _names = new(StringComparer.OrdinalIgnoreCase);
    private DateTimeOffset _loadedAt = DateTimeOffset.MinValue;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public ListenOnlyLines(IServiceScopeFactory scopes) => _scopes = scopes;

    public async Task<bool> IsListenOnlyAsync(string? instanceName, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(instanceName)) return false;
        var names = await GetAsync(ct);
        return names.Contains(instanceName!);
    }

    /// <summary>Fuerza la relectura en el próximo chequeo (al cambiar el switch en la UI).</summary>
    public void Invalidate() => _loadedAt = DateTimeOffset.MinValue;

    private async Task<HashSet<string>> GetAsync(CancellationToken ct)
    {
        if (DateTimeOffset.UtcNow - _loadedAt < Ttl) return _names;
        await _lock.WaitAsync(ct);
        try
        {
            if (DateTimeOffset.UtcNow - _loadedAt < Ttl) return _names;
            using var scope = _scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var names = await db.EvolutionInstances.AsNoTracking()
                .Where(i => i.ListenOnly)
                .Select(i => i.InstanceName)
                .ToListAsync(ct);
            _names = new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);
            _loadedAt = DateTimeOffset.UtcNow;
            return _names;
        }
        finally { _lock.Release(); }
    }
}
