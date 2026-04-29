namespace SalesHub.Core.Domain.Entities;

public class Locality
{
    // GADM admin level 2 ID (e.g. "ARG.6.28_1"). Stable across GADM releases enough
    // for our purposes and matches feature.id in the served PMTiles.
    public string Gid2 { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    // GADM level 1 (province / state).
    public string AdminLevel1Gid { get; set; } = string.Empty;
    public string AdminLevel1Name { get; set; } = string.Empty;

    public string CountryCode { get; set; } = string.Empty;
    public string CountryName { get; set; } = string.Empty;

    public double CentroidLat { get; set; }
    public double CentroidLng { get; set; }

    public ICollection<SellerLocality> SellerAssignments { get; set; } = new List<SellerLocality>();
}
