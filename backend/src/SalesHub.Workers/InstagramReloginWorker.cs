using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SalesHub.Core.Domain.Entities;
using SalesHub.Infrastructure.Instagram;
using SalesHub.Infrastructure.Persistence;

namespace SalesHub.Workers;

/// <summary>
/// Self-heal del login de Instagram. El problema: el login batch del scraper corre UNA vez al
/// arrancar el container; si en ese momento el túnel del proxy (iProxy del celu del user) está
/// caído, TODAS las cuentas quedan deslogueadas y nada las recupera hasta un restart o un
/// login-all manual. Este worker cierra ese hueco: cada pocos minutos PRUEBA barato (HttpClient,
/// sin browser) si el proxy llega a Instagram y, apenas vuelve, RELOGUEA solo las cuentas que
/// están caídas y tienen campañas activas — sin que el user mire el dashboard de iProxy ni toque
/// login-all. Con el túnel vivo, el widget de auto-follow del panel pasa de "Sin login" a
/// "Andando" en un par de ticks.
///
/// Gate por el mismo flag 'instagram' que el resto del stack IG. Solo toca cuentas con ProxyUrl
/// propio (nunca expone una cuenta por la IP del server) y con al menos una campaña activa (no
/// releva cuentas ociosas: apilar sesiones sin uso en la misma IP solo las correlaciona).
/// </summary>
public class InstagramReloginWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly IOptions<InstagramOptions> _opts;
    private readonly IConfiguration _config;
    private readonly ILogger<InstagramReloginWorker> _log;

    public InstagramReloginWorker(
        IServiceScopeFactory scopes,
        IOptions<InstagramOptions> opts,
        IConfiguration config,
        ILogger<InstagramReloginWorker> log)
    {
        _scopes = scopes; _opts = opts; _config = config; _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromMinutes(
            Math.Max(1, _config.GetValue<int>("Workers:InstagramReloginIntervalMinutes", 3)));
        _log.LogInformation("InstagramReloginWorker started (self-heal login, interval {Min}m, gate flag 'instagram')",
            interval.TotalMinutes);

        // Arranca después del scraper (que ya intenta el login batch inicial): así este worker
        // solo actúa sobre lo que quedó caído.
        await Task.Delay(TimeSpan.FromMinutes(3), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try { await TickAsync(stoppingToken); }
            catch (OperationCanceledException) { }
            catch (Exception ex) { _log.LogError(ex, "InstagramReloginWorker tick failed"); }
            await Task.Delay(interval, stoppingToken);
        }
    }

    private async Task TickAsync(CancellationToken ct)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        if (!await db.IsFlagOnAsync("instagram", _config.GetValue<bool>("Workers:InstagramAutoStart", true), ct))
            return;

        // Cuentas caídas que vale la pena revivir: activas, no bloqueadas, deslogueadas, con
        // proxy propio y con al menos una campaña activa.
        var activeCampaignAccountIds = (await db.InstagramFollowCampaigns
                .Where(c => c.IsActive)
                .Select(c => c.InstagramAccountId)
                .Distinct()
                .ToListAsync(ct))
            .ToHashSet();
        if (activeCampaignAccountIds.Count == 0) return;

        var down = await db.InstagramAccounts
            .Where(a => a.IsActive && !a.IsActionBlocked && !a.IsLoggedIn
                     && a.ProxyUrl != null && a.ProxyUrl != "")
            .ToListAsync(ct);
        down = down.Where(a => activeCampaignAccountIds.Contains(a.Id)).ToList();
        if (down.Count == 0) return;

        var crypto = scope.ServiceProvider.GetRequiredService<InstagramEncryptionService>();
        var recovered = new List<string>();

        // Agrupamos por ProxyUrl y probamos cada proxy UNA vez: si el túnel está caído no
        // spinneamos ningún login (el probe es un GET barato de 15s, sin browser).
        foreach (var group in down.GroupBy(a => a.ProxyUrl!))
        {
            if (!await InstagramClient.ProbeProxyReachesInstagramAsync(group.Key, _log, ct))
            {
                _log.LogDebug("self-heal IG: el proxy de {N} cuenta/s aún no llega a Instagram — reintento el próximo tick",
                    group.Count());
                continue;
            }

            foreach (var account in group)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    // La cuenta está marcada como caída: descartamos cookies viejas para forzar
                    // un login fresco (usuario + pass + TOTP) en vez de restaurar una sesión muerta.
                    if (!string.IsNullOrEmpty(account.SessionCookiesJson))
                    {
                        account.SessionCookiesJson = null;
                        await db.SaveChangesAsync(ct);
                    }

                    await using var client = new InstagramClient(db, crypto, _opts.Value, _log);
                    await client.InitializeAsync(account, ct);

                    if (await client.CheckSessionAsync(ct))
                    {
                        account.IsLoggedIn = true;
                        account.LastLoginAt = DateTimeOffset.UtcNow;
                        await client.SaveSessionCookiesAsync(account, ct);
                        await db.SaveChangesAsync(ct);
                        recovered.Add(account.Username);
                    }
                    else
                    {
                        // Login no tomó (ej. quedó esperando 2FA): dejamos limpio para reintentar.
                        account.IsLoggedIn = false;
                        account.SessionCookiesJson = null;
                        await db.SaveChangesAsync(ct);
                        _log.LogWarning("self-heal IG: {User} no quedó logueada tras el intento (¿2FA?)", account.Username);
                    }
                }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "self-heal IG: falló el relogin de {User}", account.Username);
                }
            }
        }

        if (recovered.Count > 0)
            _log.LogWarning("self-heal IG: proxy de vuelta → reconectadas {N} cuenta/s ({List})",
                recovered.Count, string.Join(", ", recovered));
    }
}
