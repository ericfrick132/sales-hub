using Microsoft.EntityFrameworkCore;
using SalesHub.Core.Domain.Entities;
using SalesHub.Core.Domain.Entities.Social;

namespace SalesHub.Infrastructure.Persistence;

public class ApplicationDbContext : DbContext
{
    public ApplicationDbContext(DbContextOptions<ApplicationDbContext> options) : base(options) { }

    public DbSet<Seller> Sellers => Set<Seller>();
    public DbSet<EvolutionInstance> EvolutionInstances => Set<EvolutionInstance>();
    public DbSet<Product> Products => Set<Product>();
    public DbSet<Lead> Leads => Set<Lead>();
    public DbSet<CityQueue> Cities => Set<CityQueue>();
    public DbSet<ScrapeLog> ScrapeLogs => Set<ScrapeLog>();
    public DbSet<Competitor> Competitors => Set<Competitor>();
    public DbSet<CompetitorPost> CompetitorPosts => Set<CompetitorPost>();
    public DbSet<CompetitorComment> CompetitorComments => Set<CompetitorComment>();
    public DbSet<ApifyRun> ApifyRuns => Set<ApifyRun>();
    public DbSet<MessageOutbox> Outbox => Set<MessageOutbox>();
    public DbSet<SellerDailyStats> DailyStats => Set<SellerDailyStats>();
    public DbSet<ConversationMessage> ConversationMessages => Set<ConversationMessage>();
    public DbSet<Locality> Localities => Set<Locality>();
    public DbSet<SellerLocality> SellerLocalities => Set<SellerLocality>();
    public DbSet<SearchJob> SearchJobs => Set<SearchJob>();
    public DbSet<MediaAsset> MediaAssets => Set<MediaAsset>();
    public DbSet<MessageStepRotation> MessageStepRotations => Set<MessageStepRotation>();
    public DbSet<InstagramAccount> InstagramAccounts => Set<InstagramAccount>();
    public DbSet<InstagramMetrics> InstagramMetrics => Set<InstagramMetrics>();
    public DbSet<InstagramScrapeTarget> InstagramScrapeTargets => Set<InstagramScrapeTarget>();
    public DbSet<InstagramStructureSnapshot> InstagramStructureSnapshots => Set<InstagramStructureSnapshot>();
    public DbSet<InstagramFollowCampaign> InstagramFollowCampaigns => Set<InstagramFollowCampaign>();
    public DbSet<InstagramFollowAction> InstagramFollowActions => Set<InstagramFollowAction>();
    public DbSet<SeoSite> SeoSites => Set<SeoSite>();
    public DbSet<SeoKeyword> SeoKeywords => Set<SeoKeyword>();
    public DbSet<SeoArticle> SeoArticles => Set<SeoArticle>();

    // Módulo Posteos
    public DbSet<PostingProfile> PostingProfiles => Set<PostingProfile>();
    public DbSet<PostingChannel> PostingChannels => Set<PostingChannel>();
    public DbSet<SocialPost> SocialPosts => Set<SocialPost>();
    public DbSet<SocialPostAsset> SocialPostAssets => Set<SocialPostAsset>();
    public DbSet<InspirationItem> InspirationItems => Set<InspirationItem>();
    public DbSet<InspirationSettings> InspirationSettings => Set<InspirationSettings>();

    // Flags on/off de runners, toggleables desde la UI (override de Workers:*AutoStart).
    public DbSet<RuntimeFlag> RuntimeFlags => Set<RuntimeFlag>();

    // Relay de transcripción de audios: allowlist de números + config global (on/off + línea).
    public DbSet<TranscriptionPhone> TranscriptionPhones => Set<TranscriptionPhone>();
    public DbSet<TranscriptionSettings> TranscriptionSettings => Set<TranscriptionSettings>();

    // Follow-up de abandono unificado y AGNÓSTICO: secuencias genéricas por (app, trigger) +
    // telemetría que reportan las apps, para reportes centralizados por app.
    public DbSet<FollowupSequence> FollowupSequences => Set<FollowupSequence>();
    public DbSet<FollowupEvent> FollowupEvents => Set<FollowupEvent>();

    // Objetivos de vendedor (metas precargadas por superadmin; progreso calculado en runtime).
    public DbSet<SellerGoal> SellerGoals => Set<SellerGoal>();

    // Uso de la API de Claude (tokens + costo USD por feature) para el widget de gasto.
    public DbSet<AiUsageLog> AiUsageLogs => Set<AiUsageLog>();

    // Reglas duras genéricas de la IA de ventas (CRUD admin; se inyectan al system prompt).
    public DbSet<AiRule> AiRules => Set<AiRule>();

    // Onboarding de ads multi-app: estado por lead + config por producto.
    public DbSet<LeadOnboarding> LeadOnboardings => Set<LeadOnboarding>();
    public DbSet<OnboardingConfig> OnboardingConfigs => Set<OnboardingConfig>();
    public DbSet<OnboardingPersona> OnboardingPersonas => Set<OnboardingPersona>();
    public DbSet<OnboardingAudio> OnboardingAudios => Set<OnboardingAudio>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(ApplicationDbContext).Assembly);
    }
}
