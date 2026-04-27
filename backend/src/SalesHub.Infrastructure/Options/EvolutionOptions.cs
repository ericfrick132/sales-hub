namespace SalesHub.Infrastructure.Options;

public class EvolutionOptions
{
    public string BaseUrl { get; set; } = "http://64.227.3.140:8080";
    public string ApiKey { get; set; } = string.Empty;
    public int TimeoutSeconds { get; set; } = 15;
}
