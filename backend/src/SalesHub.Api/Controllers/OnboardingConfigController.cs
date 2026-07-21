using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SalesHub.Core.Domain.Entities;
using SalesHub.Infrastructure.Persistence;

namespace SalesHub.Api.Controllers;

/// <summary>
/// CRUD de la config de onboarding de ads POR APP. Cada producto define si tiene onboarding
/// (Enabled), su intro, sus preguntas, su endpoint de provisión y su mensaje de éxito. El motor
/// (OnboardingService) es genérico y lee esto.
/// </summary>
[ApiController]
[Route("api/onboarding-configs")]
[Authorize(Roles = "Admin")]
public class OnboardingConfigController : ControllerBase
{
    private readonly ApplicationDbContext _db;
    public OnboardingConfigController(ApplicationDbContext db) { _db = db; }

    public record ConfigDto(string ProductKey, string DisplayName, bool Enabled, bool SelfServe, string Intro,
        List<string> Questions, string EmailPrompt, string ProvisionUrl, string ProvisionNameField,
        string SuccessMessage, string ClosingMessage, bool UsePitchAudio,
        int ReplyDelayMinSec, int ReplyDelayMaxSec, int AudioCount,
        // Reenganche (ver OnboardingConfig): nullable para que un frontend viejo que no manda
        // estos campos NO los pise con vacío en el Upsert.
        string? ReengageIntro = null, List<string>? ReengageQuestions = null,
        List<Guid>? ReengageMediaAssetIds = null, List<string>? ReengageMediaCaptions = null);

    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken ct)
    {
        var products = await _db.Products.AsNoTracking()
            .Where(p => p.Active && p.ProductKey != "")
            .OrderBy(p => p.DisplayName).ToListAsync(ct);
        var configs = await _db.OnboardingConfigs.AsNoTracking().ToDictionaryAsync(c => c.ProductKey, ct);
        var audioCounts = (await _db.OnboardingAudios.AsNoTracking()
                .GroupBy(a => a.ProductKey).Select(g => new { g.Key, C = g.Count() }).ToListAsync(ct))
            .ToDictionary(x => x.Key, x => x.C);

        var result = products.Select(p =>
        {
            configs.TryGetValue(p.ProductKey, out var c);
            audioCounts.TryGetValue(p.ProductKey, out var ac);
            return new ConfigDto(p.ProductKey, p.DisplayName, c?.Enabled ?? false, c?.SelfServe ?? true, c?.Intro ?? "",
                c?.Questions ?? new(), c?.EmailPrompt ?? "", c?.ProvisionUrl ?? "",
                c?.ProvisionNameField ?? "name", c?.SuccessMessage ?? "", c?.ClosingMessage ?? "",
                c?.UsePitchAudio ?? false, c?.ReplyDelayMinSec ?? 0, c?.ReplyDelayMaxSec ?? 0, ac,
                c?.ReengageIntro ?? "", c?.ReengageQuestions ?? new(), c?.ReengageMediaAssetIds ?? new(),
                c?.ReengageMediaCaptions ?? new());
        });
        return Ok(result);
    }

    [HttpPut("{productKey}")]
    public async Task<IActionResult> Upsert(string productKey, [FromBody] ConfigDto dto, CancellationToken ct)
    {
        var product = await _db.Products.FirstOrDefaultAsync(p => p.ProductKey == productKey, ct);
        if (product is null) return NotFound(new { error = "Producto desconocido" });

        var c = await _db.OnboardingConfigs.FirstOrDefaultAsync(x => x.ProductKey == productKey, ct);
        if (c is null) { c = new OnboardingConfig { ProductKey = productKey }; _db.OnboardingConfigs.Add(c); }

        c.Enabled = dto.Enabled;
        c.SelfServe = dto.SelfServe;
        c.UsePitchAudio = dto.UsePitchAudio;
        c.ReplyDelayMinSec = Math.Max(0, dto.ReplyDelayMinSec);
        c.ReplyDelayMaxSec = Math.Max(0, dto.ReplyDelayMaxSec);
        c.ClosingMessage = dto.ClosingMessage ?? "";
        c.Intro = dto.Intro ?? "";
        c.Questions = (dto.Questions ?? new()).Where(q => !string.IsNullOrWhiteSpace(q)).Select(q => q.Trim()).ToList();
        c.EmailPrompt = dto.EmailPrompt ?? "";
        c.ProvisionUrl = dto.ProvisionUrl ?? "";
        c.ProvisionNameField = string.IsNullOrWhiteSpace(dto.ProvisionNameField) ? "name" : dto.ProvisionNameField.Trim();
        c.SuccessMessage = dto.SuccessMessage ?? "";
        // Reenganche: solo se actualizan si vienen en el body (frontend viejo no los manda → no pisa).
        if (dto.ReengageIntro is not null) c.ReengageIntro = dto.ReengageIntro;
        if (dto.ReengageQuestions is not null)
            c.ReengageQuestions = dto.ReengageQuestions.Where(q => !string.IsNullOrWhiteSpace(q)).Select(q => q.Trim()).ToList();
        if (dto.ReengageMediaAssetIds is not null)
            c.ReengageMediaAssetIds = dto.ReengageMediaAssetIds.Where(g => g != Guid.Empty).ToList();
        if (dto.ReengageMediaCaptions is not null)
            c.ReengageMediaCaptions = dto.ReengageMediaCaptions.Select(x => (x ?? "").Trim()).ToList();
        c.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);
        return Ok(new { ok = true });
    }
}
