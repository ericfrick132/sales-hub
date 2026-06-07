namespace SalesHub.Infrastructure.Options;

/// <summary>
/// Config de Buffer (API GraphQL nueva). El token personal va en el .env del
/// deploy como Buffer__Token. La API toma URLs públicas de media (no upload).
/// </summary>
public class BufferOptions
{
    public string Token { get; set; } = string.Empty;
    public string BaseUrl { get; set; } = "https://api.buffer.com";
    public int TimeoutSeconds { get; set; } = 60;
}
