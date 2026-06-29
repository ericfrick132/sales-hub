namespace SalesHub.Core.Domain.Entities;

public class CompetitorPost
{
    public Guid Id { get; set; }
    public Guid CompetitorId { get; set; }
    public Competitor? Competitor { get; set; }

    public string ExternalPostId { get; set; } = string.Empty;
    public string? PostUrl { get; set; }
    public string? Caption { get; set; }
    public DateTimeOffset? PostedAt { get; set; }
    public int Likes { get; set; }
    public int CommentsCount { get; set; }
    public List<string> Hashtags { get; set; } = new();
    public string? RawJson { get; set; }
    public DateTimeOffset ScrapedAt { get; set; } = DateTimeOffset.UtcNow;

    // ── Media (para mostrar/curar en el board de inspiración) ──────────────
    /// <summary>URL del asset (imagen displayUrl o video).</summary>
    public string? MediaUrl { get; set; }
    public string? ThumbnailUrl { get; set; }
    public bool IsVideo { get; set; }

    // ── Curación (el "me gusta esto, replicalo") ──────────────────────────
    /// <summary>Marcado por el usuario como inspiración para replicar.</summary>
    public bool Curated { get; set; }
    public DateTimeOffset? CuratedAt { get; set; }

    public ICollection<CompetitorComment> Comments { get; set; } = new List<CompetitorComment>();
}
