using SalesHub.Core.Domain.Entities.Social;

namespace SalesHub.Core.Abstractions;

/// <summary>
/// Distribuye un posteo por Warmr (posteo nativo en device real, sin handicap de API).
/// Warmr NO tiene API → el default es <see cref="WarmrDispatchMode.Handoff"/>: el posteo
/// queda en la cola ReadyForWarmr y el humano hace el último drag a Cloud Drop.
/// La abstracción deja la puerta abierta a un futuro distribuidor por Playwright
/// (auto-upload a app.warmr.so) sin tocar el resto del pipeline.
/// </summary>
public interface IWarmrDistributor
{
    bool IsConfigured { get; }

    /// <summary>Modo efectivo (informativo para la UI): "handoff" | "playwright".</summary>
    string Mode { get; }

    /// <summary>
    /// Encola/despacha el posteo a Warmr. En handoff deja <c>post.Status = ReadyForWarmr</c>
    /// (el caller persiste). Un futuro modo auto subiría el clip y dejaría WarmrUploaded.
    /// </summary>
    Task<WarmrDispatchResult> DispatchAsync(SocialPost post, CancellationToken ct = default);
}

public static class WarmrDispatchMode
{
    public const string Handoff = "handoff";
    public const string Playwright = "playwright";
}

public record WarmrDispatchResult(bool Success, string? Error = null);
