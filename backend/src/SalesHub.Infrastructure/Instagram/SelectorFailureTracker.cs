using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace SalesHub.Infrastructure.Instagram;

/// <summary>
/// Rastrea fallos de selectores CSS de Instagram y gatilla
/// un re-análisis automático cuando se supera el umbral.
/// </summary>
public class SelectorFailureTracker
{
    private readonly InstagramOptions _opts;
    private readonly ILogger<SelectorFailureTracker> _log;

    // pageKey -> (selectorKey -> consecutiveFailures)
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, int>> _failures = new();

    /// <summary>
    /// Evento que se dispara cuando un selector alcanza el máximo de fallos.
    /// El worker de estructura se suscribe para ejecutar el re-análisis.
    /// </summary>
    public event Func<string, string, Task>? OnMaxFailuresReached;

    public SelectorFailureTracker(
        IOptions<InstagramOptions> opts,
        ILogger<SelectorFailureTracker> log)
    {
        _opts = opts.Value;
        _log = log;
    }

    /// <summary>
    /// Reporta un fallo de selector. Si se supera el umbral, dispara el evento.
    /// </summary>
    /// <param name="pageKey">Ej: "login", "followersDialog", "dmInbox"</param>
    /// <param name="selectorKey">Ej: "usernameInput", "searchInput"</param>
    public void ReportFailure(string pageKey, string selectorKey)
    {
        var pageFailures = _failures.GetOrAdd(pageKey, _ => new ConcurrentDictionary<string, int>());
        var count = pageFailures.AddOrUpdate(selectorKey, 1, (_, c) => c + 1);

        _log.LogWarning("Selector {PageKey}.{SelectorKey} falló {Count} veces consecutivas",
            pageKey, selectorKey, count);

        if (_opts.MaxSelectorFailuresBeforeReanalysis > 0 &&
            count >= _opts.MaxSelectorFailuresBeforeReanalysis)
        {
            _log.LogCritical(
                "Selector {PageKey}.{SelectorKey} alcanzó {Max} fallos. Gatillando re-análisis de estructura.",
                pageKey, selectorKey, _opts.MaxSelectorFailuresBeforeReanalysis);

            // Resetear contadores para esta página
            pageFailures.Clear();

            // Disparar evento de re-análisis (fire-and-forget)
            var handler = OnMaxFailuresReached;
            if (handler is not null)
            {
                Task.Run(async () =>
                {
                    try
                    {
                        await handler.Invoke(pageKey, selectorKey);
                    }
                    catch (Exception ex)
                    {
                        _log.LogError(ex, "Error en handler de re-análisis de estructura");
                    }
                });
            }
        }
    }

    /// <summary>
    /// Reporta un éxito de selector (resetea el contador de fallos).
    /// </summary>
    public void ReportSuccess(string pageKey, string selectorKey)
    {
        if (_failures.TryGetValue(pageKey, out var pageFailures))
        {
            pageFailures.TryRemove(selectorKey, out _);
        }
    }

    /// <summary>
    /// Resetea todos los contadores de fallos.
    /// </summary>
    public void ResetAll()
    {
        _failures.Clear();
        _log.LogInformation("Todos los contadores de fallos de selectores fueron reseteados");
    }

    /// <summary>
    /// Obtiene el estado actual de los contadores (para debugging/monitoreo).
    /// </summary>
    public Dictionary<string, Dictionary<string, int>> GetSnapshot()
    {
        return _failures.ToDictionary(
            kvp => kvp.Key,
            kvp => kvp.Value.ToDictionary(kvp2 => kvp2.Key, kvp2 => kvp2.Value));
    }
}
