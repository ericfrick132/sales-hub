namespace SalesHub.Core.Domain.Enums;

public enum OutboxStatus
{
    Scheduled = 0,
    Sending = 1,
    Sent = 2,
    Failed = 3,
    Cancelled = 4,
    Skipped = 5
}
