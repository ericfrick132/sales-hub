using SalesHub.Core.Domain.Entities;

namespace SalesHub.Core.Abstractions;

public interface ILeadAssigner
{
    Task<Guid?> PickSellerForProductAsync(string productKey, CancellationToken ct = default);
    Task<Guid?> PickForLeadAsync(string productKey, string? province, CancellationToken ct = default);
    Task AssignAsync(Lead lead, Guid sellerId, CancellationToken ct = default);
}
