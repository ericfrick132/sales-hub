namespace SalesHub.Core.Domain.Entities;

/// <summary>Métricas diarias por cuenta de Instagram.</summary>
public class InstagramMetrics
{
    public Guid Id { get; set; }

    public Guid InstagramAccountId { get; set; }
    public InstagramAccount? InstagramAccount { get; set; }

    public DateOnly Date { get; set; }

    // Scraping
    public int ProfilesScraped { get; set; }
    public int LeadsCreated { get; set; }

    // Envío de DMs
    public int DmSent { get; set; }
    public int DmFailed { get; set; }
    public int DmReplies { get; set; }

    // Salud de la cuenta
    public int ActionBlocks { get; set; }
    public int Warnings { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
