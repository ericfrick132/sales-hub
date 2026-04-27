using SalesHub.Core.Domain.Enums;

namespace SalesHub.Core.Domain.Entities;

public class ApifyRun
{
    public Guid Id { get; set; }
    public LeadSource Source { get; set; }
    public string ActorId { get; set; } = string.Empty;
    public string? ProductKey { get; set; }
    public string? InputJson { get; set; }
    public string? ApifyRunId { get; set; }
    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? FinishedAt { get; set; }
    public string Status { get; set; } = "running";
    public int ItemsCount { get; set; }
    public int LeadsCreated { get; set; }
    public string? Error { get; set; }
}
