namespace SalesHub.Core.Domain.Entities;

/// <summary>
/// Tanda de negocios para la extensión "Públicos IG", armada a mano desde Marketing → Competencia →
/// Públicos: toma N pendientes de una app (y opcionalmente de un listado). La extensión solo trabaja
/// negocios de batches abiertos. Si aparece un error se cierra o se reintentan los que no salieron.
/// </summary>
public class FinderBatch
{
    public Guid Id { get; set; }
    /// <summary>Número correlativo para mostrar (#1, #2…).</summary>
    public int Number { get; set; }
    public string ProductKey { get; set; } = "";
    public string? Dataset { get; set; }
    public int Size { get; set; }
    /// <summary>open / closed.</summary>
    public string Status { get; set; } = "open";
    public string? Note { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    /// <summary>Último "reintentar los que no salieron": las estadísticas cuentan desde acá.</summary>
    public DateTimeOffset? RetriedAt { get; set; }
    public DateTimeOffset? ClosedAt { get; set; }
}
