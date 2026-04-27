using SalesHub.Core.Domain.Enums;

namespace SalesHub.Core.Domain.Entities;

public class CityQueue
{
    public Guid Id { get; set; }
    public string Country { get; set; } = "AR";
    public string Province { get; set; } = string.Empty;
    public string City { get; set; } = string.Empty;
    public PopulationBucket PopulationBucket { get; set; } = PopulationBucket.Small;
    public double? Latitude { get; set; }
    public double? Longitude { get; set; }
    public int? Population { get; set; }
    public int? GeonameId { get; set; }
}
