using SalesHub.Core.Domain.Enums;

namespace SalesHub.Core.Domain.Entities;

public class MessageOutbox
{
    public Guid Id { get; set; }

    public Guid LeadId { get; set; }
    public Lead? Lead { get; set; }

    public Guid SellerId { get; set; }
    public Seller? Seller { get; set; }

    public string EvolutionInstance { get; set; } = string.Empty;
    public string WhatsappPhone { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    /// <summary>Si está, el sender manda el archivo via Evolution sendMedia con Message como caption.</summary>
    public Guid? MediaAssetId { get; set; }
    public MediaAsset? MediaAsset { get; set; }

    public DateTimeOffset ScheduledAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? SentAt { get; set; }
    public DateTimeOffset? LockedAt { get; set; }

    public OutboxStatus Status { get; set; } = OutboxStatus.Scheduled;
    public int Attempts { get; set; }
    public string? Error { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
