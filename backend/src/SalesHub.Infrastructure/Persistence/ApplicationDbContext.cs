using Microsoft.EntityFrameworkCore;
using SalesHub.Core.Domain.Entities;

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

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(ApplicationDbContext).Assembly);
    }
}
