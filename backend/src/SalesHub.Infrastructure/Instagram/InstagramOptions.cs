namespace SalesHub.Infrastructure.Instagram;

public class InstagramOptions
{
    public const string SectionName = "Instagram";

    /// <summary>Ruta donde se guardan los perfiles de navegador persistentes.</summary>
    public string BrowserDataDir { get; set; } = "/app/instagram_browser_data";

    /// <summary>Tiempo máximo de espera para navegación (ms).</summary>
    public int NavigationTimeoutMs { get; set; } = 30_000;

    /// <summary>Delay entre acciones para parecer humano (ms).</summary>
    public int MinActionDelayMs { get; set; } = 2_000;
    public int MaxActionDelayMs { get; set; } = 5_000;

    /// <summary>Máximo de DMs por hora por cuenta (pacing anti-ban del worker).</summary>
    public int MaxDmPerHour { get; set; } = 20;

    /// <summary>Máximo de DMs por DÍA por cuenta. Tope duro anti-ban.</summary>
    public int MaxDmPerDay { get; set; } = 40;

    /// <summary>Máximo de perfiles a scrapear por hora por cuenta.</summary>
    public int MaxScrapePerHour { get; set; } = 100;

    /// <summary>Intervalo entre ejecuciones del scraper worker.</summary>
    public int ScraperIntervalMinutes { get; set; } = 60;

    /// <summary>URL del proxy (opcional). Ej: http://user:pass@host:port</summary>
    public string? ProxyUrl { get; set; }

    /// <summary>
    /// Cantidad de fallos consecutivos de un selector antes de gatillar
    /// un re-análisis automático de la estructura. 0 = desactivado.
    /// </summary>
    public int MaxSelectorFailuresBeforeReanalysis { get; set; } = 3;
}
