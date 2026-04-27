namespace SalesHub.Api.Dtos;

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
    string CheckoutUrl,
    string PriceDisplay,
    int DailyLimit,
    List<int> TriggerHours,
    bool RequiresAssistedSale);

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
    string CheckoutUrl,
    string PriceDisplay,
    int DailyLimit,
    List<int> TriggerHours,
    bool RequiresAssistedSale);
