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

    /// <summary>
    /// Proxy GLOBAL de salida para las instancias de WhatsApp (fallback si la instancia no
    /// tiene el suyo). Formato: <c>scheme://user:pass@host:port</c> o <c>host:port:user:pass</c>
    /// o <c>host:port</c>. Vacío = sin proxy (sale por la IP del server, comportamiento actual).
    /// Lo ideal es 1 proxy residencial/móvil AR por número (EvolutionInstance.ProxyUrl); este
    /// global sirve para arrancar con una sola IP para todas las líneas.
    /// </summary>
    public string ProxyUrl { get; set; } = string.Empty;
}
