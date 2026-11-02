namespace SalesHub.Core.Domain.Entities;

/// <summary>
/// Un cobro real que una app le hizo a uno de sus tenants y el período que cubre, tal como
/// lo reporta en GET {app}/api/hub/tenant-metrics (campo "periods" de cada tenant). Con esto
/// se arma el MRR de antes de las fotos diarias (app_metrics_daily). Lo reemplaza entero
/// TenantMetricsWorker en cada sync de la app.
/// </summary>
public class AppPaidPeriod
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string ProductKey { get; set; } = string.Empty;

    /// <summary>Id del tenant en la app (= <see cref="AppTenant.ExternalId"/>).</summary>
    public string ExternalId { get; set; } = string.Empty;

    /// <summary>Desde cuándo cubre el cobro (normalmente la fecha del pago).</summary>
    public DateTimeOffset From { get; set; }

    /// <summary>Hasta cuándo cubre (exclusivo).</summary>
    public DateTimeOffset To { get; set; }

    /// <summary>Lo cobrado llevado a mensual, en la moneda del tenant (<see cref="AppTenant.Currency"/>).</summary>
    public decimal MonthlyAmount { get; set; }
}
