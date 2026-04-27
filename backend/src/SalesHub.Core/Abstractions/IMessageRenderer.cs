using SalesHub.Core.Domain.Entities;

namespace SalesHub.Core.Abstractions;

public interface IMessageRenderer
{
    string Render(Lead lead, Product product, Seller? seller = null);
}
