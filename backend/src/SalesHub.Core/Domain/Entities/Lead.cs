using SalesHub.Core.Domain.Enums;

namespace SalesHub.Core.Domain.Entities;

public class Lead
{
    public Guid Id { get; set; }

    public string ProductKey { get; set; } = string.Empty;
    public Product? Product { get; set; }

    public LeadSource Source { get; set; }
    public string? ExternalId { get; set; }
    public string? PlaceId { get; set; }

    public string Name { get; set; } = string.Empty;
    public string? Address { get; set; }
    public string? City { get; set; }
    public string? Province { get; set; }
    public string Country { get; set; } = "AR";

    public string? RawPhone { get; set; }
    public string? WhatsappPhone { get; set; }
    public string? WhatsappJid { get; set; }
    public bool WhatsappValidated { get; set; }

    public string? Website { get; set; }
    public string? InstagramHandle { get; set; }
    public string? FacebookUrl { get; set; }

    public double? Rating { get; set; }
    public int? TotalReviews { get; set; }
    public double? Latitude { get; set; }
    public double? Longitude { get; set; }
    public string? BusinessStatus { get; set; }
    public List<string> Types { get; set; } = new();

    public string? SearchQuery { get; set; }
    public string? SearchCategory { get; set; }
    public string? RawDataJson { get; set; }

    public string? LocalityGid2 { get; set; }
    public Locality? Locality { get; set; }

    public int Score { get; set; }

    public Guid? SellerId { get; set; }
    public Seller? Seller { get; set; }
    public DateTimeOffset? AssignedAt { get; set; }

    public LeadStatus Status { get; set; } = LeadStatus.New;

    public string? RenderedMessage { get; set; }
    public string? WhatsappLink { get; set; }

    public DateTimeOffset? QueuedAt { get; set; }
    public DateTimeOffset? SentAt { get; set; }
    public DateTimeOffset? FirstReplyAt { get; set; }
    public DateTimeOffset? ClosedAt { get; set; }

    public string? Notes { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
