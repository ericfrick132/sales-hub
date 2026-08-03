using System.Collections.Concurrent;

namespace SalesHub.Api.WebSockets;

/// <summary>
/// "Enviar YA" de prueba por device: cola en memoria (sin outbox — un test no tiene
/// lead) que /api/bridge/pending sirve ANTES que la cola real, salteando caps, gap,
/// seller y dup-guard. El ack de la app ( /delivered | /failed ) cierra el ciclo acá.
/// Si la API se reinicia en el medio, el test se pierde: se aprieta de nuevo y ya.
/// </summary>
public class BridgeTestSendService
{
    public record TestSend(Guid TestId, Guid DeviceId, string Phone, string Text)
    {
        public string State { get; set; } = "queued";   // queued | sending | sent | failed
        public string? Error { get; set; }
        public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
        public DateTimeOffset? FinishedAt { get; set; }
    }

    // Un test vigente por device (el último pisa al anterior si aún no fue tomado).
    private readonly ConcurrentDictionary<Guid, TestSend> _byDevice = new();
    private readonly ConcurrentDictionary<Guid, TestSend> _byTestId = new();

    public TestSend Queue(Guid deviceId, string phone, string text)
    {
        var t = new TestSend(Guid.NewGuid(), deviceId, phone, text);
        _byDevice[deviceId] = t;
        _byTestId[t.TestId] = t;
        return t;
    }

    /// <summary>El próximo test pendiente del device, marcándolo como sending.</summary>
    public TestSend? TryTakePending(Guid deviceId)
    {
        if (!_byDevice.TryGetValue(deviceId, out var t) || t.State != "queued") return null;
        t.State = "sending";
        return t;
    }

    /// <summary>Ack de la app para un testId. True si el id era de un test.</summary>
    public bool TryComplete(Guid testId, bool ok, string? error)
    {
        if (!_byTestId.TryGetValue(testId, out var t)) return false;
        t.State = ok ? "sent" : "failed";
        t.Error = ok ? null : error;
        t.FinishedAt = DateTimeOffset.UtcNow;
        return true;
    }

    public TestSend? Status(Guid deviceId) => _byDevice.GetValueOrDefault(deviceId);
}
