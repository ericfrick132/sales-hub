namespace SalesHub.Core.Domain.Entities;

/// <summary>
/// Foto diaria (día de Argentina) de los números de negocio de una app, para graficar la
/// tendencia de MRR. Una fila por app y día; la última sync del día la pisa.
/// </summary>
public class AppMetricsDaily
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string ProductKey { get; set; } = string.Empty;
    public DateOnly Date { get; set; }
    public decimal MrrUsd { get; set; }
    public int Active { get; set; }
    public int Trial { get; set; }
    public int PastDue { get; set; }
    public DateTimeOffset CapturedAt { get; set; } = DateTimeOffset.UtcNow;
}
