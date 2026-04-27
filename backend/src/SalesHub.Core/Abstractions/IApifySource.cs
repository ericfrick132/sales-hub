using SalesHub.Core.Domain.Entities;
using SalesHub.Core.Domain.Enums;

namespace SalesHub.Core.Abstractions;

public record SourceRunRequest(
    Product Product,
    string? City,
    string? Province,
    string? Category,
    int MaxResults);

public record SourceRunResult(
    LeadSource Source,
    IReadOnlyList<Lead> Leads,
    string? ApifyRunId,
    int RawItems);

public interface IApifySource
{
    LeadSource Source { get; }
    Task<SourceRunResult> RunAsync(SourceRunRequest request, CancellationToken ct = default);
}
