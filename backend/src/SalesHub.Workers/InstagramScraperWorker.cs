using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SalesHub.Infrastructure.Instagram;
using SalesHub.Infrastructure.Persistence;

namespace SalesHub.Workers;

/// <summary>
/// Worker que ejecuta periódicamente el scraper de Instagram.
/// Procesa todos los InstagramScrapeTargets configurados.
///
/// Al iniciar, hace login batch de todas las cuentas activas
/// para tener las sesiones listas.
/// </summary>
public class InstagramScraperWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly IOptions<InstagramOptions> _opts;
    private readonly ILogger<InstagramScraperWorker> _log;

    public InstagramScraperWorker(
        IServiceScopeFactory scopes,
        IOptions<InstagramOptions> opts,
        ILogger<InstagramScraperWorker> log)
    {
        _scopes = scopes;
        _opts = opts;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromMinutes(_opts.Value.ScraperIntervalMinutes);

        _log.LogInformation("InstagramScraperWorker started (interval: {Interval}m)", interval.TotalMinutes);

        // Login batch de todas las cuentas activas al iniciar
        await LoginAllAccountsAsync(stoppingToken);

        // Esperar un poco antes del primer scrape
        await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunScrapeAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "InstagramScraperWorker run failed");
            }

            await Task.Delay(interval, stoppingToken);
        }
    }

    /// <summary>
    /// Hace login de todas las cuentas activas que no están logueadas o tienen sesión vencida.
    /// </summary>
    private async Task LoginAllAccountsAsync(CancellationToken ct)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var crypto = scope.ServiceProvider.GetRequiredService<InstagramEncryptionService>();

        var accounts = await db.InstagramAccounts
            .Where(a => a.IsActive && !a.IsActionBlocked)
            .ToListAsync(ct);

        if (accounts.Count == 0)
        {
            _log.LogInformation("No hay cuentas de Instagram activas para loguear");
            return;
        }

        _log.LogInformation("Login batch: {Count} cuentas activas encontradas", accounts.Count);

        foreach (var account in accounts)
        {
            try
            {
                // Si ya está logueada y tiene cookies, verificar si la sesión sigue activa
                if (account.IsLoggedIn && !string.IsNullOrEmpty(account.SessionCookiesJson))
                {
                    await using var checkClient = new InstagramClient(db, crypto, _opts.Value, _log);
                    await checkClient.InitializeAsync(account, ct);
                    var sessionOk = await checkClient.CheckSessionAsync(ct);

                    if (sessionOk)
                    {
                        _log.LogInformation("Sesión de {User} ya activa, saltando login", account.Username);
                        continue;
                    }

                    _log.LogInformation("Sesión de {User} expirada, relogueando...", account.Username);
                    account.SessionCookiesJson = null;
                    account.IsLoggedIn = false;
                    await db.SaveChangesAsync(ct);
                }

                // Hacer login
                await using var client = new InstagramClient(db, crypto, _opts.Value, _log);
                await client.InitializeAsync(account, ct);

                _log.LogInformation("Login batch exitoso: {User}", account.Username);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Login batch falló para {User}", account.Username);
            }
        }

        _log.LogInformation("Login batch completado");
    }

    private async Task RunScrapeAsync(CancellationToken ct)
    {
        using var scope = _scopes.CreateScope();
        var scraper = scope.ServiceProvider.GetRequiredService<InstagramLeadScraper>();

        _log.LogInformation("Ejecutando Instagram scraper...");
        var created = await scraper.RunAllTargetsAsync(ct);
        _log.LogInformation("Instagram scraper completado: {Count} leads creados", created);
    }
}
