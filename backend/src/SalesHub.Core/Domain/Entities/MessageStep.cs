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

    /// <summary>
    /// Saludo de voz PERSONALIZADO (audio 1 del cold-open). Si está seteado, ANTES de mandar
    /// el contenido del step se genera una nota de voz con la voz clonada (ElevenLabs)
    /// renderizando este texto con los placeholders del lead — ej. "hola {name}, ¿cómo andás?"
    /// — y se manda primero. El pitch grabado va en MediaAssetIds y sale justo después.
    /// Null/vacío = sin saludo (comportamiento de siempre).
    /// </summary>
    public string? VoiceGreeting { get; set; }
}
