using SalesHub.Core.Domain.Enums;

namespace SalesHub.Core.Domain.Entities;

public class ScrapeLog
{
    public long Id { get; set; }
    public string ProductKey { get; set; } = string.Empty;
    public string Country { get; set; } = "AR";
    public string? City { get; set; }
    public string? Category { get; set; }
    public LeadSource Source { get; set; }
    public int ResultsCount { get; set; }
    public string Status { get; set; } = "done";
    public string? Error { get; set; }
    public DateTimeOffset RunAt { get; set; } = DateTimeOffset.UtcNow;
}
