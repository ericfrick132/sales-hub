using SalesHub.Core.Domain.Entities.Social;

namespace SalesHub.Core.Abstractions;

/// <summary>
/// Genera el asset visual (imagen o video) de un posteo. Hoy: AiImageGenerator
/// (IA texto→imagen, GPT Image/Grok). El worker resuelve el primero que sepa
/// manejar el assetKind pedido.
/// </summary>
public interface ISocialAssetGenerator
{
    /// <summary>¿Está configurado el proveedor (API key presente)?</summary>
    bool IsConfigured { get; }
    /// <summary>"image" | "video".</summary>
    bool CanHandle(string assetKind);
    Task<AssetResult?> GenerateAsync(string prompt, string assetKind, CancellationToken ct = default);
    /// <summary>Genera con prompt rico de marca a partir del perfil + el posteo, y liga el asset al posteo.</summary>
    Task<AssetResult?> GenerateForPostAsync(PostingProfile profile, SocialPost post, CancellationToken ct = default);
}

public record AssetResult(string Url, string? ThumbnailUrl);
