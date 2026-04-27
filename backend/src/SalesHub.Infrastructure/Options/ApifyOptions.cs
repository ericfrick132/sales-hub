namespace SalesHub.Infrastructure.Options;

public class ApifyOptions
{
    public string Token { get; set; } = string.Empty;
    public string BaseUrl { get; set; } = "https://api.apify.com/v2";

    // Lead sources
    public ActorSettings GoogleMaps { get; set; } = new() { ActorId = "compass/crawler-google-places" };
    public ActorSettings MetaAdsLibrary { get; set; } = new() { ActorId = "curious_coder/facebook-ads-library-scraper" };
    public ActorSettings Instagram { get; set; } = new() { ActorId = "apify/instagram-scraper" };
    public ActorSettings FacebookPosts { get; set; } = new() { ActorId = "apify/facebook-posts-scraper" };

    // Enrichment (on-demand)
    public ActorSettings InstagramProfile { get; set; } = new() { ActorId = "apify/instagram-profile-scraper" };
    public ActorSettings GoogleSearch { get; set; } = new() { ActorId = "apify/google-search-scraper" };
    public ActorSettings WebsiteCrawler { get; set; } = new() { ActorId = "apify/website-content-crawler" };

    // Trends
    public ActorSettings TikTok { get; set; } = new() { ActorId = "clockworks/tiktok-scraper" };
    public ActorSettings TikTokComments { get; set; } = new() { ActorId = "futurizerush/tiktok-comment-scraper", Enabled = false };

    public int RunTimeoutSeconds { get; set; } = 600;
    public int DefaultMaxResults { get; set; } = 100;

    // Cap diario de runs Apify a través de TODOS los actors. Free plan = $5/mes de plataforma.
    // Actor compass/crawler-google-places = $2.10 / 1k lugares → ~2,380 lugares/mes.
    // Default conservador: 2 runs/día (con MaxResults 30 = ~60 lugares/día = 1,800/mes, dentro del free tier).
    public int DailyRunCap { get; set; } = 2;

    public class ActorSettings
    {
        public string ActorId { get; set; } = string.Empty;
        public bool Enabled { get; set; } = true;
        public int MaxResults { get; set; } = 100;
    }
}
