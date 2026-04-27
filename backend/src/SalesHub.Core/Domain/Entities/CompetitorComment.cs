namespace SalesHub.Core.Domain.Entities;

public class CompetitorComment
{
    public Guid Id { get; set; }
    public Guid PostId { get; set; }
    public CompetitorPost? Post { get; set; }

    public string? AuthorHandle { get; set; }
    public string? Text { get; set; }
    public DateTimeOffset? PostedAt { get; set; }
    public bool IsNegative { get; set; }
    public string? RawJson { get; set; }
}
