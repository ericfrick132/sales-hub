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
    // Mensaje "opener" opcional. Si está, se manda primero (ej. "buenas") y el
    // mensaje principal sale después con el delay normal del seller. Vacío = un solo mensaje.
    public string OpenerTemplate { get; set; } = string.Empty;

    public string CheckoutUrl { get; set; } = string.Empty;
    public string PriceDisplay { get; set; } = string.Empty;

    public int DailyLimit { get; set; } = 60;
    public List<int> TriggerHours { get; set; } = new();
    // Ventana de horario en la que se permite enviar para este producto (0-24,
    // hora local del seller). Default 0/24 = sin restricción a nivel producto;
    // queda solo la del seller. Si Start>=End el sistema asume sin restricción.
    public int SendHourStart { get; set; } = 0;
    public int SendHourEnd { get; set; } = 24;

    // Cap of NEW leads per day from the free Google Places pipeline. 0 = no per-product cap
    // (only the global Google:PlacesDailyCap of runs/day applies).
    public int GooglePlacesDailyLeadCap { get; set; } = 60;

    public bool RequiresAssistedSale { get; set; } = false;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    public ICollection<Lead> Leads { get; set; } = new List<Lead>();
}
