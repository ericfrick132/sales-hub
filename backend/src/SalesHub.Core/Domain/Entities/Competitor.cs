namespace SalesHub.Core.Domain.Entities;

public class Competitor
{
    public Guid Id { get; set; }
    public string Handle { get; set; } = string.Empty;
    public string Platform { get; set; } = "instagram";
    public string? DisplayName { get; set; }
    public string? Vertical { get; set; }
    public int? FollowersCount { get; set; }
    public int? FollowingCount { get; set; }
    public int? PostsCount { get; set; }
    public bool IsActive { get; set; } = true;
    public string? RawProfileJson { get; set; }
    public DateTimeOffset? LastScrapedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    public ICollection<CompetitorPost> Posts { get; set; } = new List<CompetitorPost>();
}
