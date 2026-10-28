namespace SalesHub.Core.Domain.Entities;

/// <summary>
/// Imagen que Eric mandó por WhatsApp para una <see cref="ClaudeTask"/> (mientras esperaba el "sí", o con
/// caption como pedido). La Mac la baja con <c>GET api/claude-tasks/{id}/attachments/{index}</c> y se la pasa
/// a Claude Code como archivo.
/// </summary>
public class ClaudeTaskAttachment
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TaskId { get; set; }
    /// <summary>Orden de llegada, desde 1.</summary>
    public int Index { get; set; }
    public string MimeType { get; set; } = "image/jpeg";
    public byte[] Data { get; set; } = Array.Empty<byte>();
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
