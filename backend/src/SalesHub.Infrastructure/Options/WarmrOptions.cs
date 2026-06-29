namespace SalesHub.Infrastructure.Options;

/// <summary>
/// Config de la distribución por Warmr. Warmr no expone API → el modo soportado es
/// "handoff" (cola para subida manual a Cloud Drop). El modo "playwright" (auto-upload
/// a app.warmr.so, NO soportado por Warmr, frágil) queda previsto para el futuro y
/// reusaría el patrón de automatización IG + self-healing de selectores.
/// </summary>
public class WarmrOptions
{
    public string Mode { get; set; } = "handoff";
    public string AppUrl { get; set; } = "https://app.warmr.so";
    /// <summary>Credenciales de Cloud Drop (solo para el futuro modo playwright).</summary>
    public string Email { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
}
