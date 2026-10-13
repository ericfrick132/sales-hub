namespace SalesHub.Core.Domain.Entities;

public enum VideoCampaignStatus { Draft = 0, Active = 1, Paused = 2, Done = 3 }

public enum VideoCampaignRecipientStatus { Pending = 0, Sent = 1, Failed = 2, Skipped = 3 }

/// <summary>
/// Campaña de remarketing con video (pantalla Marketing → Campaña con video): un mensaje, un video
/// y la lista de leads elegidos a mano. Sale por UNA línea, de a uno, dentro de la franja horaria y
/// con tope diario. Cada lead la recibe una sola vez; si contesta, se le pasa a la persona del
/// pase de la app (Mateo) con copia al maestro, igual que la pre-calificación.
/// </summary>
public class VideoCampaign
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string ProductKey { get; set; } = string.Empty;

    /// <summary>Línea de Evolution por la que sale.</summary>
    public string InstanceName { get; set; } = string.Empty;

    /// <summary>Texto antes del video. "{nombre}" se reemplaza por el nombre de pila (o se saca si no hay). Líneas en blanco = burbujas separadas.</summary>
    public string MessageBefore { get; set; } = string.Empty;
    public Guid MediaAssetId { get; set; }
    public MediaAsset? MediaAsset { get; set; }
    /// <summary>Texto después del video (opcional), ej. el cierre "podemos armar una demo…".</summary>
    public string MessageAfter { get; set; } = string.Empty;

    public int PerDay { get; set; } = 20;
    /// <summary>Franja de envío, hora Argentina: [desde, hasta).</summary>
    public int WindowFromHour { get; set; } = 9;
    public int WindowToHour { get; set; } = 12;
    /// <summary>Días de envío (1 = lunes … 7 = domingo).</summary>
    public List<int> Weekdays { get; set; } = new() { 1, 2, 3, 4 };

    public VideoCampaignStatus Status { get; set; } = VideoCampaignStatus.Draft;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? LastSentAt { get; set; }

    public List<VideoCampaignRecipient> Recipients { get; set; } = new();
}

public class VideoCampaignRecipient
{
    public Guid Id { get; set; }
    public Guid CampaignId { get; set; }
    public VideoCampaign? Campaign { get; set; }
    public Guid LeadId { get; set; }
    public Lead? Lead { get; set; }

    public VideoCampaignRecipientStatus Status { get; set; } = VideoCampaignRecipientStatus.Pending;
    public DateTimeOffset? SentAt { get; set; }
    /// <summary>Primera respuesta del lead después del envío.</summary>
    public DateTimeOffset? RepliedAt { get; set; }
    public string? Error { get; set; }
}
