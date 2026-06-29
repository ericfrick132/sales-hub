namespace SalesHub.Infrastructure.Options;

/// <summary>
/// Config de fal.ai (generación de video por API key, headless — corre en el droplet).
/// Auth por header "Authorization: Key &lt;FAL_KEY&gt;". Queue API: submit → poll → response.
/// El modelo se elige por su endpoint id (catálogo de fal.ai): Seedance/Kling/Veo/Wan…
/// </summary>
public class FalOptions
{
    public string ApiKey { get; set; } = string.Empty;
    public string BaseUrl { get; set; } = "https://queue.fal.run";

    /// <summary>
    /// Endpoint id del modelo texto→video. Ej:
    /// fal-ai/bytedance/seedance/v1/pro/text-to-video | fal-ai/kling-video/v2/standard/text-to-video | fal-ai/veo3
    /// </summary>
    public string VideoModel { get; set; } = "fal-ai/bytedance/seedance/v1/pro/text-to-video";

    /// <summary>480p | 720p | 1080p (depende del modelo).</summary>
    public string Resolution { get; set; } = "1080p";
    public int DurationSeconds { get; set; } = 5;

    /// <summary>Polling de la queue (status hasta COMPLETED/ERROR).</summary>
    public int PollSeconds { get; set; } = 5;
    public int MaxPollAttempts { get; set; } = 90; // ~7.5 min

    public int TimeoutSeconds { get; set; } = 180;
    /// <summary>Base pública para servir el mp4 persistido (la consume Buffer / el handoff de Warmr).</summary>
    public string PublicBaseUrl { get; set; } = "https://api.sales.efcloud.tech";
}
