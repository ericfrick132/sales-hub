using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SalesHub.Core.Domain.Entities;
using SalesHub.Infrastructure.Persistence;

namespace SalesHub.Api.Controllers;

/// <summary>
/// Config del módulo de transcripción de audios (relay personal): on/off, a qué línea
/// (instancia de Evolution) escucha y la allowlist de números autorizados. El motor es
/// <see cref="SalesHub.Infrastructure.Services.AudioTranscriptionRelay"/>, enganchado en
/// el webhook de Evolution.
/// </summary>
[ApiController]
[Route("api/transcription")]
[Authorize]
public class TranscriptionController : ControllerBase
{
    private readonly ApplicationDbContext _db;
    private static readonly Regex NonDigit = new(@"\D", RegexOptions.Compiled);

    public TranscriptionController(ApplicationDbContext db) { _db = db; }

    public record PhoneDto(Guid Id, string Phone, string? Label);
    public record InstanceDto(string InstanceName, string? PhoneNumber, string? Label, string Status);
    public record ConfigDto(bool Enabled, string? InstanceName, List<InstanceDto> Instances, List<PhoneDto> Phones);
    public record SetEnabledRequest(bool Enabled);
    public record SetInstanceRequest(string? InstanceName);
    public record AddPhoneRequest(string Phone, string? Label);

    [HttpGet]
    public async Task<ActionResult<ConfigDto>> Get(CancellationToken ct)
    {
        if (!CurrentUser.IsAdmin(User)) return Forbid();
        var s = await _db.TranscriptionSettings.AsNoTracking().FirstOrDefaultAsync(ct);
        var instances = await _db.EvolutionInstances.AsNoTracking()
            .Include(i => i.Seller)
            .OrderBy(i => i.Seller!.DisplayName)
            .Select(i => new InstanceDto(i.InstanceName, i.ConnectedPhoneNumber, i.Seller!.DisplayName, i.Status.ToString()))
            .ToListAsync(ct);
        var phones = await _db.TranscriptionPhones.AsNoTracking()
            .OrderBy(p => p.CreatedAt)
            .Select(p => new PhoneDto(p.Id, p.Phone, p.Label))
            .ToListAsync(ct);
        return Ok(new ConfigDto(s?.Enabled ?? false, s?.InstanceName, instances, phones));
    }

    [HttpPost("enabled")]
    public async Task<IActionResult> SetEnabled([FromBody] SetEnabledRequest req, CancellationToken ct)
    {
        if (!CurrentUser.IsAdmin(User)) return Forbid();
        var s = await GetOrCreateAsync(ct);
        s.Enabled = req.Enabled;
        s.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);
        return Ok(new { enabled = s.Enabled });
    }

    [HttpPost("instance")]
    public async Task<IActionResult> SetInstance([FromBody] SetInstanceRequest req, CancellationToken ct)
    {
        if (!CurrentUser.IsAdmin(User)) return Forbid();
        var name = string.IsNullOrWhiteSpace(req.InstanceName) ? null : req.InstanceName.Trim();
        if (name is not null && !await _db.EvolutionInstances.AnyAsync(i => i.InstanceName == name, ct))
            return BadRequest(new { error = "Instancia desconocida." });

        var s = await GetOrCreateAsync(ct);
        s.InstanceName = name;
        s.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);
        return Ok(new { instanceName = s.InstanceName });
    }

    [HttpPost("phones")]
    public async Task<ActionResult<PhoneDto>> AddPhone([FromBody] AddPhoneRequest req, CancellationToken ct)
    {
        if (!CurrentUser.IsAdmin(User)) return Forbid();
        var digits = NonDigit.Replace(req.Phone ?? "", "");
        if (digits.Length < 6) return BadRequest(new { error = "Número inválido." });

        var exists = await _db.TranscriptionPhones.AnyAsync(p => p.Phone == digits, ct);
        if (exists) return Conflict(new { error = "Ese número ya está autorizado." });

        var label = string.IsNullOrWhiteSpace(req.Label) ? null : req.Label.Trim();
        var p = new TranscriptionPhone { Phone = digits, Label = label };
        _db.TranscriptionPhones.Add(p);
        await _db.SaveChangesAsync(ct);
        return Ok(new PhoneDto(p.Id, p.Phone, p.Label));
    }

    [HttpDelete("phones/{id:guid}")]
    public async Task<IActionResult> RemovePhone(Guid id, CancellationToken ct)
    {
        if (!CurrentUser.IsAdmin(User)) return Forbid();
        var p = await _db.TranscriptionPhones.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (p is null) return NotFound();
        _db.TranscriptionPhones.Remove(p);
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }

    private async Task<TranscriptionSettings> GetOrCreateAsync(CancellationToken ct)
    {
        var s = await _db.TranscriptionSettings.FirstOrDefaultAsync(ct);
        if (s is null) { s = new TranscriptionSettings { Id = 1 }; _db.TranscriptionSettings.Add(s); }
        return s;
    }
}
