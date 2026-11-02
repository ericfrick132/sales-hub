namespace SalesHub.Infrastructure.Options;

public class GoogleOptions
{
    public string PlacesApiKey { get; set; } = string.Empty;
    public string OAuthClientId { get; set; } = string.Empty;
    public string OAuthClientSecret { get; set; } = string.Empty;

    /// <summary>Callback del OAuth de Google Calendar (agenda de demos). Tiene que estar cargado tal cual en el cliente OAuth.</summary>
    public string CalendarRedirectUri { get; set; } = "https://api.sales.efcloud.tech/api/google-calendar/callback";

    // Cap de corridas diarias de Google Places (cada corrida = 1 Text Search + hasta MaxResults Place Details).
    // Espejo del n8n: 3 runs/día × ~21 calls = ~63 calls/día (bien dentro del free tier de $200/mes).
    public int PlacesDailyCap { get; set; } = 3;
}
