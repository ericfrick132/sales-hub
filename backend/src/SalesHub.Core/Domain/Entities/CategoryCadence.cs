namespace SalesHub.Core.Domain.Entities;

/// <summary>
/// Override de cadencia para una categoría de búsqueda concreta del producto
/// (ej. "yoga" dentro de "GymHero"). Si Steps tiene contenido y el lead
/// matchea Category, se usa este set en vez del MessageSteps default del
/// producto.
/// </summary>
public class CategoryCadence
{
    public string Category { get; set; } = string.Empty;
    public List<MessageStep> Steps { get; set; } = new();
}
