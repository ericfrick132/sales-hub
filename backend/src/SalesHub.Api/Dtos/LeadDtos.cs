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
public record UpdateLeadInfoRequest(string? Name, string? WhatsappPhone);
public record QueueLeadRequest(DateTimeOffset? At);
public record ClaimLeadRequest(Guid LeadId);

public record CreateManualLeadRequest(
    string Name,
    string ProductKey,
    LeadSource Source,
    LeadStatus? Status,
    string? City,
    string? WhatsappPhone,
    string? InstagramHandle,
    string? Website,
    string? Notes,
    Guid? SellerId);

public record BulkImportRequest(
    string RawText,
    string ProductKey,
    LeadSource Source = LeadSource.ManualMaps,
    LeadStatus Status = LeadStatus.New,
    bool AssignToCaller = true,
    string? City = null,
    Guid? SellerId = null);

public record BulkImportItem(
    string Name,
    string? Phone,
    string? Address,
    double? Rating,
    int? TotalReviews,
    string Outcome,            // "inserted" | "duplicate" | "closed" | "error" | "skipped"
    string? Reason = null,
    Guid? LeadId = null);

public record BulkImportResult(
    int Parsed,
    int Inserted,
    int Duplicates,
    int Closed,
    int Errors,
    List<BulkImportItem> Items);
