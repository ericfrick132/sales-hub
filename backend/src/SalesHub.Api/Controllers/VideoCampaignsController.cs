using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SalesHub.Core.Abstractions;
using SalesHub.Core.Domain.Entities;
using SalesHub.Infrastructure.Persistence;
using SalesHub.Infrastructure.Services;

namespace SalesHub.Api.Controllers;

/// <summary>Campañas de remarketing con video (Marketing → Campaña con video). Solo admin.</summary>
[ApiController]
[Route("api/video-campaigns")]
[Authorize]
public class VideoCampaignsController : ControllerBase
{
    private readonly ApplicationDbContext _db;
    private readonly VideoCampaignService _svc;
    private readonly IPhoneNormalizer _phones;

    public VideoCampaignsController(ApplicationDbContext db, VideoCampaignService svc, IPhoneNormalizer phones)
    {
        _db = db; _svc = svc; _phones = phones;
    }

    public record CampaignDto(Guid Id, string Name, string ProductKey, string InstanceName, string MessageBefore,
        Guid MediaAssetId, string? VideoName, string MessageAfter, int PerDay, int WindowFromHour, int WindowToHour,
        List<int> Weekdays, string Status, DateTimeOffset CreatedAt, DateTimeOffset? StartedAt,
        int Total, int Pending, int Sent, int Failed, int Skipped, int Replied);

    public record RecipientDto(Guid LeadId, string Name, string? Phone, string Status, DateTimeOffset? SentAt,
        DateTimeOffset? RepliedAt, string? Error);

    public record SaveRequest(string Name, string ProductKey, string InstanceName, string MessageBefore,
        Guid MediaAssetId, string? MessageAfter, int PerDay, int WindowFromHour, int WindowToHour,
        List<int>? Weekdays, List<Guid> LeadIds);

    public record TestRequest(string Phone, string? SampleName);

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        if (!CurrentUser.IsAdmin(User)) return Forbid();
        var rows = await _db.VideoCampaigns.AsNoTracking().OrderByDescending(c => c.CreatedAt)
            .Select(c => new CampaignDto(c.Id, c.Name, c.ProductKey, c.InstanceName, c.MessageBefore,
                c.MediaAssetId, c.MediaAsset != null ? c.MediaAsset.FileName : null, c.MessageAfter, c.PerDay,
                c.WindowFromHour, c.WindowToHour, c.Weekdays, c.Status.ToString(), c.CreatedAt, c.StartedAt,
                c.Recipients.Count,
                c.Recipients.Count(r => r.Status == VideoCampaignRecipientStatus.Pending),
                c.Recipients.Count(r => r.Status == VideoCampaignRecipientStatus.Sent),
                c.Recipients.Count(r => r.Status == VideoCampaignRecipientStatus.Failed),
                c.Recipients.Count(r => r.Status == VideoCampaignRecipientStatus.Skipped),
                c.Recipients.Count(r => r.RepliedAt != null)))
            .ToListAsync(ct);
        return Ok(rows);
    }

    [HttpGet("{id:guid}/recipients")]
    public async Task<IActionResult> Recipients(Guid id, CancellationToken ct)
    {
        if (!CurrentUser.IsAdmin(User)) return Forbid();
        var rows = await _db.VideoCampaignRecipients.AsNoTracking().Where(r => r.CampaignId == id)
            .OrderByDescending(r => r.RepliedAt != null).ThenByDescending(r => r.SentAt).ThenBy(r => r.Lead!.Name)
            .Select(r => new RecipientDto(r.LeadId, r.Lead!.Name, r.Lead.WhatsappPhone, r.Status.ToString(),
                r.SentAt, r.RepliedAt, r.Error))
            .ToListAsync(ct);
        return Ok(rows);
    }

    /// <summary>Candidatos: escribieron ellos primero y después silencio (ver VideoCampaignService.AudienceAsync).</summary>
    [HttpGet("audience")]
    public async Task<IActionResult> Audience([FromQuery] string productKey, [FromQuery] int minIdleDays = 7, CancellationToken ct = default)
    {
        if (!CurrentUser.IsAdmin(User)) return Forbid();
        if (string.IsNullOrWhiteSpace(productKey)) return BadRequest(new { error = "Falta la app" });
        return Ok(await _svc.AudienceAsync(productKey, Math.Max(0, minIdleDays), ct));
    }

    /// <summary>Crea la campaña en borrador con los leads elegidos.</summary>
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] SaveRequest req, CancellationToken ct)
    {
        if (!CurrentUser.IsAdmin(User)) return Forbid();
        if (await Validate(req, ct) is { } error) return BadRequest(new { error });
        var c = new VideoCampaign
        {
            Id = Guid.NewGuid(),
            Name = req.Name.Trim(),
            ProductKey = req.ProductKey,
            InstanceName = req.InstanceName,
            MessageBefore = req.MessageBefore.Trim(),
            MediaAssetId = req.MediaAssetId,
            MessageAfter = (req.MessageAfter ?? "").Trim(),
            PerDay = req.PerDay,
            WindowFromHour = req.WindowFromHour,
            WindowToHour = req.WindowToHour,
            Weekdays = req.Weekdays is { Count: > 0 } w ? w.Distinct().Where(d => d is >= 1 and <= 7).ToList() : new() { 1, 2, 3, 4 },
        };
        // Solo leads que no recibieron ninguna campaña con video.
        var already = await _db.VideoCampaignRecipients.Where(r => req.LeadIds.Contains(r.LeadId)).Select(r => r.LeadId).ToListAsync(ct);
        var leadIds = req.LeadIds.Distinct().Except(already).ToList();
        if (leadIds.Count == 0) return BadRequest(new { error = "Ninguno de los leads elegidos esta disponible" });
        c.Recipients = leadIds.Select(id => new VideoCampaignRecipient { Id = Guid.NewGuid(), CampaignId = c.Id, LeadId = id }).ToList();
        _db.VideoCampaigns.Add(c);
        await _db.SaveChangesAsync(ct);
        return Ok(new { id = c.Id, recipients = leadIds.Count, skippedAlreadyInCampaign = already.Count });
    }

    public record PreviewRequest(string MessageBefore, string? MessageAfter, List<string?>? Names);

    /// <summary>Cómo queda el mensaje para algunos leads (variantes + nombre), sin mandar nada.</summary>
    [HttpPost("preview")]
    public IActionResult Preview([FromBody] PreviewRequest req)
    {
        if (!CurrentUser.IsAdmin(User)) return Forbid();
        var names = req.Names is { Count: > 0 } n ? n.Take(5).ToList() : new List<string?> { null };
        return Ok(names.Select(name =>
        {
            var rnd = new Random();
            return new
            {
                name,
                before = VideoCampaignService.Bubbles(VideoCampaignService.Render(req.MessageBefore ?? "", name, rnd)),
                after = VideoCampaignService.Bubbles(VideoCampaignService.Render(req.MessageAfter ?? "", name, rnd)),
            };
        }));
    }

    /// <summary>Manda la campaña a un número para verla como la va a ver el lead.</summary>
    [HttpPost("{id:guid}/test")]
    public async Task<IActionResult> Test(Guid id, [FromBody] TestRequest req, CancellationToken ct)
    {
        if (!CurrentUser.IsAdmin(User)) return Forbid();
        var c = await _db.VideoCampaigns.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (c is null) return NotFound();
        var phone = _phones.NormalizeArStrict(req.Phone);
        if (phone is null) return BadRequest(new { error = "Numero invalido" });
        if (await _svc.LineProblemAsync(c.InstanceName, ct) is { } problem) return BadRequest(new { error = problem });
        var ok = await _svc.SendTestAsync(c, phone, req.SampleName, ct);
        return ok ? Ok(new { sent = true }) : StatusCode(502, new { error = "WhatsApp no acepto el envio" });
    }

    [HttpPost("{id:guid}/activate")]
    public async Task<IActionResult> Activate(Guid id, CancellationToken ct) => await SetStatus(id, VideoCampaignStatus.Active, ct);

    [HttpPost("{id:guid}/pause")]
    public async Task<IActionResult> Pause(Guid id, CancellationToken ct) => await SetStatus(id, VideoCampaignStatus.Paused, ct);

    /// <summary>Borra una campaña que todavía no mandó nada.</summary>
    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        if (!CurrentUser.IsAdmin(User)) return Forbid();
        var c = await _db.VideoCampaigns.Include(x => x.Recipients).FirstOrDefaultAsync(x => x.Id == id, ct);
        if (c is null) return NotFound();
        if (c.Recipients.Any(r => r.Status != VideoCampaignRecipientStatus.Pending))
            return BadRequest(new { error = "Ya mando mensajes: pausala en vez de borrarla" });
        _db.VideoCampaigns.Remove(c);
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }

    private async Task<IActionResult> SetStatus(Guid id, VideoCampaignStatus status, CancellationToken ct)
    {
        if (!CurrentUser.IsAdmin(User)) return Forbid();
        var c = await _db.VideoCampaigns.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (c is null) return NotFound();
        if (c.Status == VideoCampaignStatus.Done) return BadRequest(new { error = "La campaña ya termino" });
        if (status == VideoCampaignStatus.Active && await _svc.LineProblemAsync(c.InstanceName, ct) is { } problem)
            return BadRequest(new { error = problem });
        c.Status = status;
        if (status == VideoCampaignStatus.Active) c.StartedAt ??= DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);
        return Ok(new { status = c.Status.ToString() });
    }

    private async Task<string?> Validate(SaveRequest r, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(r.Name)) return "Ponele un nombre";
        if (string.IsNullOrWhiteSpace(r.MessageBefore)) return "Escribi el mensaje";
        if (r.PerDay is < 1 or > 40) return "El tope por dia va de 1 a 40";
        if (r.WindowFromHour is < 7 or > 21 || r.WindowToHour <= r.WindowFromHour || r.WindowToHour > 22) return "Franja horaria invalida (entre 7 y 22 h)";
        if (r.LeadIds is not { Count: > 0 }) return "Elegi al menos un lead";
        if (await _svc.LineProblemAsync(r.InstanceName, ct) is { } problem) return problem;
        var asset = await _db.MediaAssets.AsNoTracking().Where(m => m.Id == r.MediaAssetId)
            .Select(m => new { m.MimeType, m.SizeBytes }).FirstOrDefaultAsync(ct);
        if (asset is null) return "Elegi el video";
        if (!asset.MimeType.StartsWith("video/") && !asset.MimeType.StartsWith("image/")) return "El adjunto tiene que ser un video o una imagen";
        if (asset.SizeBytes > 16 * 1024 * 1024) return "El video pasa los 16 MB que acepta WhatsApp";
        return null;
    }
}
