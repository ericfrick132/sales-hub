namespace SalesHub.Core.Abstractions;

/// <summary>
/// Abstracción del publicador social. Hoy lo implementa BufferPublisher (Buffer
/// GraphQL); mañana podríamos swapear una red puntual a su API directa sin tocar
/// el resto del módulo Posteos.
/// </summary>
public interface ISocialPublisher
{
    Task<IReadOnlyList<SocialChannel>> ListChannelsAsync(CancellationToken ct = default);
    Task<PublishResult> CreatePostAsync(PublishRequest req, CancellationToken ct = default);
    Task<bool> DeletePostAsync(string externalPostId, CancellationToken ct = default);
}

public record SocialChannel(string Id, string Name, string Service);

public class PublishRequest
{
    public string ChannelId { get; init; } = string.Empty;
    /// <summary>Servicio del canal ("instagram", "tiktok"…). Define la metadata específica.</summary>
    public string Service { get; init; } = string.Empty;
    public string? Caption { get; init; }
    public string? ImageUrl { get; init; }
    public string? VideoUrl { get; init; }
    public string? ThumbnailUrl { get; init; }
    /// <summary>Solo IG: post | story | reel | carousel.</summary>
    public string? InstagramType { get; init; }
    public bool ShareToFeed { get; init; }
    /// <summary>Si tiene valor, se agenda; si no, se publica/encola ahora.</summary>
    public DateTimeOffset? ScheduledAt { get; init; }
    /// <summary>Drafts primero: deja el post como borrador para aprobación humana.</summary>
    public bool SaveAsDraft { get; init; } = true;
    /// <summary>true = auto-publica (IG Business); false = notificación (push manual).</summary>
    public bool Automatic { get; init; } = true;
}

public class PublishResult
{
    public bool Success { get; init; }
    public string? ExternalPostId { get; init; }
    public string? Status { get; init; }
    public string? Error { get; init; }
    public string? RawJson { get; init; }
}
