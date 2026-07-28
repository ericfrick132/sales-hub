using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using SalesHub.Core.Abstractions;
using SalesHub.Infrastructure.Apify;
using SalesHub.Infrastructure.Evolution;
using SalesHub.Infrastructure.Instagram;
using SalesHub.Infrastructure.Options;
using SalesHub.Infrastructure.Persistence;
using SalesHub.Infrastructure.Services;
using SalesHub.Infrastructure.Services.Social;

namespace SalesHub.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddSalesHubInfrastructure(this IServiceCollection services, IConfiguration config)
    {
        services.Configure<ApifyOptions>(config.GetSection("Apify"));
        services.Configure<EvolutionOptions>(config.GetSection("Evolution"));
        services.Configure<GoogleOptions>(config.GetSection("Google"));
        services.Configure<JwtOptions>(config.GetSection("Jwt"));
        services.Configure<GroqOptions>(config.GetSection("Groq"));
        services.Configure<ClaudeOptions>(config.GetSection("Claude"));
        services.Configure<InstagramOptions>(config.GetSection("Instagram"));
        // Varios servicios IG (InstagramClient, InstagramLeadScraper, InstagramAccountsController…)
        // inyectan InstagramOptions DIRECTO (no IOptions<>). Sin esta línea no resuelven en runtime
        // → era lo que bloqueaba las ventas automáticas por Instagram.
        services.AddSingleton(sp => sp.GetRequiredService<IOptions<InstagramOptions>>().Value);

        // Módulo Posteos
        services.Configure<BufferOptions>(config.GetSection("Buffer"));
        services.Configure<FalOptions>(config.GetSection("Fal"));
        services.Configure<ImageGenOptions>(config.GetSection("ImageGen"));
        services.Configure<ElevenLabsOptions>(config.GetSection("ElevenLabs"));
        services.Configure<WarmrOptions>(config.GetSection("Warmr"));
        services.Configure<VoiceNoteOptions>(config.GetSection("VoiceNote"));

        // Módulo SEO/GEO
        services.Configure<SeoOptions>(config.GetSection("Seo"));
        services.Configure<GitHubOptions>(config.GetSection("GitHub"));

        services.AddDbContext<ApplicationDbContext>(o =>
            o.UseNpgsql(config.GetConnectionString("Default")
                ?? Environment.GetEnvironmentVariable("SALESHUB_DB_CONNECTION")
                ?? throw new InvalidOperationException("Missing ConnectionStrings:Default"))
             .UseSnakeCaseNamingConvention());

        services.AddHttpClient<ApifyHttpClient>();
        services.AddHttpClient<ApifyUsageMonitor>();
        services.AddHttpClient<EvolutionClient>();
        services.AddHttpClient<GroqWhisperClient>();
        services.AddHttpClient<ClaudeClient>();
        services.AddHttpClient<BufferClient>();
        services.AddScoped<ISocialPublisher>(sp => sp.GetRequiredService<BufferClient>());
        services.AddScoped<SocialContentGenerator>();
        services.AddScoped<Services.Social.LandingKnowledgeService>();
        services.AddScoped<Services.Social.BrandLogoService>();
        services.AddHttpClient<AiImageGenerator>();
        services.AddScoped<ISocialAssetGenerator>(sp => sp.GetRequiredService<AiImageGenerator>());
        // Narración de video (TTS ElevenLabs). El FalVideoGenerator la muxea al mp3.
        services.AddHttpClient<Services.Social.ElevenLabsClient>();
        // 2° generador de asset: video por fal.ai (API key, headless). Se resuelve por CanHandle("video").
        services.AddHttpClient<FalVideoGenerator>();
        services.AddScoped<ISocialAssetGenerator>(sp => sp.GetRequiredService<FalVideoGenerator>());
        // Distribución por Warmr (handoff: deja el posteo en la cola para subida manual a Cloud Drop).
        services.AddScoped<IWarmrDistributor, WarmrHandoffDistributor>();
        services.AddHttpClient<GoogleAutocompleteClient>();
        services.AddHttpClient<GitHubContentClient>();
        services.AddHttpClient<GooglePlacesSource>();
        services.AddHttpClient<GooglePlacesEnricher>();
        services.AddScoped<IGooglePlacesEnricher>(sp => sp.GetRequiredService<GooglePlacesEnricher>());
        services.AddHttpClient<GeonamesImporter>();
        services.AddHttpClient<WebsiteContactExtractor>();
        services.AddScoped<IWebsiteContactExtractor>(sp => sp.GetRequiredService<WebsiteContactExtractor>());

        services.AddScoped<IEvolutionClient>(sp => sp.GetRequiredService<EvolutionClient>());

        // Aviso operativo al número maestro por WhatsApp (ej. Claude sin crédito).
        services.AddScoped<IAdminAlerter, AdminAlerter>();

        // Cliente del endpoint de estado de cada producto (pull-guard del follow-up).
        services.AddHttpClient<ProductStateClient>();
        services.AddScoped<IProductStateClient>(sp => sp.GetRequiredService<ProductStateClient>());

        // Notifica al producto el cambio de estado de venta (status-back).
        services.AddHttpClient<ProductStatusNotifier>();
        services.AddScoped<IProductStatusNotifier>(sp => sp.GetRequiredService<ProductStatusNotifier>());

        // Lead sources registered via IApifySource
        services.AddScoped<IApifySource, ApifyGoogleMapsSource>();
        services.AddScoped<IApifySource, ApifyMetaAdsLibrarySource>();
        services.AddScoped<IApifySource, ApifyInstagramSource>();
        services.AddScoped<IApifySource, ApifyFacebookPostsSource>();
        services.AddScoped<IApifySource>(sp => sp.GetRequiredService<GooglePlacesSource>());

        // Enrichers and on-demand services
        services.AddScoped<InstagramProfileEnricher>();
        services.AddScoped<WebsiteCrawlerEnricher>();
        services.AddScoped<GoogleSearchService>();
        // Scraper de competidores para inspiración: browser logueado (reemplazó a Apify).
        services.AddScoped<Instagram.InstagramCompetitorBrowserScraper>();
        services.AddScoped<ApifyTikTokSource>();

        services.AddScoped<IPhoneNormalizer, PhoneNormalizer>();
        services.AddScoped<IMessageRenderer, MessageRenderer>();
        services.AddScoped<ILeadAssigner, LeadAssigner>();
        services.AddScoped<ILeadIngestService, LeadIngestService>();
        services.AddScoped<ISendScheduler, SendScheduler>();

        services.AddScoped<PipelineService>();
        services.AddScoped<OutboxSender>();
        services.AddScoped<InstanceMonitor>();
        services.AddScoped<LeadRebalancer>();
        services.AddScoped<ConversationService>();
        services.AddSingleton<TakeoverSignal>(); // señal "+": salto de cola webhook→agente
        // Relay de transcripción de audios (notas de voz → texto, sólo números de la allowlist).
        services.AddScoped<AudioTranscriptionRelay>();
        // Intake de inspiraciones por WhatsApp (imágenes/ideas del número maestro → Posteos).
        services.AddScoped<Services.Social.InspirationIntakeRelay>();
        // Acumulador del modo batch: junta imágenes/texto/audio y responde un PDF (debounce).
        // Singleton: vive entre webhooks (cada uno es un request) y al cerrar el batch abre su scope.
        services.AddSingleton<TranscriptionBatchAccumulator>();
        // Reglas duras de la IA (cache 30s, leídas vía scope) — inyectadas al system prompt.
        services.AddSingleton<AiRulesProvider>();
        // Tono de conversación editable (global + override por producto) — base de estilo de
        // TODO lo que compone la IA (venta, nudges, soporte, asides). Cache 30s.
        services.AddSingleton<ToneProvider>();
        services.AddScoped<AiSuggestionService>();

        // Onboarding de ads multi-app (config por producto en onboarding_configs).
        services.AddHttpClient<OnboardingProvisionClient>();
        services.AddScoped<IOnboardingProvisionClient>(sp => sp.GetRequiredService<OnboardingProvisionClient>());
        services.AddScoped<OnboardingService>();
        // Email: con Email:RelayUrl configurado sale por el relay HTTPS (el droplet tiene
        // SMTP saliente bloqueado por DO); si no, SMTP directo (entornos donde sí se puede).
        services.AddHttpClient<RelayEmailSender>();
        services.AddSingleton<SmtpEmailSender>();
        services.AddTransient<IEmailSender>(sp =>
        {
            var cfg = sp.GetRequiredService<Microsoft.Extensions.Configuration.IConfiguration>();
            return string.IsNullOrWhiteSpace(cfg["Email:RelayUrl"])
                ? sp.GetRequiredService<SmtpEmailSender>()
                : sp.GetRequiredService<RelayEmailSender>();
        });
        services.AddScoped<ConversationAgentService>();
        services.AddScoped<VoiceNoteService>();
        services.AddScoped<VoiceCalibrationRelay>();
        services.AddScoped<ColdOpenAudioRelay>();
        services.AddScoped<SeoContentService>();
        services.AddScoped<BlogPublisher>();

        // Instagram services
        services.AddSingleton<InstagramEncryptionService>();
        services.AddScoped<InstagramLeadScraper>();
        services.AddScoped<InstagramFollowService>();
        services.AddScoped<InstagramDmSender>();
        services.AddScoped<InstagramInboxPoller>();
        services.AddScoped<InstagramStructureAnalyzer>();
        services.AddSingleton<SelectorFailureTracker>();
        services.Configure<InstagramLlmOptions>(config.GetSection("InstagramLlm"));

        return services;
    }
}
