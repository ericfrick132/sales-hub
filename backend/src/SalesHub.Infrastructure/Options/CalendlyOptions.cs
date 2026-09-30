namespace SalesHub.Infrastructure.Options;

/// <summary>
/// Calendly (agenda de demos). El token personal va en el .env del droplet como Calendly__Token,
/// nunca en el repo. Reservar por API (POST /invitees) exige plan pago (Standard o superior).
/// </summary>
public class CalendlyOptions
{
    public string Token { get; set; } = string.Empty;
    public string BaseUrl { get; set; } = "https://api.calendly.com";
    public int TimeoutSeconds { get; set; } = 20;
}
