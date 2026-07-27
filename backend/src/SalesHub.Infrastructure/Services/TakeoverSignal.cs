using System.Collections.Concurrent;

namespace SalesHub.Infrastructure.Services;

/// <summary>
/// Señal en memoria entre el webhook y el agente de conversación: los leads que el humano
/// reactivó con "+" saltan la cola en el próximo tick (sin settle, sin sorteo del batch,
/// sin espera humanizada del onboarding). Best-effort: si el proceso se reinicia la señal
/// se pierde, pero el lead igual queda elegible por el flujo normal (el "+" limpió la
/// sugerencia/marcador).
/// </summary>
public class TakeoverSignal
{
    private readonly ConcurrentDictionary<Guid, byte> _pending = new();

    public void Enqueue(Guid leadId) => _pending.TryAdd(leadId, 0);

    /// <summary>Devuelve y vacía los pendientes.</summary>
    public IReadOnlyList<Guid> Drain()
    {
        if (_pending.IsEmpty) return Array.Empty<Guid>();
        var ids = _pending.Keys.ToList();
        foreach (var id in ids) _pending.TryRemove(id, out _);
        return ids;
    }
}
