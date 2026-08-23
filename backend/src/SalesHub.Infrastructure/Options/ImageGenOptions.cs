namespace SalesHub.Infrastructure.Options;

/// <summary>
/// Config del generador de imagen IA para los posteos. Proveedor swappable por env:
/// "openai" (GPT Image — default, mejor render de texto) o "grok" (xAI, OpenAI-compatible).
/// Claves en el .env como ImageGen__ApiKey, ImageGen__Provider, etc.
/// </summary>
public class ImageGenOptions
{
    /// <summary>"openai" | "grok" | "gemini".</summary>
    public string Provider { get; set; } = "openai";
    public string ApiKey { get; set; } = string.Empty;
    /// <summary>ID exacto del modelo. OpenAI: "gpt-image-1.5". Grok: "grok-imagine-image".</summary>
    public string Model { get; set; } = "gpt-image-1.5";
    public string Size { get; set; } = "1024x1024";
    /// <summary>OpenAI gpt-image: "low" | "medium" | "high" | "auto".</summary>
    public string Quality { get; set; } = "high";
    public int TimeoutSeconds { get; set; } = 180;

    /// <summary>
    /// Si el proveedor principal no devuelve imagen (sin saldo, rate-limit, caída),
    /// reintentar con Pollinations (gratis, sin API key, flux). APAGADO por default
    /// (pedido 2026-08-22): si OpenAI se queda sin crédito, el posteo queda en Error
    /// y NO se publica nada con otro modelo — la calidad de Pollinations no da para
    /// salir al aire. Se había prendido el 11-ago-2026 cuando credit_balance_exhausted
    /// dejó 16 posteos sin imagen. Opt-in explícito: ImageGen__FallbackToPollinations=true.
    /// </summary>
    public bool FallbackToPollinations { get; set; } = false;

    /// <summary>
    /// Base pública de la API (donde vive /api/posteos/assets/{id}.png) para armar
    /// la URL absoluta del asset — el worker no tiene HTTP context. El front está en
    /// sales.efcloud.tech (GitHub Pages) pero la API está en api.sales.efcloud.tech.
    /// </summary>
    public string PublicBaseUrl { get; set; } = "https://api.sales.efcloud.tech";

    /// <summary>Base de la API del proveedor (override opcional).</summary>
    public string BaseUrl { get; set; } = string.Empty;

    /// <summary>Resuelve la base según el proveedor si no se setea BaseUrl explícito.</summary>
    public string ResolveBaseUrl() =>
        !string.IsNullOrWhiteSpace(BaseUrl) ? BaseUrl
        : Provider.Trim().ToLowerInvariant() switch
        {
            "grok" => "https://api.x.ai",
            "gemini" => "https://generativelanguage.googleapis.com",
            _ => "https://api.openai.com",
        };
}
