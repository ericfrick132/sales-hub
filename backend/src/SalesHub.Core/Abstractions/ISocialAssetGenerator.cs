namespace SalesHub.Core.Abstractions;

/// <summary>
/// Genera el asset visual (imagen o video) a partir de un prompt. Implementaciones
/// futuras: HiggsfieldVideoGenerator (video TikTok/YT), CanvaImageGenerator (imagen IG).
/// El worker resuelve el primero que sepa manejar el assetKind pedido.
/// </summary>
public interface ISocialAssetGenerator
{
    /// <summary>"image" | "video".</summary>
    bool CanHandle(string assetKind);
    Task<AssetResult?> GenerateAsync(string prompt, string assetKind, CancellationToken ct = default);
}

public record AssetResult(string Url, string? ThumbnailUrl);
