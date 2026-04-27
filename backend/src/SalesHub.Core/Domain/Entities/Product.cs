namespace SalesHub.Core.Domain.Entities;

public class Product
{
    public Guid Id { get; set; }

    public string ProductKey { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public bool Active { get; set; } = true;

    public string Country { get; set; } = "AR";
    public string CountryName { get; set; } = "Argentina";
    public string RegionCode { get; set; } = "ar";
    public string Language { get; set; } = "es";
    public string PhonePrefix { get; set; } = "54";

    public List<string> Categories { get; set; } = new();
    public string MessageTemplate { get; set; } = string.Empty;

    public string CheckoutUrl { get; set; } = string.Empty;
    public string PriceDisplay { get; set; } = string.Empty;

    public int DailyLimit { get; set; } = 60;
    public List<int> TriggerHours { get; set; } = new();

    public bool RequiresAssistedSale { get; set; } = false;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    public ICollection<Lead> Leads { get; set; } = new List<Lead>();
}
