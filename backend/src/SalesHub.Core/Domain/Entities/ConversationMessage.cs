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
}
