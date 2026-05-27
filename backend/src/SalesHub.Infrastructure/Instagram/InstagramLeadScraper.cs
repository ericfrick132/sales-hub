using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SalesHub.Core.Domain.Entities;
using SalesHub.Core.Domain.Enums;
using SalesHub.Infrastructure.Persistence;

namespace SalesHub.Infrastructure.Instagram;

/// <summary>
/// Orquesta el scraping de leads de Instagram usando InstagramClient.
/// Procesa los InstagramScrapeTargets configurados y crea leads en la DB.
/// </summary>
public class InstagramLeadScraper
{
    private readonly ApplicationDbContext _db;
    private readonly InstagramEncryptionService _crypto;
    private readonly InstagramOptions _opts;
    private readonly ILogger<InstagramLeadScraper> _log;

    public InstagramLeadScraper(
        ApplicationDbContext db,
        InstagramEncryptionService crypto,
        InstagramOptions opts,
        ILogger<InstagramLeadScraper> log)
    {
        _db = db;
        _crypto = crypto;
        _opts = opts;
        _log = log;
    }

    /// <summary>
    /// Ejecuta el scraping para todos los targets activos.
    /// </summary>
    public async Task<int> RunAllTargetsAsync(CancellationToken ct = default)
    {
        var targets = await _db.InstagramScrapeTargets
            .Include(t => t.AssignedAccount)
            .Where(t => t.IsActive)
            .ToListAsync(ct);

        if (targets.Count == 0)
        {
            _log.LogInformation("No hay targets de Instagram configurados");
            return 0;
        }

        var totalCreated = 0;

        foreach (var target in targets)
        {
            try
            {
                var created = await RunTargetAsync(target, ct);
                totalCreated += created;
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Error scraping target {Type}:{Value} para producto {Product}",
                    target.TargetType, target.TargetValue, target.ProductKey);
            }
        }

        return totalCreated;
    }

    /// <summary>
    /// Ejecuta el scraping para un target específico.
    /// </summary>
    public async Task<int> RunTargetAsync(InstagramScrapeTarget target, CancellationToken ct = default)
    {
        // Buscar una cuenta disponible
        var account = target.AssignedAccount;
        if (account is null)
        {
            account = await _db.InstagramAccounts
                .Where(a => a.IsActive && !a.IsActionBlocked)
                .FirstOrDefaultAsync(ct);

            if (account is null)
            {
                _log.LogWarning("No hay cuentas de Instagram disponibles para scraping");
                return 0;
            }
        }

        if (account.IsActionBlocked)
        {
            _log.LogWarning("Cuenta {User} está bloqueada, saltando target {Type}:{Value}",
                account.Username, target.TargetType, target.TargetValue);
            return 0;
        }

        var created = 0;

        // Inicializar cliente
        await using var client = new InstagramClient(_db, _crypto, _opts, _log);
        await client.InitializeAsync(account, ct);

        // Verificar sesión
        var sessionOk = await client.CheckSessionAsync(ct);
        if (!sessionOk)
        {
            _log.LogWarning("Sesión de Instagram expirada para {User}, intentando reloguear...", account.Username);
            account.SessionCookiesJson = null;
            await _db.SaveChangesAsync(ct);
            return 0;
        }

        List<InstagramProfile> profiles;

        switch (target.TargetType)
        {
            case "competitor_followers":
                profiles = await client.ScrapeFollowersAsync(target.TargetValue, target.MaxPerRun, ct);
                break;

            case "hashtag_commenters":
                profiles = await client.ScrapeHashtagCommentersAsync(target.TargetValue, target.MaxPerRun, ct);
                break;

            default:
                _log.LogWarning("Tipo de target desconocido: {Type}", target.TargetType);
                return 0;
        }

        // Guardar perfiles como leads
        foreach (var profile in profiles)
        {
            try
            {
                // Verificar si ya existe un lead con ese InstagramHandle para este producto
                var exists = await _db.Leads.AnyAsync(
                    l => l.ProductKey == target.ProductKey && l.InstagramHandle == profile.Handle, ct);

                if (exists) continue;

                var lead = new Lead
                {
                    Id = Guid.NewGuid(),
                    ProductKey = target.ProductKey,
                    Source = LeadSource.InstagramScraper,
                    Name = profile.DisplayName,
                    InstagramHandle = profile.Handle,
                    Status = LeadStatus.New,
                    CreatedAt = DateTimeOffset.UtcNow
                };

                _db.Leads.Add(lead);
                created++;
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Error guardando lead {Handle}", profile.Handle);
            }
        }

        // Actualizar métricas
        target.LastScrapedAt = DateTimeOffset.UtcNow;
        target.TotalLeadsCreated += created;
        target.UpdatedAt = DateTimeOffset.UtcNow;

        account.LastUsedAt = DateTimeOffset.UtcNow;

        // Guardar cookies actualizadas
        await client.SaveSessionCookiesAsync(account, ct);

        // Actualizar métricas diarias
        await UpdateDailyMetricsAsync(account.Id, profiles.Count, created, ct);

        await _db.SaveChangesAsync(ct);

        _log.LogInformation("Target {Type}:{Value} — {Created} leads creados de {Total} perfiles",
            target.TargetType, target.TargetValue, created, profiles.Count);

        return created;
    }

    private async Task UpdateDailyMetricsAsync(Guid accountId, int profilesScraped, int leadsCreated, CancellationToken ct)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var metrics = await _db.InstagramMetrics
            .FirstOrDefaultAsync(m => m.InstagramAccountId == accountId && m.Date == today, ct);

        if (metrics is null)
        {
            metrics = new InstagramMetrics
            {
                Id = Guid.NewGuid(),
                InstagramAccountId = accountId,
                Date = today
            };
            _db.InstagramMetrics.Add(metrics);
        }

        metrics.ProfilesScraped += profilesScraped;
        metrics.LeadsCreated += leadsCreated;
        metrics.UpdatedAt = DateTimeOffset.UtcNow;
    }
}
