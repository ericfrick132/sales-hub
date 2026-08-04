using System.Collections.Concurrent;

namespace SalesHub.Infrastructure.Services;

/// <summary>
/// Envíos directos por device: cola FIFO en memoria (sin outbox — no hay lead detrás)
/// que /api/bridge/pending sirve ANTES que la cola real, salteando caps, gap, seller y
/// dup-guard. La usan el botón "Enviar YA" de /devices y las respuestas de transcripción
/// de audios. El ack de la app (/delivered | /failed) cierra el ciclo acá.
/// Si la API se reinicia en el medio, lo pendiente se pierde: se vuelve a pedir y ya.
/// </summary>
public class BridgeDirectSendService
{
    public const string KindTest = "test";
    public const string KindTranscription = "transcription";
    public const string KindChatReply = "chat";

    public record TestSend(Guid TestId, Guid DeviceId, string Phone, string Text, string Kind)
    {
        public string State { get; set; } = "queued";   // queued | sending | sent | failed
        public string? Error { get; set; }
        public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
        public DateTimeOffset? FinishedAt { get; set; }
        /// <summary>Tipear sin el ritmo humano: son mensajes al propio dueño, no outreach.</summary>
        public bool Fast => Kind != KindTest;
    }

    // FIFO por device: varios audios seguidos generan varias respuestas y ninguna se pisa.
    private readonly ConcurrentDictionary<Guid, ConcurrentQueue<TestSend>> _queues = new();
    private readonly ConcurrentDictionary<Guid, TestSend> _byTestId = new();
    // Último "Enviar YA" por device, para el panel de estado de /devices.
    private readonly ConcurrentDictionary<Guid, TestSend> _lastTestByDevice = new();

    public TestSend Queue(Guid deviceId, string phone, string text, string kind = KindTest)
    {
        var t = new TestSend(Guid.NewGuid(), deviceId, phone, text, kind);
        _queues.GetOrAdd(deviceId, _ => new ConcurrentQueue<TestSend>()).Enqueue(t);
        _byTestId[t.TestId] = t;
        if (kind == KindTest) _lastTestByDevice[deviceId] = t;
        return t;
    }

    /// <summary>El próximo envío directo pendiente del device, marcándolo como sending.</summary>
    public TestSend? TryTakePending(Guid deviceId)
    {
        if (!_queues.TryGetValue(deviceId, out var q)) return null;
        while (q.TryDequeue(out var t))
        {
            if (t.State != "queued") continue;   // ya lo tomó otro tick
            t.State = "sending";
            return t;
        }
        return null;
    }

    /// <summary>Ack de la app. True si el id era de un envío directo (y no del outbox).</summary>
    public bool TryComplete(Guid testId, bool ok, string? error)
    {
        if (!_byTestId.TryGetValue(testId, out var t)) return false;
        t.State = ok ? "sent" : "failed";
        t.Error = ok ? null : error;
        t.FinishedAt = DateTimeOffset.UtcNow;
        return true;
    }

    public TestSend? Status(Guid deviceId) => _lastTestByDevice.GetValueOrDefault(deviceId);

    // Pedido de barrido de chats: el celu recorre WhatsApp y reporta lo que los leads
    // respondieron antes de que existiera la lectura de notificaciones.
    private readonly ConcurrentDictionary<Guid, byte> _sweeps = new();

    public void RequestSweep(Guid deviceId) => _sweeps[deviceId] = 1;

    /// <summary>True una sola vez por pedido: el celu ya se lo lleva en esta respuesta.</summary>
    public bool ConsumeSweep(Guid deviceId) => _sweeps.TryRemove(deviceId, out _);
}
