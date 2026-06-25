using SalesHub.Core.Domain.Enums;

namespace SalesHub.Core.Domain.Entities;

/// <summary>
/// Objetivo (meta) que un vendedor tiene que cumplir en un período. Lo precarga
/// el superadmin. El progreso real se calcula contra la tabla Leads en runtime
/// (no se persiste el "actual").
///
/// Alcance flexible:
/// - <see cref="SellerId"/> null  => aplica a TODOS los vendedores (default global).
/// - <see cref="SellerId"/> seteado => override puntual para ese vendedor.
/// - <see cref="ProductKey"/> null => agrega todas las apps.
/// - <see cref="ProductKey"/> seteado => mide solo esa app.
/// </summary>
public class SellerGoal
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Vendedor al que aplica. Null = todos (default global).</summary>
    public Guid? SellerId { get; set; }

    /// <summary>App/vertical a la que aplica. Null = todas (agregado).</summary>
    public string? ProductKey { get; set; }

    public SellerGoalMetric Metric { get; set; }

    public SellerGoalPeriod Period { get; set; }

    /// <summary>Cantidad a alcanzar en el período.</summary>
    public int Target { get; set; }

    public bool IsActive { get; set; } = true;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    public Seller? Seller { get; set; }
}
