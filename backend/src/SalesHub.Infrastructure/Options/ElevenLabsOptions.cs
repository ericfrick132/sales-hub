namespace SalesHub.Infrastructure.Options;

/// <summary>
/// Config de ElevenLabs (TTS) para la narración de los videos. La voz la controla
/// <see cref="VoiceId"/> (default = "George", multilingüe). Para acento argentino
/// nativo hace falta tier Creator (voz "Adrian"); en free tier las voces default
/// hablan español con acento algo neutro.
/// </summary>
public class ElevenLabsOptions
{
    public string ApiKey { get; set; } = string.Empty;
    public string BaseUrl { get; set; } = "https://api.elevenlabs.io";
    /// <summary>Voice id de ElevenLabs. Default: George (JBFqnCBsd6RMkjVDRZzb).</summary>
    public string VoiceId { get; set; } = "JBFqnCBsd6RMkjVDRZzb";
    /// <summary>Modelo multilingüe (habla español). eleven_multilingual_v2 es el estable.</summary>
    public string ModelId { get; set; } = "eleven_multilingual_v2";
    public int TimeoutSeconds { get; set; } = 60;
}
