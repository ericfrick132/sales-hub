namespace SalesHub.Core.Domain.Entities;

public enum MessageDirection
{
    Outbound = 0,
    Inbound = 1
}

public enum MessageDeliveryStatus
{
    Queued = 0,
    Sent = 1,
    Delivered = 2,
    Read = 3,
    Failed = 4,
    Received = 5
}

public class ConversationMessage
{
    public Guid Id { get; set; }

    public Guid LeadId { get; set; }
    public Lead? Lead { get; set; }

    public Guid? SellerId { get; set; }
    public Seller? Seller { get; set; }

    public MessageDirection Direction { get; set; }
    public MessageDeliveryStatus Status { get; set; } = MessageDeliveryStatus.Queued;

    public string? WhatsappMessageId { get; set; }
    public string Text { get; set; } = string.Empty;
    public string? EvolutionInstance { get; set; }

    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.UtcNow;
    public bool IsRead { get; set; }
    public DateTimeOffset? ReadAt { get; set; }

    public string? RawJson { get; set; }

    /// <summary>Cuántas veces se intentó transcribir este mensaje (si es nota de
    /// voz). Tope para no reintentar infinito un audio que falla.</summary>
    public int TranscriptionAttempts { get; set; }

    /// <summary>
    /// Tipo de mensaje del lead según el diccionario (reply_intents), ej. "precio", "anuncio".
    /// null = sin clasificar todavía; "otro" = ningún patrón coincidió (lo que iría a la IA).
    /// </summary>
    public string? IntentKey { get; set; }

    /// <summary>Si el tipo es "seguro" (un solo tipo, corto, sin pregunta mezclada): solo esos puede contestar el bot solo.</summary>
    public bool? IntentConfident { get; set; }

    /// <summary>
    /// Qué haría (o hizo) el diccionario con este mensaje: "sin_respuesta", "respuesta" o "ia_humano".
    /// Con "responde solo" apagado es una simulación: no se manda nada.
    /// </summary>
    public string? IntentAction { get; set; }

    /// <summary>La respuesta que mandaría el diccionario (simulada), para compararla con la real.</summary>
    public string? IntentSimulatedReply { get; set; }
}
