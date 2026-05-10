namespace SalesHub.Infrastructure.Options;

public class EvolutionOptions
{
    public string BaseUrl { get; set; } = "http://64.227.3.140:8080";
    public string ApiKey { get; set; } = string.Empty;
    public int TimeoutSeconds { get; set; } = 15;
    /// <summary>URL pública/interna a la que Evolution debe POSTear los eventos
    /// (messages.upsert, etc). Si está vacío, no configuramos webhook al
    /// crear instancias y los inbound nunca llegan.</summary>
    public string WebhookUrl { get; set; } = string.Empty;
}
