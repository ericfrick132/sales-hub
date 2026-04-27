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

var host = builder.Build();

using (var scope = host.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
    await db.Database.MigrateAsync();
    var adminEmail = builder.Configuration["Seed:AdminEmail"];
    var adminPassword = builder.Configuration["Seed:AdminPassword"];
    await DatabaseSeeder.SeedAsync(db, adminEmail, adminPassword);
}

await host.RunAsync();
