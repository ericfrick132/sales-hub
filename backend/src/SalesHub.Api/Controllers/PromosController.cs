using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SalesHub.Core.Domain.Entities.Promos;
using SalesHub.Infrastructure.Persistence;
using SalesHub.Infrastructure.Services.Promos;

namespace SalesHub.Api.Controllers;

/// <summary>
/// Marketing → Promos: el calendario de fechas (curable), las campañas por app y los links
/// fijos de bio. El motor (PromoWorker) hace todo solo; acá se cura y se puede forzar.
/// </summary>
[ApiController]
[Route("api/promos")]
[Authorize(Roles = "Admin")]
public class PromosController : ControllerBase
{
    private readonly ApplicationDbContext _db;
    private readonly PromoEngine _engine;

    public PromosController(ApplicationDbContext db, PromoEngine engine)
    {
        _db = db; _engine = engine;
    }

    [HttpGet]
    public async Task<IActionResult> Overview(CancellationToken ct)
    {
        await PromoCalendarSeed.EnsureSeededAsync(_db, ct);
        var bases = _engine.AppBases();
        var events = await _db.PromoEvents.AsNoTracking().Include(e => e.Campaigns)
            .OrderBy(e => e.Date).ToListAsync(ct);

        // Link fijo de bio por app: sale de la última landing que devolvió la app.
        var landings = events.SelectMany(e => e.Campaigns).Where(c => c.LandingUrl != null)
            .GroupBy(c => c.ProductKey).ToDictionary(g => g.Key, g => g.OrderByDescending(c => c.UpdatedAt).First().LandingUrl!);

        return Ok(new
        {
            today = PromoEngine.TodayAr(),
            defaults = new { discountPercent = _engine.DefaultDiscount, durationMonths = _engine.DefaultMonths },
            apps = bases.Keys.OrderBy(k => k).Select(k => new
            {
                productKey = k,
                landingUrl = landings.GetValueOrDefault(k),
                bioUrl = landings.TryGetValue(k, out var l) ? PromoEngine.BioUrl(l) : null,
            }),
            events = events.Select(e => new
            {
                e.Id, e.Slug, e.Title, e.Description, e.Date, e.EndDate, e.Apps, e.DiscountPercent, e.DurationMonths,
                e.CouponCode, effectiveCode = PromoEngine.CodeFor(e, _engine.DefaultDiscount),
                e.LeadDays, e.Enabled, e.Confidence, e.Source, e.Note, e.ImageSourceUrl, e.ImageCredit,
                imageUrl = e.ImageAssetId is { } id ? _engine.AssetUrl(id) : e.ImageSourceUrl,
                launchOn = PromoEngine.WindowStart(e),
                targetApps = _engine.AppsFor(e, bases),
                campaigns = e.Campaigns.OrderBy(c => c.ProductKey).Select(c => new
                {
                    c.Id, c.ProductKey, status = c.Status.ToString(), c.CouponCode, c.DiscountPercent, c.DurationMonths,
                    c.Headline, c.Subheadline, c.Bullets, c.LandingUrl, c.TrackedUrl, c.StartsAt, c.EndsAt,
                    c.Error, c.Attempts, c.LaunchedAt, c.EndedAt,
                }),
            }),
        });
    }

    public record EventDto(DateOnly Date, DateOnly? EndDate, string Title, string? Slug, string? Description, List<string>? Apps,
        int? DiscountPercent, int? DurationMonths, string? CouponCode, int? LeadDays, bool? Enabled, string? Confidence,
        string? Source, string? Note, string? ImageSourceUrl, string? ImageCredit);

    [HttpPost("events")]
    public async Task<IActionResult> Create([FromBody] EventDto dto, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(dto.Title)) return BadRequest(new { error = "Falta el título" });
        var slug = string.IsNullOrWhiteSpace(dto.Slug) ? Slugify($"{dto.Title}-{dto.Date.Year}") : Slugify(dto.Slug);
        if (await _db.PromoEvents.AnyAsync(e => e.Slug == slug, ct)) return Conflict(new { error = $"Ya existe {slug}" });
        var ev = new PromoEvent { Id = Guid.NewGuid(), Slug = slug };
        Apply(ev, dto);
        _db.PromoEvents.Add(ev);
        await _db.SaveChangesAsync(ct);
        return Ok(new { ev.Id, ev.Slug });
    }

    [HttpPut("events/{id:guid}")]
    public async Task<IActionResult> Update(Guid id, [FromBody] EventDto dto, CancellationToken ct)
    {
        var ev = await _db.PromoEvents.FirstOrDefaultAsync(e => e.Id == id, ct);
        if (ev == null) return NotFound();
        Apply(ev, dto);
        await _db.SaveChangesAsync(ct);
        return Ok(new { ev.Id, ev.Slug });
    }

    [HttpDelete("events/{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        var ev = await _db.PromoEvents.Include(e => e.Campaigns).FirstOrDefaultAsync(e => e.Id == id, ct);
        if (ev == null) return NotFound();
        foreach (var c in ev.Campaigns.Where(c => c.Status == PromoCampaignStatus.Live))
            await _engine.EndAsync(c, null, ct);
        _db.PromoEvents.Remove(ev);
        await _db.SaveChangesAsync(ct);
        return Ok(new { ok = true });
    }

    /// <summary>Lanza YA (sin esperar la ventana) en una app o en todas las del evento.</summary>
    [HttpPost("events/{id:guid}/launch")]
    public async Task<IActionResult> Launch(Guid id, [FromQuery] string? productKey, CancellationToken ct)
    {
        var ev = await _db.PromoEvents.FirstOrDefaultAsync(e => e.Id == id, ct);
        if (ev == null) return NotFound();
        var bases = _engine.AppBases();
        var apps = string.IsNullOrWhiteSpace(productKey) ? _engine.AppsFor(ev, bases) : new[] { productKey };
        var res = new List<object>();
        foreach (var app in apps)
        {
            var c = await _engine.LaunchAsync(ev, app, bases, ct);
            res.Add(new { c.ProductKey, status = c.Status.ToString(), c.TrackedUrl, c.Error });
        }
        return Ok(res);
    }

    /// <summary>Re-genera el copy (Claude) y re-empuja a la app.</summary>
    [HttpPost("campaigns/{id:guid}/regenerate")]
    public async Task<IActionResult> Regenerate(Guid id, CancellationToken ct)
    {
        var c = await _db.PromoCampaigns.Include(x => x.Event).FirstOrDefaultAsync(x => x.Id == id, ct);
        if (c?.Event == null) return NotFound();
        c.Headline = "";
        await _db.SaveChangesAsync(ct);
        var r = await _engine.LaunchAsync(c.Event, c.ProductKey, null, ct);
        return Ok(new { status = r.Status.ToString(), r.Headline, r.Subheadline, r.Bullets, r.TrackedUrl, r.Error });
    }

    [HttpPost("campaigns/{id:guid}/end")]
    public async Task<IActionResult> End(Guid id, CancellationToken ct)
    {
        var c = await _db.PromoCampaigns.Include(x => x.Event).FirstOrDefaultAsync(x => x.Id == id, ct);
        if (c == null) return NotFound();
        var ok = await _engine.EndAsync(c, null, ct);
        return Ok(new { ok, c.Error });
    }

    /// <summary>Corre el motor ahora (lo mismo que hace el worker cada 30 min).</summary>
    [HttpPost("run")]
    public async Task<IActionResult> Run(CancellationToken ct) => Ok(await _engine.TickAsync(ct));

    /// <summary>Importa un JSON de calendario (mismo formato que docs/promo-calendar). overwrite=true pisa por slug.</summary>
    [HttpPost("import")]
    public async Task<IActionResult> Import([FromBody] List<PromoCalendarSeed.Entry>? entries, [FromQuery] bool overwrite, CancellationToken ct)
    {
        var n = await PromoCalendarSeed.ImportAsync(_db, entries is { Count: > 0 } ? entries : PromoCalendarSeed.Embedded(), overwrite, ct);
        return Ok(new { imported = n });
    }

    private static void Apply(PromoEvent ev, EventDto dto)
    {
        ev.Date = dto.Date;
        ev.EndDate = dto.EndDate is { } e && e >= dto.Date ? e : dto.Date;
        ev.Title = dto.Title.Trim();
        ev.Description = dto.Description?.Trim() ?? "";
        ev.Apps = dto.Apps?.Where(a => !string.IsNullOrWhiteSpace(a)).Select(a => a.Trim()).Distinct().ToList() ?? new();
        ev.DiscountPercent = dto.DiscountPercent is > 0 and <= 100 ? dto.DiscountPercent : null;
        ev.DurationMonths = dto.DurationMonths is > 0 and <= 24 ? dto.DurationMonths : null;
        ev.CouponCode = string.IsNullOrWhiteSpace(dto.CouponCode) ? null : new string(dto.CouponCode.Trim().ToUpperInvariant().Where(char.IsLetterOrDigit).ToArray());
        ev.LeadDays = dto.LeadDays is >= 0 and <= 60 ? dto.LeadDays.Value : ev.LeadDays;
        ev.Enabled = dto.Enabled ?? ev.Enabled;
        ev.Confidence = string.IsNullOrWhiteSpace(dto.Confidence) ? ev.Confidence : dto.Confidence;
        ev.Source = dto.Source;
        ev.Note = dto.Note;
        if (!string.Equals(ev.ImageSourceUrl, dto.ImageSourceUrl, StringComparison.Ordinal)) ev.ImageAssetId = null;
        ev.ImageSourceUrl = string.IsNullOrWhiteSpace(dto.ImageSourceUrl) ? null : dto.ImageSourceUrl.Trim();
        ev.ImageCredit = dto.ImageCredit;
        ev.UpdatedAt = DateTimeOffset.UtcNow;
    }

    private static string Slugify(string s)
    {
        var norm = s.Normalize(System.Text.NormalizationForm.FormD);
        var chars = norm.Where(c => System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.NonSpacingMark)
            .Select(c => char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : '-').ToArray();
        return System.Text.RegularExpressions.Regex.Replace(new string(chars), "-+", "-").Trim('-');
    }
}
