using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SalesHub.Core.Abstractions;
using SalesHub.Infrastructure.Apify;
using SalesHub.Infrastructure.Evolution;
using SalesHub.Infrastructure.Options;
using SalesHub.Infrastructure.Persistence;
using SalesHub.Infrastructure.Services;

namespace SalesHub.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddSalesHubInfrastructure(this IServiceCollection services, IConfiguration config)
    {
        services.Configure<ApifyOptions>(config.GetSection("Apify"));
        services.Configure<EvolutionOptions>(config.GetSection("Evolution"));
        services.Configure<GoogleOptions>(config.GetSection("Google"));
        services.Configure<JwtOptions>(config.GetSection("Jwt"));

        services.AddDbContext<ApplicationDbContext>(o =>
            o.UseNpgsql(config.GetConnectionString("Default")
                ?? Environment.GetEnvironmentVariable("SALESHUB_DB_CONNECTION")
                ?? throw new InvalidOperationException("Missing ConnectionStrings:Default"))
             .UseSnakeCaseNamingConvention());

        services.AddHttpClient<ApifyHttpClient>();
        services.AddHttpClient<ApifyUsageMonitor>();
        services.AddHttpClient<EvolutionClient>();
        services.AddHttpClient<GooglePlacesSource>();
        services.AddHttpClient<GeonamesImporter>();
        services.AddHttpClient<WebsiteContactExtractor>();
        services.AddScoped<IWebsiteContactExtractor>(sp => sp.GetRequiredService<WebsiteContactExtractor>());

        services.AddScoped<IEvolutionClient>(sp => sp.GetRequiredService<EvolutionClient>());

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
        services.AddScoped<InstagramCompetitorScraper>();
        services.AddScoped<ApifyTikTokSource>();

        services.AddScoped<IPhoneNormalizer, PhoneNormalizer>();
        services.AddScoped<IMessageRenderer, MessageRenderer>();
        services.AddScoped<ILeadAssigner, LeadAssigner>();
        services.AddScoped<ISendScheduler, SendScheduler>();

        services.AddScoped<PipelineService>();
        services.AddScoped<OutboxSender>();
        services.AddScoped<InstanceMonitor>();
        services.AddScoped<ConversationService>();

        return services;
    }
}
