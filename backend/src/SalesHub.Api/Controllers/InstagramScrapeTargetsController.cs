using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SalesHub.Infrastructure.Instagram;
using SalesHub.Infrastructure.Persistence;

namespace SalesHub.Api.Controllers;

[ApiController]
[Route("api/instagram-scrape-targets")]
[Authorize(Roles = "Admin")]
public class InstagramScrapeTargetsController : ControllerBase
{
    private readonly ApplicationDbContext _db;
    private readonly InstagramLeadScraper _scraper;
    private readonly ILogger<InstagramScrapeTargetsController> _log;

    public InstagramScrapeTargetsController(
        ApplicationDbContext db,
        InstagramLeadScraper scraper,
        ILogger<InstagramScrapeTargetsController> log)
    {
        _db = db;
        _scraper = scraper;
        _log = log;
    }

    [HttpGet]
    public async Task<IActionResult> GetAll()
    {
        var targets = await _db.InstagramScrapeTargets
            .Include(t => t.Product)
            .Include(t => t.AssignedAccount)
            .OrderBy(t => t.ProductKey)
            .ThenBy(t => t.TargetType)
            .Select(t => new InstagramScrapeTargetDto
            {
                Id = t.Id,
                ProductKey = t.ProductKey,
                ProductName = t.Product != null ? t.Product.DisplayName : null,
                TargetType = t.TargetType,
                TargetValue = t.TargetValue,
                DisplayName = t.DisplayName,
                IsActive = t.IsActive,
                MaxPerRun = t.MaxPerRun,
                AssignedAccountId = t.AssignedAccountId,
                AssignedAccountUsername = t.AssignedAccount != null ? t.AssignedAccount.Username : null,
                LastScrapedAt = t.LastScrapedAt,
                TotalLeadsCreated = t.TotalLeadsCreated,
                CreatedAt = t.CreatedAt
            })
            .ToListAsync();

        return Ok(targets);
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateScrapeTargetRequest req)
    {
        var validTypes = new[] { "competitor_followers", "hashtag_commenters" };
        if (!validTypes.Contains(req.TargetType))
            return BadRequest(new { error = $"Tipo inválido. Válidos: {string.Join(", ", validTypes)}" });

        var product = await _db.Products.FirstOrDefaultAsync(p => p.ProductKey == req.ProductKey);
        if (product is null)
            return BadRequest(new { error = $"Producto '{req.ProductKey}' no existe" });

        var target = new Core.Domain.Entities.InstagramScrapeTarget
        {
            Id = Guid.NewGuid(),
            ProductKey = req.ProductKey,
            TargetType = req.TargetType,
            TargetValue = req.TargetValue,
            DisplayName = req.DisplayName,
            IsActive = true,
            MaxPerRun = req.MaxPerRun,
            AssignedAccountId = req.AssignedAccountId,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };

        _db.InstagramScrapeTargets.Add(target);
        await _db.SaveChangesAsync();

        _log.LogInformation("Target de Instagram creado: {Type}:{Value} para {Product}",
            req.TargetType, req.TargetValue, req.ProductKey);

        return Ok(new { id = target.Id });
    }

    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdateScrapeTargetRequest req)
    {
        var target = await _db.InstagramScrapeTargets.FirstOrDefaultAsync(t => t.Id == id);
        if (target is null) return NotFound();

        if (req.DisplayName is not null) target.DisplayName = req.DisplayName;
        if (req.IsActive.HasValue) target.IsActive = req.IsActive.Value;
        if (req.MaxPerRun.HasValue) target.MaxPerRun = req.MaxPerRun.Value;
        if (req.AssignedAccountId is not null) target.AssignedAccountId = req.AssignedAccountId;

        target.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync();

        return Ok(new { id = target.Id });
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id)
    {
        var target = await _db.InstagramScrapeTargets.FirstOrDefaultAsync(t => t.Id == id);
        if (target is null) return NotFound();

        _db.InstagramScrapeTargets.Remove(target);
        await _db.SaveChangesAsync();

        return NoContent();
    }

    [HttpPost("{id:guid}/run")]
    public async Task<IActionResult> RunNow(Guid id)
    {
        var target = await _db.InstagramScrapeTargets
            .Include(t => t.AssignedAccount)
            .FirstOrDefaultAsync(t => t.Id == id);

        if (target is null) return NotFound();

        try
        {
            var created = await _scraper.RunTargetAsync(target);
            return Ok(new { leadsCreated = created });
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Error al ejecutar target {Id}", id);
            return BadRequest(new { error = ex.Message });
        }
    }

    // DTOs
    public record InstagramScrapeTargetDto
    {
        public Guid Id { get; init; }
        public string ProductKey { get; init; } = string.Empty;
        public string? ProductName { get; init; }
        public string TargetType { get; init; } = string.Empty;
        public string TargetValue { get; init; } = string.Empty;
        public string? DisplayName { get; init; }
        public bool IsActive { get; init; }
        public int MaxPerRun { get; init; }
        public Guid? AssignedAccountId { get; init; }
        public string? AssignedAccountUsername { get; init; }
        public DateTimeOffset? LastScrapedAt { get; init; }
        public int TotalLeadsCreated { get; init; }
        public DateTimeOffset CreatedAt { get; init; }
    }

    public record CreateScrapeTargetRequest
    {
        public string ProductKey { get; init; } = string.Empty;
        public string TargetType { get; init; } = string.Empty;
        public string TargetValue { get; init; } = string.Empty;
        public string? DisplayName { get; init; }
        public int MaxPerRun { get; init; } = 50;
        public Guid? AssignedAccountId { get; init; }
    }

    public record UpdateScrapeTargetRequest
    {
        public string? DisplayName { get; init; }
        public bool? IsActive { get; init; }
        public int? MaxPerRun { get; init; }
        public Guid? AssignedAccountId { get; init; }
    }
}
