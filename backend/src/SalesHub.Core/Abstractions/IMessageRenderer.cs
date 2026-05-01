using SalesHub.Core.Domain.Entities;

namespace SalesHub.Core.Abstractions;

public interface IMessageRenderer
{
    string Render(Lead lead, Product product, Seller? seller = null);
    /// <summary>Renderiza el opener (mensaje corto previo). Devuelve null si el producto no tiene opener configurado.</summary>
    string? RenderOpener(Lead lead, Product product, Seller? seller = null);
}
