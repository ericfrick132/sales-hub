using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;
using SalesHub.Api.Auth;
using SalesHub.Core.Domain.Enums;
using Microsoft.AspNetCore.StaticFiles;
using SalesHub.Api.WebSockets;
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
builder.Services.AddHttpClient(); // IHttpClientFactory para el LeadImportWorker
// Bot de configuración por WhatsApp (menú numerado del maestro): ejecutor que pega a la propia API
// con un JWT admin minteado + el relay navegador. Ver src/SalesHub.Api/AdminMenu/.
builder.Services.AddHttpClient<SalesHub.Api.AdminMenu.AdminApiExecutor>();
builder.Services.AddScoped<SalesHub.Api.AdminMenu.AdminMenuRelay>();

// WebSocket hub para devices Android
builder.Services.AddSingleton<DeviceHub>();
builder.Services.AddSingleton<DeviceWebSocketMiddleware>();
builder.Services.AddSingleton<SalesHub.Infrastructure.Services.BridgeDirectSendService>();

if ((Environment.GetEnvironmentVariable("SALESHUB_RUN_WORKERS") ?? "false") == "true")
{
    builder.Services.AddHostedService<InstanceMonitorService>();
    builder.Services.AddHostedService<HumanizedSenderService>();
    builder.Services.AddHostedService<PipelineSchedulerService>();
    builder.Services.AddHostedService<LeadRebalanceWorker>();              // auto-reparto: pool retry + rescate de congelados + drenaje a líneas dedicadas (flag 'rebalance')
    builder.Services.AddHostedService<GooglePlacesSchedulerService>();
    builder.Services.AddHostedService<CompetitorIngestWorker>();
    builder.Services.AddHostedService<TrendsIngestWorker>();
    builder.Services.AddHostedService<ConversationAgentWorker>();
    builder.Services.AddHostedService<SeoContentWorker>();
    builder.Services.AddHostedService<SeoDistributeWorker>();
    builder.Services.AddHostedService<SocialContentWorker>();              // posteos automáticos → Buffer (gated por Workers:PosteosAutoStart)
    builder.Services.AddHostedService<LeadImportWorker>();                 // re-engagement: import de leads de TurnosPro/GymHero
    builder.Services.AddHostedService<LeadStateGuardWorker>();             // pull-guard: corta el follow-up de leads que ya convirtieron en su producto
    builder.Services.AddHostedService<EvolutionChatSyncWorker>();          // TODOS los chats de cada línea vinculada → Conversaciones (backfill + baches de webhook; flag 'chat-sync')
    builder.Services.AddHostedService<SlaAlertWorker>();                   // avisa al maestro cuando un chat pasa el SLA sin respuesta (flag 'sla-alerts')

    // Workers de Instagram. Corren acá (no en un contenedor aparte) porque la imagen
    // ya trae Playwright/Chromium y el droplet tiene RAM de sobra. Sin esto, las
    // automatizaciones de IG (follow, DM, inbox) quedan deployadas pero nunca se ejecutan.
    builder.Services.AddHostedService<InstagramScraperWorker>();          // + login batch de cuentas
    // Singleton + hosted sobre la MISMA instancia: así el controller puede inyectarlo
    // (antes solo estaba como hosted → no resoluble → 500 en /api/instagram/structure/*).
    builder.Services.AddSingleton<InstagramStructureAnalyzerWorker>(); // self-healing de selectores
    builder.Services.AddHostedService(sp => sp.GetRequiredService<InstagramStructureAnalyzerWorker>());
    builder.Services.AddHostedService<InstagramReloginWorker>();           // self-heal: cuando el proxy vuelve, relogin auto de cuentas caídas con campañas (flag 'instagram')
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

app.UseStaticFiles(new StaticFileOptions
{
    ContentTypeProvider = new FileExtensionContentTypeProvider
    {
        Mappings = { [".apk"] = "application/vnd.android.package-archive" }
    }
});
app.UseDeviceWebSockets();
app.UseCors();
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();

app.MapGet("/", () => Results.Ok(new { status = "ok", service = "sales-hub", utc = DateTimeOffset.UtcNow }));
app.MapGet("/health", () => Results.Ok(new { status = "healthy" }));

app.Run();
