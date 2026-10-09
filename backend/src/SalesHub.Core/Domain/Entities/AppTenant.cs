namespace SalesHub.Core.Domain.Entities;

/// <summary>
/// Último estado conocido de un tenant (cliente) de una de nuestras apps, tal como lo
/// reporta la app en GET {app}/api/hub/tenant-metrics. Lo upsertea TenantMetricsWorker.
/// De acá salen MRR, pagos, trials, altas, bajas y churn del Dashboard (tab Negocio).
/// </summary>
public class AppTenant
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>App (gymhero, turnospro, …).</summary>
    public string ProductKey { get; set; } = string.Empty;

    /// <summary>Id del tenant en la app.</summary>
    public string ExternalId { get; set; } = string.Empty;

    public string? Name { get; set; }

    /// <summary>"trial" | "trial_expired" | "active" | "past_due" | "cancelled".</summary>
    public string Status { get; set; } = string.Empty;

    public string? Plan { get; set; }

    /// <summary>Lo que paga por mes (normalizado si paga trimestral/anual), en <see cref="Currency"/>.</summary>
    public decimal MonthlyAmount { get; set; }

    /// <summary>"ARS" | "USD".</summary>
    public string Currency { get; set; } = "ARS";

    public DateTimeOffset? CreatedAt { get; set; }
    public DateTimeOffset? FirstPaidAt { get; set; }
    public DateTimeOffset? CancelledAt { get; set; }

    /// <summary>Última vez que la app lo reportó.</summary>
    public DateTimeOffset LastSeenAt { get; set; } = DateTimeOffset.UtcNow;
}
