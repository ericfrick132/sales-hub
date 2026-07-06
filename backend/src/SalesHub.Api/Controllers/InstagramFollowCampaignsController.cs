using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SalesHub.Core.Domain.Entities;
using SalesHub.Core.Domain.Enums;
using SalesHub.Infrastructure.Instagram;
using SalesHub.Infrastructure.Persistence;

namespace SalesHub.Api.Controllers;

[ApiController]
[Route("api/instagram-follow-campaigns")]
[Authorize(Roles = "Admin")]
public class InstagramFollowCampaignsController : ControllerBase
{
    private readonly ApplicationDbContext _db;
    private readonly InstagramFollowService _follow;
    private readonly ILogger<InstagramFollowCampaignsController> _log;
    private readonly IServiceScopeFactory _scopes;

    public InstagramFollowCampaignsController(
        ApplicationDbContext db,
        InstagramFollowService follow,
        ILogger<InstagramFollowCampaignsController> log,
        IServiceScopeFactory scopes)
    {
        _db = db;
        _follow = follow;
        _log = log;
        _scopes = scopes;
    }

    [HttpGet]
    public async Task<IActionResult> GetAll()
    {
        var today = DateTime.UtcNow.Date;

        var campaigns = await _db.InstagramFollowCampaigns
            .Include(c => c.InstagramAccount)
            .OrderByDescending(c => c.CreatedAt)
            .Select(c => new CampaignDto
            {
                Id = c.Id,
                InstagramAccountId = c.InstagramAccountId,
                InstagramAccountUsername = c.InstagramAccount != null ? c.InstagramAccount.Username : null,
                SourceHandle = c.SourceHandle,
                SourceMode = c.SourceMode.ToString(),
                DailyRate = c.DailyRate,
                ActiveHourStart = c.ActiveHourStart,
                ActiveHourEnd = c.ActiveHourEnd,
                MaxTotalFollows = c.MaxTotalFollows,
                IsActive = c.IsActive,
                TotalEnqueued = c.TotalEnqueued,
                TotalFollowed = c.TotalFollowed,
                TotalFailed = c.TotalFailed,
                TotalSkipped = c.TotalSkipped,
                Pending = _db.InstagramFollowActions
                    .Count(a => a.CampaignId == c.Id && a.Status == InstagramFollowActionStatus.Pending),
                FollowedToday = _db.InstagramFollowActions
                    .Count(a => a.CampaignId == c.Id
                        && a.Status == InstagramFollowActionStatus.Done
                        && a.ExecutedAt != null
                        && a.ExecutedAt >= today),
                LastScrapeAt = c.LastScrapeAt,
                LastFollowAt = c.LastFollowAt,
                CreatedAt = c.CreatedAt
            })
            .ToListAsync();

        return Ok(campaigns);
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> GetById(Guid id)
    {
        var campaign = await _db.InstagramFollowCampaigns
            .Include(c => c.InstagramAccount)
            .FirstOrDefaultAsync(c => c.Id == id);

        if (campaign is null) return NotFound();

        var recentActions = await _db.InstagramFollowActions
            .Where(a => a.CampaignId == id)
            .OrderByDescending(a => a.ExecutedAt ?? a.EnqueuedAt)
            .Take(100)
            .Select(a => new ActionDto
            {
                Id = a.Id,
                TargetHandle = a.TargetHandle,
                Status = a.Status.ToString(),
                EnqueuedAt = a.EnqueuedAt,
                ExecutedAt = a.ExecutedAt,
                Attempts = a.Attempts,
                ErrorMessage = a.ErrorMessage
            })
            .ToListAsync();

        var today = DateTime.UtcNow.Date;

        return Ok(new CampaignDetailDto
        {
            Id = campaign.Id,
            InstagramAccountId = campaign.InstagramAccountId,
            InstagramAccountUsername = campaign.InstagramAccount?.Username,
            SourceHandle = campaign.SourceHandle,
            SourceMode = campaign.SourceMode.ToString(),
            DailyRate = campaign.DailyRate,
            ActiveHourStart = campaign.ActiveHourStart,
            ActiveHourEnd = campaign.ActiveHourEnd,
            MaxTotalFollows = campaign.MaxTotalFollows,
            ScrapeBatchSize = campaign.ScrapeBatchSize,
            MinQueuedThreshold = campaign.MinQueuedThreshold,
            IsActive = campaign.IsActive,
            TotalEnqueued = campaign.TotalEnqueued,
            TotalFollowed = campaign.TotalFollowed,
            TotalFailed = campaign.TotalFailed,
            TotalSkipped = campaign.TotalSkipped,
            Pending = await _db.InstagramFollowActions
                .CountAsync(a => a.CampaignId == id && a.Status == InstagramFollowActionStatus.Pending),
            FollowedToday = await _db.InstagramFollowActions
                .CountAsync(a => a.CampaignId == id
                    && a.Status == InstagramFollowActionStatus.Done
                    && a.ExecutedAt != null
                    && a.ExecutedAt >= today),
            LastScrapeAt = campaign.LastScrapeAt,
            LastFollowAt = campaign.LastFollowAt,
            CreatedAt = campaign.CreatedAt,
            RecentActions = recentActions
        });
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateCampaignRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.SourceHandle))
            return BadRequest(new { error = "SourceHandle requerido" });

        var account = await _db.InstagramAccounts.FirstOrDefaultAsync(a => a.Id == req.InstagramAccountId);
        if (account is null) return BadRequest(new { error = "InstagramAccount no existe" });

        // Normalizar a handle limpio: tolera URL completa (instagram.com/x/), @, barras.
        var handle = CleanHandle(req.SourceHandle);
        if (string.IsNullOrEmpty(handle))
            return BadRequest(new { error = "SourceHandle inválido (poné solo el usuario, ej: accesogym)" });

        var sourceMode = Enum.TryParse<InstagramFollowSourceMode>(req.SourceMode, ignoreCase: true, out var m)
            ? m
            : InstagramFollowSourceMode.Followers;

        var campaign = new InstagramFollowCampaign
        {
            Id = Guid.NewGuid(),
            InstagramAccountId = req.InstagramAccountId,
            SourceHandle = handle,
            SourceMode = sourceMode,
            DailyRate = req.DailyRate > 0 ? req.DailyRate : 30,
            ActiveHourStart = ClampHour(req.ActiveHourStart),
            ActiveHourEnd = ClampHour(req.ActiveHourEnd),
            MaxTotalFollows = req.MaxTotalFollows,
            ScrapeBatchSize = req.ScrapeBatchSize > 0 ? req.ScrapeBatchSize : 100,
            MinQueuedThreshold = req.MinQueuedThreshold > 0 ? req.MinQueuedThreshold : 20,
            IsActive = true,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };

        _db.InstagramFollowCampaigns.Add(campaign);
        await _db.SaveChangesAsync();

        _log.LogInformation(
            "Campaña follow creada: {Id} (source={Source}, modo={Mode}, account={User}, rate={Rate}/d)",
            campaign.Id, handle, sourceMode, account.Username, campaign.DailyRate);

        return Ok(new { id = campaign.Id });
    }

    [HttpPatch("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdateCampaignRequest req)
    {
        var campaign = await _db.InstagramFollowCampaigns.FirstOrDefaultAsync(c => c.Id == id);
        if (campaign is null) return NotFound();

        if (req.IsActive.HasValue) campaign.IsActive = req.IsActive.Value;
        if (!string.IsNullOrWhiteSpace(req.SourceHandle))
        {
            var handle = CleanHandle(req.SourceHandle);
            if (string.IsNullOrEmpty(handle))
                return BadRequest(new { error = "SourceHandle inválido (poné solo el usuario, ej: accesogym)" });
            campaign.SourceHandle = handle;
        }
        if (req.SetActiveHours)
        {
            campaign.ActiveHourStart = ClampHour(req.ActiveHourStart);
            campaign.ActiveHourEnd = ClampHour(req.ActiveHourEnd);
        }
        if (!string.IsNullOrWhiteSpace(req.SourceMode)
            && Enum.TryParse<InstagramFollowSourceMode>(req.SourceMode, ignoreCase: true, out var mode))
            campaign.SourceMode = mode;
        if (req.DailyRate.HasValue && req.DailyRate.Value > 0) campaign.DailyRate = req.DailyRate.Value;
        if (req.MaxTotalFollows.HasValue) campaign.MaxTotalFollows = req.MaxTotalFollows.Value;
        if (req.ScrapeBatchSize.HasValue && req.ScrapeBatchSize.Value > 0)
            campaign.ScrapeBatchSize = req.ScrapeBatchSize.Value;
        if (req.MinQueuedThreshold.HasValue && req.MinQueuedThreshold.Value > 0)
            campaign.MinQueuedThreshold = req.MinQueuedThreshold.Value;

        campaign.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync();

        return Ok(new { id = campaign.Id });
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id)
    {
        var campaign = await _db.InstagramFollowCampaigns.FirstOrDefaultAsync(c => c.Id == id);
        if (campaign is null) return NotFound();

        _db.InstagramFollowCampaigns.Remove(campaign);
        await _db.SaveChangesAsync();

        return NoContent();
    }

    /// <summary>Dispara una iteración manual del worker para esta campaña (test).</summary>
    [HttpPost("{id:guid}/run")]
    public async Task<IActionResult> RunNow(Guid id)
    {
        if (!await _db.InstagramFollowCampaigns.AnyAsync(c => c.Id == id)) return NotFound();

        // El scrape+follow con Playwright tarda (1-2 min); lo corremos en background con su
        // propio scope y devolvemos al toque. La UI mira las stats/acciones para ver el avance.
        _ = Task.Run(async () =>
        {
            using var scope = _scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var follow = scope.ServiceProvider.GetRequiredService<InstagramFollowService>();
            var log = scope.ServiceProvider.GetRequiredService<ILogger<InstagramFollowCampaignsController>>();
            try
            {
                var campaign = await db.InstagramFollowCampaigns.Include(c => c.InstagramAccount)
                    .FirstOrDefaultAsync(c => c.Id == id);
                if (campaign is not null) await follow.RunCampaignAsync(campaign);
            }
            catch (Exception ex) { log.LogError(ex, "Run manual de campaña {Id} falló", id); }
        });

        return Accepted(new { ok = true, message = "Run disparado. Mirá las stats en unos segundos." });
    }

    /// <summary>Handle limpio: tolera URL completa (instagram.com/x/), @, barras, query.</summary>
    private static string CleanHandle(string raw)
    {
        var handle = raw.Trim();
        var urlIdx = handle.IndexOf("instagram.com/", StringComparison.OrdinalIgnoreCase);
        if (urlIdx >= 0) handle = handle[(urlIdx + "instagram.com/".Length)..];
        return handle.TrimStart('@').Trim().Trim('/').Split('/', '?')[0].Trim();
    }

    private static int? ClampHour(int? h) => h is null ? null : Math.Clamp(h.Value, 0, 23);

    // DTOs

    public record CampaignDto
    {
        public Guid Id { get; init; }
        public Guid InstagramAccountId { get; init; }
        public string? InstagramAccountUsername { get; init; }
        public string SourceHandle { get; init; } = string.Empty;
        public string SourceMode { get; init; } = nameof(InstagramFollowSourceMode.Followers);
        public int DailyRate { get; init; }
        public int? ActiveHourStart { get; init; }
        public int? ActiveHourEnd { get; init; }
        public int MaxTotalFollows { get; init; }
        public bool IsActive { get; init; }
        public int TotalEnqueued { get; init; }
        public int TotalFollowed { get; init; }
        public int TotalFailed { get; init; }
        public int TotalSkipped { get; init; }
        public int Pending { get; init; }
        public int FollowedToday { get; init; }
        public DateTimeOffset? LastScrapeAt { get; init; }
        public DateTimeOffset? LastFollowAt { get; init; }
        public DateTimeOffset CreatedAt { get; init; }
    }

    public record CampaignDetailDto : CampaignDto
    {
        public int ScrapeBatchSize { get; init; }
        public int MinQueuedThreshold { get; init; }
        public List<ActionDto> RecentActions { get; init; } = new();
    }

    public record ActionDto
    {
        public Guid Id { get; init; }
        public string TargetHandle { get; init; } = string.Empty;
        public string Status { get; init; } = string.Empty;
        public DateTimeOffset EnqueuedAt { get; init; }
        public DateTimeOffset? ExecutedAt { get; init; }
        public int Attempts { get; init; }
        public string? ErrorMessage { get; init; }
    }

    public record CreateCampaignRequest
    {
        public Guid InstagramAccountId { get; init; }
        public string SourceHandle { get; init; } = string.Empty;
        /// <summary>"Followers" (default) o "Following".</summary>
        public string? SourceMode { get; init; }
        public int DailyRate { get; init; } = 30;
        /// <summary>Ventana horaria local AR (null = default 10-22 del entity).</summary>
        public int? ActiveHourStart { get; init; } = 10;
        public int? ActiveHourEnd { get; init; } = 22;
        public int MaxTotalFollows { get; init; }
        public int ScrapeBatchSize { get; init; } = 100;
        public int MinQueuedThreshold { get; init; } = 20;
    }

    public record UpdateCampaignRequest
    {
        public bool? IsActive { get; init; }
        public string? SourceHandle { get; init; }
        public string? SourceMode { get; init; }
        public int? DailyRate { get; init; }
        /// <summary>true para aplicar ActiveHourStart/End (permite setear null = 24h).</summary>
        public bool SetActiveHours { get; init; }
        public int? ActiveHourStart { get; init; }
        public int? ActiveHourEnd { get; init; }
        public int? MaxTotalFollows { get; init; }
        public int? ScrapeBatchSize { get; init; }
        public int? MinQueuedThreshold { get; init; }
    }
}
