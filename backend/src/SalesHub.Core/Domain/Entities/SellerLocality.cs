namespace SalesHub.Core.Domain.Entities;

public class SellerLocality
{
    public Guid SellerId { get; set; }
    public string LocalityGid2 { get; set; } = string.Empty;
    public DateTimeOffset AssignedAt { get; set; } = DateTimeOffset.UtcNow;

    public Seller? Seller { get; set; }
    public Locality? Locality { get; set; }
}
