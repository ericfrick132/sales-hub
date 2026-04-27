using SalesHub.Core.Domain.Enums;

namespace SalesHub.Api.Dtos;

public record LeadDto(
    Guid Id,
    string ProductKey,
    string? ProductName,
    LeadSource Source,
    string Name,
    string? City,
    string? Province,
    string? WhatsappPhone,
    string? Website,
    string? InstagramHandle,
    string? FacebookUrl,
    double? Rating,
    int? TotalReviews,
    int Score,
    LeadStatus Status,
    Guid? SellerId,
    string? SellerName,
    string? RenderedMessage,
    string? WhatsappLink,
    DateTimeOffset? AssignedAt,
    DateTimeOffset? SentAt,
    DateTimeOffset? FirstReplyAt,
    string? Notes,
    DateTimeOffset CreatedAt);

public record UpdateLeadStatusRequest(LeadStatus Status, string? Notes);
public record QueueLeadRequest(DateTimeOffset? At);
public record ClaimLeadRequest(Guid LeadId);
