using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SalesHub.Core.Domain.Entities;
using SalesHub.Core.Domain.Enums;
using SalesHub.Infrastructure.Persistence;

namespace SalesHub.Infrastructure.Instagram;

/// <summary>
/// Orquesta las campañas de auto-follow:
/// 1. Top-up de la cola: si quedan pocos pendientes, scrapea otro lote de
///    followers del SourceHandle y los encola (skipeando handles ya encolados
///    en la misma campaña).
/// 2. Ejecuta una acción pendiente por campaña por tick respetando el
///    DailyRate (cap diario por campaña).
/// </summary>
public class InstagramFollowService
{
    private readonly ApplicationDbContext _db;
    private readonly InstagramEncryptionService _crypto;
    private readonly InstagramOptions _opts;
    private readonly ILogger<InstagramFollowService> _log;

    public InstagramFollowService(
        ApplicationDbContext db,
        InstagramEncryptionService crypto,
        InstagramOptions opts,
        ILogger<InstagramFollowService> log)
    {
        _db = db;
        _crypto = crypto;
        _opts = opts;
        _log = log;
    }

    /// <summary>
    /// Procesa todas las campañas activas: top-up de cola + ejecución de
    /// una acción pendiente por campaña.
    /// </summary>
    public async Task RunAllAsync(CancellationToken ct = default)
    {
        var campaigns = await _db.InstagramFollowCampaigns
            .Include(c => c.InstagramAccount)
            .Where(c => c.IsActive)
            .ToListAsync(ct);

        if (campaigns.Count == 0)
        {
            return;
        }

        foreach (var campaign in campaigns)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                await RunCampaignAsync(campaign, ct);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Error procesando campaña {CampaignId} ({Source})",
                    campaign.Id, campaign.SourceHandle);
            }
        }
    }

    /// <summary>
    /// Procesa una campaña: top-up (si hace falta) + ejecutar una acción
    /// pendiente si el cap diario lo permite.
    /// </summary>
    public async Task RunCampaignAsync(InstagramFollowCampaign campaign, CancellationToken ct = default)
    {
        var account = campaign.InstagramAccount;
        if (account is null)
        {
            _log.LogWarning("Campaña {Id} no tiene InstagramAccount cargada", campaign.Id);
            return;
        }

        if (!account.IsActive || account.IsActionBlocked)
        {
            _log.LogInformation(
                "Campaña {Id}: cuenta {User} no disponible (active={Active}, blocked={Blocked})",
                campaign.Id, account.Username, account.IsActive, account.IsActionBlocked);
            return;
        }

        // ¿Hit del MaxTotal?
        if (campaign.MaxTotalFollows > 0 && campaign.TotalFollowed >= campaign.MaxTotalFollows)
        {
            _log.LogInformation("Campaña {Id}: alcanzó MaxTotalFollows ({Max})",
                campaign.Id, campaign.MaxTotalFollows);
            campaign.IsActive = false;
            campaign.UpdatedAt = DateTimeOffset.UtcNow;
            await _db.SaveChangesAsync(ct);
            return;
        }

        // ¿Llegamos al cap diario?
        var todayCount = await CountFollowsTodayAsync(campaign.Id, ct);
        if (todayCount >= campaign.DailyRate)
        {
            _log.LogDebug("Campaña {Id}: cap diario alcanzado ({Count}/{Rate})",
                campaign.Id, todayCount, campaign.DailyRate);
            return;
        }

        // Contar pendientes y top-up si la cola está baja
        var pending = await _db.InstagramFollowActions
            .CountAsync(a => a.CampaignId == campaign.Id && a.Status == InstagramFollowActionStatus.Pending, ct);

        if (pending < campaign.MinQueuedThreshold)
        {
            await TopUpQueueAsync(campaign, account, ct);
        }

        // Tomar siguiente acción pendiente
        var next = await _db.InstagramFollowActions
            .Where(a => a.CampaignId == campaign.Id && a.Status == InstagramFollowActionStatus.Pending)
            .OrderBy(a => a.EnqueuedAt)
            .FirstOrDefaultAsync(ct);

        if (next is null)
        {
            _log.LogDebug("Campaña {Id}: no hay acciones pendientes", campaign.Id);
            return;
        }

        await ExecuteFollowAsync(campaign, account, next, ct);
    }

    /// <summary>Scrapea otro lote de followers del SourceHandle y los encola.</summary>
    private async Task TopUpQueueAsync(
        InstagramFollowCampaign campaign,
        InstagramAccount account,
        CancellationToken ct)
    {
        _log.LogInformation("Top-up cola campaña {Id} ({Source}): scrapeando {Size} followers",
            campaign.Id, campaign.SourceHandle, campaign.ScrapeBatchSize);

        await using var client = new InstagramClient(_db, _crypto, _opts, _log);

        try
        {
            await client.InitializeAsync(account, ct);

            var sessionOk = await client.CheckSessionAsync(ct);
            if (!sessionOk)
            {
                _log.LogWarning("Sesión caída para {User}, saltando top-up", account.Username);
                account.IsLoggedIn = false;
                account.SessionCookiesJson = null;
                await _db.SaveChangesAsync(ct);
                return;
            }

            var profiles = await client.ScrapeFollowersAsync(
                campaign.SourceHandle,
                campaign.ScrapeBatchSize,
                ct);

            if (profiles.Count == 0)
            {
                _log.LogWarning("Top-up campaña {Id}: 0 followers scrapeados", campaign.Id);
                campaign.LastScrapeAt = DateTimeOffset.UtcNow;
                campaign.UpdatedAt = DateTimeOffset.UtcNow;
                await _db.SaveChangesAsync(ct);
                return;
            }

            // Filtrar handles ya encolados en esta campaña
            var handles = profiles.Select(p => p.Handle).ToList();
            var existing = await _db.InstagramFollowActions
                .Where(a => a.CampaignId == campaign.Id && handles.Contains(a.TargetHandle))
                .Select(a => a.TargetHandle)
                .ToListAsync(ct);
            var existingSet = new HashSet<string>(existing);

            var enqueued = 0;
            foreach (var profile in profiles)
            {
                if (existingSet.Contains(profile.Handle)) continue;
                // No seguirse a sí mismo
                if (string.Equals(profile.Handle, account.Username, StringComparison.OrdinalIgnoreCase)) continue;

                _db.InstagramFollowActions.Add(new InstagramFollowAction
                {
                    Id = Guid.NewGuid(),
                    CampaignId = campaign.Id,
                    InstagramAccountId = account.Id,
                    TargetHandle = profile.Handle,
                    Status = InstagramFollowActionStatus.Pending,
                    EnqueuedAt = DateTimeOffset.UtcNow
                });
                enqueued++;
            }

            campaign.TotalEnqueued += enqueued;
            campaign.LastScrapeAt = DateTimeOffset.UtcNow;
            campaign.UpdatedAt = DateTimeOffset.UtcNow;

            await client.SaveSessionCookiesAsync(account, ct);
            await _db.SaveChangesAsync(ct);

            _log.LogInformation("Top-up campaña {Id}: {Enqueued} encolados (de {Total} scrapeados)",
                campaign.Id, enqueued, profiles.Count);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Error en top-up de campaña {Id}", campaign.Id);
        }
    }

    /// <summary>Ejecuta un follow individual y actualiza estado.</summary>
    private async Task ExecuteFollowAsync(
        InstagramFollowCampaign campaign,
        InstagramAccount account,
        InstagramFollowAction action,
        CancellationToken ct)
    {
        action.Attempts++;

        await using var client = new InstagramClient(_db, _crypto, _opts, _log);

        try
        {
            await client.InitializeAsync(account, ct);

            var sessionOk = await client.CheckSessionAsync(ct);
            if (!sessionOk)
            {
                _log.LogWarning("Sesión caída ejecutando follow {Handle}", action.TargetHandle);
                account.IsLoggedIn = false;
                account.SessionCookiesJson = null;
                await _db.SaveChangesAsync(ct);
                return;
            }

            var result = await client.FollowUserAsync(action.TargetHandle, ct);
            action.ExecutedAt = DateTimeOffset.UtcNow;

            switch (result)
            {
                case FollowResult.Followed:
                    action.Status = InstagramFollowActionStatus.Done;
                    campaign.TotalFollowed++;
                    campaign.LastFollowAt = DateTimeOffset.UtcNow;
                    await UpdateDailyMetricsAsync(account.Id, followed: 1, blocked: 0, ct);
                    break;

                case FollowResult.AlreadyFollowing:
                    action.Status = InstagramFollowActionStatus.Skipped;
                    campaign.TotalSkipped++;
                    break;

                case FollowResult.NotFound:
                    action.Status = InstagramFollowActionStatus.Skipped;
                    action.ErrorMessage = "not_found";
                    campaign.TotalSkipped++;
                    break;

                case FollowResult.Blocked:
                    action.Status = InstagramFollowActionStatus.Pending;
                    action.ExecutedAt = null;
                    action.ErrorMessage = "action_blocked";
                    account.IsActionBlocked = true;
                    account.BlockedUntil = DateTimeOffset.UtcNow.AddHours(24);
                    await UpdateDailyMetricsAsync(account.Id, followed: 0, blocked: 1, ct);
                    _log.LogWarning(
                        "Action Blocked en campaña {Id}: cuenta {User} bloqueada 24h",
                        campaign.Id, account.Username);
                    break;

                case FollowResult.Failed:
                default:
                    action.Status = action.Attempts >= 3
                        ? InstagramFollowActionStatus.Failed
                        : InstagramFollowActionStatus.Pending;
                    if (action.Status == InstagramFollowActionStatus.Failed)
                    {
                        campaign.TotalFailed++;
                        action.ErrorMessage = "follow_failed";
                    }
                    else
                    {
                        action.ExecutedAt = null;
                    }
                    break;
            }

            account.LastUsedAt = DateTimeOffset.UtcNow;
            campaign.UpdatedAt = DateTimeOffset.UtcNow;

            await client.SaveSessionCookiesAsync(account, ct);
            await _db.SaveChangesAsync(ct);

            _log.LogInformation("Follow campaña {Id} → {Handle}: {Result}",
                campaign.Id, action.TargetHandle, result);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Error ejecutando follow {Handle} de campaña {Id}",
                action.TargetHandle, campaign.Id);
            action.ErrorMessage = ex.Message.Length > 1000 ? ex.Message[..1000] : ex.Message;
            if (action.Attempts >= 3)
            {
                action.Status = InstagramFollowActionStatus.Failed;
                campaign.TotalFailed++;
            }
            await _db.SaveChangesAsync(ct);
        }
    }

    private async Task<int> CountFollowsTodayAsync(Guid campaignId, CancellationToken ct)
    {
        var since = DateTime.UtcNow.Date;
        return await _db.InstagramFollowActions
            .CountAsync(a =>
                a.CampaignId == campaignId &&
                a.Status == InstagramFollowActionStatus.Done &&
                a.ExecutedAt != null &&
                a.ExecutedAt >= since, ct);
    }

    private async Task UpdateDailyMetricsAsync(Guid accountId, int followed, int blocked, CancellationToken ct)
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

        if (followed > 0) metrics.FollowsDone += followed;
        if (blocked > 0)
        {
            metrics.FollowsBlocked += blocked;
            metrics.ActionBlocks += blocked;
        }
        metrics.UpdatedAt = DateTimeOffset.UtcNow;
    }
}
