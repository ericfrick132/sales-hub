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

    public ICollection<CompetitorComment> Comments { get; set; } = new List<CompetitorComment>();
}
