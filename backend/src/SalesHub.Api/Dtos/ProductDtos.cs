namespace SalesHub.Api.Dtos;

public record MessageStepDto(string Text, int DelaySeconds, Guid? MediaAssetId, List<Guid>? MediaAssetIds);

public record CategoryCadenceDto(string Category, List<MessageStepDto> Steps);

/// <summary>Override de cadencia por origen del lead. Source = nombre del enum LeadSource (ej. "MetaLeadAd").</summary>
public record SourceCadenceDto(string Source, List<MessageStepDto> Steps);

public record ProductDto(
    Guid Id,
    string ProductKey,
    string DisplayName,
    bool Active,
    string Country,
    string CountryName,
    string RegionCode,
    string Language,
    string PhonePrefix,
    List<string> Categories,
    string MessageTemplate,
    string OpenerTemplate,
    string CheckoutUrl,
    string PriceDisplay,
    int DailyLimit,
    List<int> TriggerHours,
    int SendHourStart,
    int SendHourEnd,
    bool RequiresAssistedSale,
    int GooglePlacesDailyLeadCap,
    List<string> ReplyTemplates,
    List<MessageStepDto> MessageSteps,
    List<CategoryCadenceDto> CategoryCadences,
    List<SourceCadenceDto> SourceCadences,
    string AiSalesPlaybook,
    bool AutoPilot,
    bool AutoReengage);

public record CreateOrUpdateProductRequest(
    string ProductKey,
    string DisplayName,
    bool Active,
    string Country,
    string CountryName,
    string RegionCode,
    string Language,
    string PhonePrefix,
    List<string> Categories,
    string MessageTemplate,
    string OpenerTemplate,
    string CheckoutUrl,
    string PriceDisplay,
    int DailyLimit,
    List<int> TriggerHours,
    int SendHourStart,
    int SendHourEnd,
    bool RequiresAssistedSale,
    int GooglePlacesDailyLeadCap,
    List<string>? ReplyTemplates,
    List<MessageStepDto>? MessageSteps,
    List<CategoryCadenceDto>? CategoryCadences,
    string? AiSalesPlaybook = null,
    bool AutoPilot = false,
    bool AutoReengage = false,
    List<SourceCadenceDto>? SourceCadences = null);
