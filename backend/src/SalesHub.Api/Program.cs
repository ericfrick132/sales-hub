using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;
using SalesHub.Api.Auth;
using SalesHub.Core.Domain.Enums;
using SalesHub.Infrastructure;
using SalesHub.Infrastructure.Options;
using SalesHub.Infrastructure.Persistence;
using SalesHub.Infrastructure.Seed;
using SalesHub.Workers;

// Npgsql 6+ defaults reject non-UTC DateTimeOffsets for timestamptz columns; the codebase passes
// local-time values in several places, so opt back into legacy behavior to avoid runtime errors.
AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", true);
AppContext.SetSwitch("Npgsql.DisableDateTimeInfinityConversions", true);

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSalesHubInfrastructure(builder.Configuration);
builder.Services.AddScoped<IJwtService, JwtService>();

if ((Environment.GetEnvironmentVariable("SALESHUB_RUN_WORKERS") ?? "false") == "true")
{
    builder.Services.AddHostedService<InstanceMonitorService>();
    builder.Services.AddHostedService<HumanizedSenderService>();
    builder.Services.AddHostedService<PipelineSchedulerService>();
    builder.Services.AddHostedService<GooglePlacesSchedulerService>();
    builder.Services.AddHostedService<CompetitorIngestWorker>();
    builder.Services.AddHostedService<TrendsIngestWorker>();
    builder.Services.AddHostedService<ConversationAgentWorker>();
    builder.Services.AddHostedService<SeoContentWorker>();
    builder.Services.AddHostedService<SeoDistributeWorker>();
    builder.Services.AddHostedService<SocialContentWorker>();              // posteos automáticos → Buffer (gated por Workers:PosteosAutoStart)

    // Workers de Instagram. Corren acá (no en un contenedor aparte) porque la imagen
    // ya trae Playwright/Chromium y el droplet tiene RAM de sobra. Sin esto, las
    // automatizaciones de IG (follow, DM, inbox) quedan deployadas pero nunca se ejecutan.
    builder.Services.AddHostedService<InstagramScraperWorker>();          // + login batch de cuentas
    builder.Services.AddHostedService<InstagramStructureAnalyzerWorker>(); // self-healing de selectores
    builder.Services.AddHostedService<InstagramFollowWorker>();            // campañas de auto-follow
    builder.Services.AddHostedService<InstagramDmWorker>();                // envío de DMs (Channel=Instagram)
    builder.Services.AddHostedService<InstagramInboxWorker>();             // poll del inbox → conversaciones
}

builder.Services.AddControllers().AddJsonOptions(o =>
{
    o.JsonSerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
    o.JsonSerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase;
});

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new OpenApiInfo { Title = "SalesHub API", Version = "v1" });
    c.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        Name = "Authorization", Type = SecuritySchemeType.Http, Scheme = "bearer", BearerFormat = "JWT"
    });
    c.AddSecurityRequirement(new OpenApiSecurityRequirement
    {
        { new OpenApiSecurityScheme { Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = "Bearer" } }, Array.Empty<string>() }
    });
});

var jwt = builder.Configuration.GetSection("Jwt").Get<JwtOptions>()
    ?? throw new InvalidOperationException("Missing Jwt config");
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(o =>
    {
        o.TokenValidationParameters = new TokenValidationParameters
        {
            ValidIssuer = jwt.Issuer,
            ValidAudience = jwt.Audience,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt.SigningKey)),
            ValidateIssuer = true, ValidateAudience = true, ValidateLifetime = true, ValidateIssuerSigningKey = true,
            RoleClaimType = System.Security.Claims.ClaimTypes.Role
        };
    });
builder.Services.AddAuthorization(opts =>
{
    opts.AddPolicy("Admin", p => p.RequireRole(SellerRole.Admin.ToString()));
});

var origins = (builder.Configuration["Cors:Origins"] ?? "http://localhost:5173")
    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
builder.Services.AddCors(c => c.AddDefaultPolicy(p => p
    .WithOrigins(origins)
    .AllowAnyHeader()
    .AllowAnyMethod()
    .AllowCredentials()));

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
    if ((Environment.GetEnvironmentVariable("SALESHUB_AUTO_MIGRATE") ?? "true") == "true")
    {
        await db.Database.MigrateAsync();
        await DatabaseSeeder.SeedAsync(db,
            builder.Configuration["Seed:AdminEmail"],
            builder.Configuration["Seed:AdminPassword"]);
    }
}

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseCors();
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();

app.MapGet("/", () => Results.Ok(new { status = "ok", service = "sales-hub", utc = DateTimeOffset.UtcNow }));
app.MapGet("/health", () => Results.Ok(new { status = "healthy" }));

app.Run();
