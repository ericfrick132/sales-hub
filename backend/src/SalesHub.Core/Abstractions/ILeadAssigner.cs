using SalesHub.Core.Domain.Entities;

namespace SalesHub.Core.Abstractions;

public interface ILeadAssigner
{
    Task<Guid?> PickSellerForProductAsync(string productKey, CancellationToken ct = default);
    Task<Guid?> PickForLeadAsync(string productKey, string? province, string? city = null, CancellationToken ct = default);
    Task<Guid?> PickForLeadAsync(string productKey, string? localityGid2, string? province, string? city, CancellationToken ct = default);
    /// <summary>Dueño del producto por whitelist, CONECTADO O NO (mismas reglas que el botón
    /// "Reasignar todo"): dedicados primero, catch-all (whitelist vacía) como fallback,
    /// prefiriendo a los que pueden enviar ya. null si no hay ningún candidato activo.</summary>
    Task<Guid?> PickOwnerAsync(string productKey, CancellationToken ct = default);
    Task AssignAsync(Lead lead, Guid sellerId, CancellationToken ct = default);
}
