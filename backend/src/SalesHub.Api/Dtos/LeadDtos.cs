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
    DateTimeOffset CreatedAt,
    /// <summary>Categoría de búsqueda con la que se capturó el lead (ej. "yoga", "barbería"). Null si fue carga manual.</summary>
    string? SearchCategory,
    /// <summary>Query libre que se tipeó en Maps (puede incluir ciudad). Útil para debug.</summary>
    string? SearchQuery);

public record LeadPreviewStepDto(
    int Index,
    string Text,
    int DelaySeconds,
    /// <summary>"audio" | "image" | "pdf" | "video" | "file" | null si es texto puro.</summary>
    string? MediaKind,
    string? MediaFileName,
    /// <summary>Cuántas variantes hay configuradas en este step (rotación). Null si no hay media.</summary>
    int? MediaVariantsCount);

public record LeadPreviewDto(
    /// <summary>true si el producto tiene cadencia de steps configurada para este lead.</summary>
    bool HasSteps,
    /// <summary>Categoría efectiva usada para resolver la cadencia ("" = default del producto).</summary>
    string Category,
    /// <summary>Mensaje legacy renderizado (MessageTemplate). Sólo se setea si HasSteps=false.</summary>
    string? LegacyMessage,
    List<LeadPreviewStepDto> Steps);

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
    Guid? SellerId,
    bool AutoQueue = false);

public record BulkImportRequest(
    string RawText,
    string ProductKey,
    LeadSource Source = LeadSource.ManualMaps,
    LeadStatus Status = LeadStatus.New,
    bool AssignToCaller = true,
    bool EnrichWithPlacesApi = true,
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
