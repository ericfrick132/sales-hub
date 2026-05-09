namespace SalesHub.Core.Domain.Entities;

/// <summary>
/// Un paso del drip de outreach inicial. Soporta los mismos placeholders y
/// spin-text que MessageTemplate. DelaySeconds es relativo al paso anterior
/// (el primer step se manda al asignar; los siguientes esperan ese delta
/// además de la humanización del seller).
/// </summary>
public class MessageStep
{
    public string Text { get; set; } = string.Empty;
    public int DelaySeconds { get; set; }
    /// <summary>Legacy: un solo archivo. Si MediaAssetIds está poblado, este campo se ignora.</summary>
    public Guid? MediaAssetId { get; set; }
    /// <summary>
    /// N variantes para A/B/C testing. Sólo se usa cuando son audios — el
    /// OutboxEnqueueHelper rota round-robin entre ellas y persiste cuál se
    /// envió en el MessageOutbox para poder calcular tasa de respuesta /
    /// éxito por archivo. Si tiene 1 elemento, equivale al MediaAssetId
    /// legacy. Si está vacío, el step es texto puro (o usa MediaAssetId).
    /// </summary>
    public List<Guid> MediaAssetIds { get; set; } = new();
}
