using SalesHub.Core.Domain.Enums;

namespace SalesHub.Core.Domain.Entities;

public class MessageOutbox
{
    public Guid Id { get; set; }

    public Guid LeadId { get; set; }
    public Lead? Lead { get; set; }

    public Guid SellerId { get; set; }
    public Seller? Seller { get; set; }

    public string EvolutionInstance { get; set; } = string.Empty;
    public string WhatsappPhone { get; set; } = string.Empty;

    /// <summary>
    /// Snapshot del mensaje al momento del enqueue. Para filas nuevas (StepIndex != null) esto
    /// queda como preview/debug; el sender re-renderiza desde la config actual del producto.
    /// Para filas legacy (StepIndex == null, encoladas antes del cambio render-at-send) este
    /// campo es la verdad y se manda tal cual.
    /// </summary>
    public string Message { get; set; } = string.Empty;
    /// <summary>
    /// Snapshot del media al momento del enqueue. Mismo deal que Message: para filas nuevas se
    /// re-resuelve al momento del envío (con rotación de variantes), y este valor termina
    /// re-escrito con el asset realmente enviado para que audio-stats siga atribuyendo bien.
    /// </summary>
    public Guid? MediaAssetId { get; set; }
    public MediaAsset? MediaAsset { get; set; }

    /// <summary>
    /// Índice del step dentro de la cadencia (0-based). Null = fila legacy encolada antes de
    /// que existiera render-at-send; el sender la trata como snapshot estático.
    /// </summary>
    public int? StepIndex { get; set; }
    /// <summary>
    /// Categoría de la cadencia override resuelta al encolar (mismo motor que el helper).
    /// "" = cadencia default del producto. Null = fila legacy.
    /// </summary>
    public string? CadenceCategory { get; set; }

    public DateTimeOffset ScheduledAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? SentAt { get; set; }
    public DateTimeOffset? LockedAt { get; set; }

    public OutboxStatus Status { get; set; } = OutboxStatus.Scheduled;
    public int Attempts { get; set; }
    public string? Error { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
