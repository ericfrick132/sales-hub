using SalesHub.Core.Domain.Enums;

namespace SalesHub.Core.Domain.Entities;

public class EvolutionInstance
{
    public Guid Id { get; set; }

    public Guid SellerId { get; set; }
    public Seller? Seller { get; set; }

    public string InstanceName { get; set; } = string.Empty;
    public string? ConnectedPhoneNumber { get; set; }

    public InstanceStatus Status { get; set; } = InstanceStatus.Disconnected;
    public DateTimeOffset? LastStatusCheckAt { get; set; }
    public DateTimeOffset? ConnectedAt { get; set; }
    public DateTimeOffset? DisconnectedAt { get; set; }

    public string? LastQrCodeBase64 { get; set; }
    public DateTimeOffset? QrCodeGeneratedAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
