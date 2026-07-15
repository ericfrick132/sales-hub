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

    /// <summary>
    /// URL del proxy primario (opcional). Ej: http://user:pass@host:port o socks5://host:port.
    /// Admite una LISTA separada por coma/;/salto de línea: se prueban en orden hasta que una
    /// llegue a IG (failover). Ej: "socks5://172.17.0.1:1080, http://user:pass@iproxy:port".
    /// </summary>
    public string? ProxyUrl { get; set; }

    /// <summary>
    /// Proxies de RESPALDO globales, en orden de preferencia. Se anexan DESPUÉS del proxy
    /// primario (el de la cuenta o el global) para que, si el primario no llega a IG, la
    /// sesión salte solo al siguiente. Ej: túnel de la compu primero, iProxy del celu de backup.
    /// </summary>
    public List<string> FallbackProxies { get; set; } = new();

    /// <summary>
    /// Bloquea la descarga de recursos pesados (imágenes, video/audio, fuentes)
    /// en el navegador. Como cada byte sale por el 4G del proxy y se paga, esto
    /// baja el consumo de una sesión de IG ~90% (de decenas de MB a &lt;3 MB) sin
    /// afectar la automatización, que opera sobre el DOM/JS. Default: activado.
    /// </summary>
    public bool BlockHeavyResources { get; set; } = true;

    /// <summary>
    /// Cantidad de fallos consecutivos de un selector antes de gatillar
    /// un re-análisis automático de la estructura. 0 = desactivado.
    /// </summary>
    public int MaxSelectorFailuresBeforeReanalysis { get; set; } = 3;
}
