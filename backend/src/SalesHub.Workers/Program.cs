using Microsoft.EntityFrameworkCore;
using SalesHub.Infrastructure;
using SalesHub.Infrastructure.Persistence;
using SalesHub.Infrastructure.Seed;
using SalesHub.Workers;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddSalesHubInfrastructure(builder.Configuration);

builder.Services.AddHostedService<InstanceMonitorService>();
builder.Services.AddHostedService<HumanizedSenderService>();
builder.Services.AddHostedService<PipelineSchedulerService>();
builder.Services.AddHostedService<CompetitorIngestWorker>();
builder.Services.AddHostedService<TrendsIngestWorker>();
builder.Services.AddHostedService<ConversationAgentWorker>();
builder.Services.AddHostedService<InstagramScraperWorker>();
builder.Services.AddHostedService<InstagramStructureAnalyzerWorker>();
builder.Services.AddHostedService<InstagramFollowWorker>();
builder.Services.AddHostedService<InstagramDmWorker>();
builder.Services.AddHostedService<InstagramInboxWorker>();
builder.Services.AddHostedService<SocialContentWorker>();

var host = builder.Build();

// Solo migrar/seedear si estamos en entorno de desarrollo local
// (cuando el worker corre en el droplet junto a la API)
// Si se conecta a una DB remota, la migración ya la hizo la API.
if (!string.IsNullOrEmpty(builder.Configuration["Seed:AdminEmail"]))
{
    using (var scope = host.Services.CreateScope())
    {
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        await db.Database.MigrateAsync();
        var adminEmail = builder.Configuration["Seed:AdminEmail"];
        var adminPassword = builder.Configuration["Seed:AdminPassword"];
        await DatabaseSeeder.SeedAsync(db, adminEmail, adminPassword);
    }
}

await host.RunAsync();
