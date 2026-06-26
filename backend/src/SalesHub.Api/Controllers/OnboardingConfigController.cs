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

    public record ConfigDto(string ProductKey, string DisplayName, bool Enabled, string Intro,
        List<string> Questions, string EmailPrompt, string ProvisionUrl, string ProvisionNameField, string SuccessMessage);

    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken ct)
    {
        var products = await _db.Products.AsNoTracking()
            .Where(p => p.Active && p.ProductKey != "")
            .OrderBy(p => p.DisplayName).ToListAsync(ct);
        var configs = await _db.OnboardingConfigs.AsNoTracking().ToDictionaryAsync(c => c.ProductKey, ct);

        var result = products.Select(p =>
        {
            configs.TryGetValue(p.ProductKey, out var c);
            return new ConfigDto(p.ProductKey, p.DisplayName, c?.Enabled ?? false, c?.Intro ?? "",
                c?.Questions ?? new(), c?.EmailPrompt ?? "", c?.ProvisionUrl ?? "",
                c?.ProvisionNameField ?? "name", c?.SuccessMessage ?? "");
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
        c.Intro = dto.Intro ?? "";
        c.Questions = (dto.Questions ?? new()).Where(q => !string.IsNullOrWhiteSpace(q)).Select(q => q.Trim()).ToList();
        c.EmailPrompt = dto.EmailPrompt ?? "";
        c.ProvisionUrl = dto.ProvisionUrl ?? "";
        c.ProvisionNameField = string.IsNullOrWhiteSpace(dto.ProvisionNameField) ? "name" : dto.ProvisionNameField.Trim();
        c.SuccessMessage = dto.SuccessMessage ?? "";
        c.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);
        return Ok(new { ok = true });
    }
}
