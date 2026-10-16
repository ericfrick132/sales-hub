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
        List<Guid>? ReengageMediaAssetIds = null, List<string>? ReengageMediaCaptions = null,
        string? PostSignupCheckin = null, string? TrialDiscountNudge = null, string? FirstStepsMessage = null,
        // Pre-calificación con pase a una persona (Mateo). HandoffEnabled null = no tocar (frontend viejo).
        bool? HandoffEnabled = null, Guid? HandoffSellerId = null, int? HandoffAfterQuestions = null,
        string? PresentAs = null, string? HandoffMessage = null, string? DemoEventTypeUri = null, string? DemoPitch = null,
        // Por anuncio (ver MetaAd): 1ª pregunta con el rubro ya sabido y pitch por ángulo. null = no tocar.
        string? KnownBusinessQuestion = null, List<AnglePitch>? AnglePitches = null,
        // Saludo con lo que dijo el anuncio y paso de precios (ver OnboardingConfig). null = no tocar.
        string? KnownBusinessGreeting = null, string? PricesMessage = null, List<Guid>? PricesMediaAssetIds = null,
        string? StartQuestion = null, string? DemoOffer = null, string? DemoLink = null,
        List<string>? FollowUpMessages = null);

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
                c?.ReengageMediaCaptions ?? new(), c?.PostSignupCheckin ?? "", c?.TrialDiscountNudge ?? "",
                c?.FirstStepsMessage ?? "",
                c?.HandoffSellerId is not null, c?.HandoffSellerId, c?.HandoffAfterQuestions ?? 2,
                c?.PresentAs ?? "", c?.HandoffMessage ?? "", c?.DemoEventTypeUri ?? "", c?.DemoPitch ?? "",
                c?.KnownBusinessQuestion ?? "", c?.AnglePitches ?? new(),
                c?.KnownBusinessGreeting ?? "", c?.PricesMessage ?? "", c?.PricesMediaAssetIds ?? new(), c?.StartQuestion ?? "",
                c?.DemoOffer ?? "", c?.DemoLink ?? "", c?.FollowUpMessages ?? new());
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
        if (dto.PostSignupCheckin is not null) c.PostSignupCheckin = dto.PostSignupCheckin;
        if (dto.TrialDiscountNudge is not null) c.TrialDiscountNudge = dto.TrialDiscountNudge;
        if (dto.FirstStepsMessage is not null) c.FirstStepsMessage = dto.FirstStepsMessage;
        if (dto.HandoffEnabled is not null)
        {
            if (dto.HandoffEnabled == true)
            {
                if (dto.HandoffSellerId is null || !await _db.Sellers.AnyAsync(s => s.Id == dto.HandoffSellerId && s.IsActive, ct))
                    return BadRequest(new { error = "Elegí a quién se le pasa el lead (un vendedor activo)." });
                c.HandoffSellerId = dto.HandoffSellerId;
            }
            else c.HandoffSellerId = null;
        }
        if (dto.HandoffAfterQuestions is not null) c.HandoffAfterQuestions = Math.Clamp(dto.HandoffAfterQuestions.Value, 1, 10);
        if (dto.PresentAs is not null) c.PresentAs = string.IsNullOrWhiteSpace(dto.PresentAs) ? null : dto.PresentAs.Trim().ToLowerInvariant();
        if (dto.HandoffMessage is not null) c.HandoffMessage = dto.HandoffMessage.Trim();
        if (dto.DemoPitch is not null) c.DemoPitch = dto.DemoPitch.Trim();
        if (dto.KnownBusinessQuestion is not null) c.KnownBusinessQuestion = dto.KnownBusinessQuestion.Trim();
        if (dto.KnownBusinessGreeting is not null) c.KnownBusinessGreeting = dto.KnownBusinessGreeting.Trim();
        if (dto.PricesMessage is not null) c.PricesMessage = dto.PricesMessage.Trim();
        if (dto.PricesMediaAssetIds is not null) c.PricesMediaAssetIds = dto.PricesMediaAssetIds.Where(g => g != Guid.Empty).ToList();
        if (dto.StartQuestion is not null) c.StartQuestion = dto.StartQuestion.Trim();
        if (dto.FollowUpMessages is not null)
            c.FollowUpMessages = dto.FollowUpMessages.Where(m => !string.IsNullOrWhiteSpace(m)).Select(m => m.Trim()).ToList();
        if (dto.DemoOffer is not null) c.DemoOffer = dto.DemoOffer.Trim();
        if (dto.DemoLink is not null) c.DemoLink = dto.DemoLink.Trim();
        if (dto.AnglePitches is not null)
            c.AnglePitches = dto.AnglePitches
                .Where(p => !string.IsNullOrWhiteSpace(p.Text))
                .Select(p => new AnglePitch
                {
                    Angle = (p.Angle ?? "").Trim().ToLowerInvariant(),
                    Text = p.Text.Trim(),
                    MediaAssetIds = (p.MediaAssetIds ?? new()).Where(g => g != Guid.Empty).ToList(),
                })
                .ToList();
        if (dto.DemoEventTypeUri is not null)
            c.DemoEventTypeUri = string.IsNullOrWhiteSpace(dto.DemoEventTypeUri) ? null : dto.DemoEventTypeUri.Trim();
        c.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);
        return Ok(new { ok = true });
    }
}
