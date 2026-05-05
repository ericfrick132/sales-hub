namespace SalesHub.Core.Domain.Entities;

/// <summary>
/// Archivo binario (imagen, PDF) atado a un producto. Se referencia por id
/// desde MessageStep y se manda como adjunto vía Evolution. Lo guardamos en
/// la DB (bytea) para no agregar dependencia de object storage — los volúmenes
/// son chicos (pocas apps × pocos adjuntos).
/// </summary>
public class MediaAsset
{
    public Guid Id { get; set; }
    public string ProductKey { get; set; } = string.Empty;
    public Product? Product { get; set; }

    public string FileName { get; set; } = string.Empty;
    public string MimeType { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    public byte[] Content { get; set; } = Array.Empty<byte>();

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
