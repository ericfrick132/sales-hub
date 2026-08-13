namespace SalesHub.Core.Abstractions;

/// <summary>
/// Un "turno de respuesta": el lead rompió el silencio (primer mensaje de su ráfaga,
/// o sea el primero después de algo nuestro) y nosotros contestamos —o no—.
/// <see cref="Minutes"/> es null cuando nunca hubo respuesta posterior.
/// </summary>
public record ResponseTurn(
    Guid LeadId,
    DateTimeOffset InAt,
    DateTimeOffset? OutAt,
    double? Minutes,
    string ProductKey,
    Guid? SellerId,
    int Source,
    bool BotMuted);

/// <summary>
/// Chat que AHORA MISMO está esperando respuesta: su último mensaje es del lead.
/// <see cref="WaitingSince"/> es el primer mensaje sin contestar de la ráfaga (no el
/// último), que es el que mide la espera real del que está del otro lado.
/// </summary>
public record WaitingChat(
    Guid LeadId,
    string LeadName,
    string Phone,
    string ProductKey,
    Guid? SellerId,
    int Source,
    DateTimeOffset WaitingSince,
    DateTimeOffset LastInAt,
    int PendingMessages,
    string LastText,
    bool BotMuted,
    int Status,
    DateTimeOffset? SlaAlertedAt)
{
    public double MinutesWaiting => (DateTimeOffset.UtcNow - WaitingSince).TotalMinutes;
}

/// <summary>
/// Tiempos de atención de las conversaciones de WhatsApp, calculados sobre
/// conversation_messages. Es la fuente única del panel de Atención, del SLA que ve
/// cada vendedor y de la alerta de chat colgado.
/// </summary>
public interface IResponseTimeService
{
    /// <summary>Turnos de respuesta desde <paramref name="since"/> (opcionalmente de un solo vendedor).</summary>
    Task<IReadOnlyList<ResponseTurn>> GetTurnsAsync(DateTimeOffset since, Guid? sellerId = null, CancellationToken ct = default);

    /// <summary>
    /// Chats esperando respuesta ahora, del más viejo al más nuevo.
    /// <paramref name="maxAgeHours"/> acota la deuda vieja (0 = sin tope).
    /// </summary>
    Task<IReadOnlyList<WaitingChat>> GetWaitingAsync(Guid? sellerId = null, int maxAgeHours = 0, int limit = 200, CancellationToken ct = default);

    /// <summary>Cuántos chats esperan respuesta, sin traer las filas (para contadores).</summary>
    Task<int> CountWaitingAsync(Guid? sellerId = null, int maxAgeHours = 0, CancellationToken ct = default);
}
