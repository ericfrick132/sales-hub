using SalesHub.Core.Domain.Enums;

namespace SalesHub.Api.Dtos;

public record CityDto(
    Guid Id,
    string Country,
    string Province,
    string City,
    PopulationBucket Bucket,
    DateTimeOffset? LastScrapedForProduct,
    int DaysSinceLastScrape,
    int LeadsFromCityForProduct,
    bool CooldownActive,
    int? LastResultsCount);

public record SuggestedCityDto(
    Guid Id,
    string Country,
    string Province,
    string City,
    PopulationBucket Bucket,
    int Score,
    string Reason);

public record CityGroupedDto(
    string Country,
    IReadOnlyDictionary<string, List<CityDto>> ProvinceToCities);

public record CreateCityRequest(string Country, string Province, string City, PopulationBucket Bucket);
