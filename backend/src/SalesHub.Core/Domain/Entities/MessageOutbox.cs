using SalesHub.Core.Domain.Enums;

namespace SalesHub.Core.Domain.Entities;

public class MessageOutbox
{
    /// <summary>
    /// Marcador en Error mientras el bridge Android tiene la fila pulleada sin ack: si el
    /// celu muere después de enviar pero antes de confirmar, el reclaim de filas Sending
    /// viejas NO debe re-programarla (pudo haber salido → reintentar duplica). Se limpia
    /// en /delivered; /failed lo pisa con el error real.
    /// </summary>
    public const string BridgePulledError = "bridge:pulled";

    public Guid Id { get; set; }

    public Guid LeadId { get; set; }
    public Lead? Lead { get; set; }

    public Guid SellerId { get; set; }
    public Seller? Seller { get; set; }

    /// <summary>
    /// Canal de envío. WhatsApp (default) lo manda el OutboxSender vía Evolution;
    /// Instagram lo despacha el InstagramDmSender (DM al InstagramHandle del lead).
    /// </summary>
    public MessageChannel Channel { get; set; } = MessageChannel.WhatsApp;

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

    /// <summary>
    /// Prioridad de envío (más alto = sale antes, DENTRO de los caps/humanización del sender).
    /// La cadencia normal queda en el default 50. El re-enganche lo setea = score del análisis
    /// del lead (0-100): los calientes salen primero, los fríos después de los leads frescos.
    /// </summary>
    public int Priority { get; set; } = 50;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
