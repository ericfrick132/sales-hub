namespace SalesHub.Infrastructure.Options;

/// <summary>
/// Config de Higgsfield Cloud API (generación de video AI para TikTok/YT/Reels).
/// API key por header. Key en el .env como Higgsfield__ApiKey.
/// </summary>
public class HiggsfieldOptions
{
    public string ApiKey { get; set; } = string.Empty;
    public string BaseUrl { get; set; } = "https://cloud.higgsfield.ai";
    public int TimeoutSeconds { get; set; } = 180;
}
