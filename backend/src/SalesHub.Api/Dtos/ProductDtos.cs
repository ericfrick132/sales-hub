namespace SalesHub.Api.Dtos;

public record MessageStepDto(string Text, int DelaySeconds, Guid? MediaAssetId, List<Guid>? MediaAssetIds);

public record CategoryCadenceDto(string Category, List<MessageStepDto> Steps);

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
    string AiSalesPlaybook);

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
    string? AiSalesPlaybook = null);
