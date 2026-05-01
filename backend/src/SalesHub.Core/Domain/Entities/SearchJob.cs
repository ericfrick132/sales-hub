using SalesHub.Core.Domain.Enums;

namespace SalesHub.Core.Domain.Entities;

public class SearchJob
{
    public Guid Id { get; set; }

    public Guid SellerId { get; set; }
    public string ProductKey { get; set; } = string.Empty;

    // Optional — the seller can also fire ad-hoc queries that aren't bound to a
    // locality (e.g. "yoga palermo" without a gid2). When set, leads created
    // by the job will inherit it.
    public string? LocalityGid2 { get; set; }
    public string? Category { get; set; }

    // Final query that the worker types into Google Maps. Built from category +
    // locality name + country, but the seller can override it from the UI.
    public string Query { get; set; } = string.Empty;

    public SearchJobStatus Status { get; set; } = SearchJobStatus.Queued;
    public DateTimeOffset ScheduledAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    public int RawItems { get; set; }
    public int LeadsCreated { get; set; }
    public string? Error { get; set; }

    public Seller? Seller { get; set; }
    public Product? Product { get; set; }
    public Locality? Locality { get; set; }
}
