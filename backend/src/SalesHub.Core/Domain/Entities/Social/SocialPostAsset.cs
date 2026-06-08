namespace SalesHub.Core.Domain.Entities.Social;

/// <summary>
/// Imagen (o futuro video) generada por IA para un posteo. Vive en la DB (bytea)
/// y se sirve por un endpoint público (/api/posteos/assets/{id}.png) que Buffer
/// descarga al subir el draft. No se hostea en GitHub: un asset de red social no
/// da SEO (eso es del blog), solo necesita una URL pública estable.
/// </summary>
public class SocialPostAsset
{
    public Guid Id { get; set; }

    /// <summary>Posteo que originó el asset (nullable: el path genérico no lo liga).</summary>
    public Guid? SocialPostId { get; set; }

    public string MimeType { get; set; } = "image/png";
    public byte[] Content { get; set; } = Array.Empty<byte>();
    public long SizeBytes { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
