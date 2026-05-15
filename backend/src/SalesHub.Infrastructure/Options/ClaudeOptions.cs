namespace SalesHub.Infrastructure.Options;

/// <summary>
/// Config para la API de Claude (Anthropic) — la usamos para generar la
/// respuesta de venta sugerida cuando un lead responde. El vendedor la
/// revisa/edita/manda; nada se manda solo. La key va en el .env del droplet
/// como Claude__ApiKey.
/// </summary>
public class ClaudeOptions
{
    public string ApiKey { get; set; } = string.Empty;
    public string BaseUrl { get; set; } = "https://api.anthropic.com/v1";
    /// <summary>Haiku 4.5: barato y rápido — alcanza de sobra porque el humano revisa.</summary>
    public string Model { get; set; } = "claude-haiku-4-5-20251001";
    public int MaxTokens { get; set; } = 400;
    public int TimeoutSeconds { get; set; } = 60;
}
