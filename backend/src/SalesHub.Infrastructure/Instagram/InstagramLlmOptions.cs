namespace SalesHub.Infrastructure.Instagram;

/// <summary>
/// Configuración para el LLM usado en el análisis de estructura de Instagram.
/// </summary>
public class InstagramLlmOptions
{
    public const string SectionName = "InstagramLlm";

    /// <summary>API Key del LLM (OpenAI o DeepSeek).</summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>Endpoint de la API. Ej: https://api.openai.com/v1/chat/completions</summary>
    public string Endpoint { get; set; } = "https://api.deepseek.com/v1/chat/completions";

    /// <summary>Modelo a usar. Ej: gpt-4o-mini, deepseek-chat</summary>
    public string Model { get; set; } = "deepseek-chat";

    /// <summary>Intervalo en horas entre análisis de estructura (default: 24).</summary>
    public int AnalysisIntervalHours { get; set; } = 24;
}
