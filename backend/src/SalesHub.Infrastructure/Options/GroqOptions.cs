namespace SalesHub.Infrastructure.Options;

/// <summary>
/// Config para la API de Groq — la usamos para transcribir las notas de voz
/// que mandan los leads (Whisper large v3 turbo, ~$0.0004 por audio de 40s).
/// La key real va en el .env del droplet como Groq__ApiKey.
/// </summary>
public class GroqOptions
{
    public string ApiKey { get; set; } = string.Empty;
    public string BaseUrl { get; set; } = "https://api.groq.com/openai/v1";
    public string TranscriptionModel { get; set; } = "whisper-large-v3-turbo";
    /// <summary>Idioma esperado de los audios (ISO-639-1). "es" mejora la precisión.</summary>
    public string Language { get; set; } = "es";
    public int TimeoutSeconds { get; set; } = 60;
}
