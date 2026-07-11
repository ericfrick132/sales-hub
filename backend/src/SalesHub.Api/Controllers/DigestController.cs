using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SalesHub.Core.Domain.Entities;
using SalesHub.Infrastructure.Persistence;
using SalesHub.Infrastructure.Services;

namespace SalesHub.Api.Controllers;

/// <summary>
/// Config del resumen diario por WhatsApp: on/off, línea, número destino y hora (AR),
/// más preview del texto y envío manual. El motor es
/// <see cref="DailyDigestService"/> (compone y manda) + <c>DailyDigestWorker</c> (agenda).
/// </summary>
[ApiController]
[Route("api/daily-digest")]
[Authorize]
public class DigestController : ControllerBase
{
    private readonly ApplicationDbContext _db;
    private readonly DailyDigestService _digest;
    private static readonly Regex NonDigit = new(@"\D", RegexOptions.Compiled);

    public DigestController(ApplicationDbContext db, DailyDigestService digest)
    {
        _db = db; _digest = digest;
    }

    public record InstanceDto(string InstanceName, string? PhoneNumber, string? Label, string Status);
    public record ConfigDto(bool Enabled, string? InstanceName, string? Phone, int SendHour,
        DateTimeOffset? LastSentAt, string? MasterPhone, List<InstanceDto> Instances);
    public record SetEnabledRequest(bool Enabled);
    public record SetConfigRequest(string? InstanceName, string? Phone, int? SendHour);

    [HttpGet]
    public async Task<ActionResult<ConfigDto>> Get(CancellationToken ct)
    {
        if (!CurrentUser.IsAdmin(User)) return Forbid();
        var s = await _db.DigestSettings.AsNoTracking().FirstOrDefaultAsync(ct);
        var master = await _db.InspirationSettings.AsNoTracking()
            .Select(x => x.MasterPhone).FirstOrDefaultAsync(ct);
        var instances = await _db.EvolutionInstances.AsNoTracking()
            .Include(i => i.Seller)
            .OrderBy(i => i.Seller!.DisplayName)
            .Select(i => new InstanceDto(i.InstanceName, i.ConnectedPhoneNumber, i.Seller!.DisplayName, i.Status.ToString()))
            .ToListAsync(ct);
        return Ok(new ConfigDto(s?.Enabled ?? false, s?.InstanceName, s?.Phone,
            s?.SendHour ?? 21, s?.LastSentAt, master, instances));
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

    [HttpPost("config")]
    public async Task<IActionResult> SetConfig([FromBody] SetConfigRequest req, CancellationToken ct)
    {
        if (!CurrentUser.IsAdmin(User)) return Forbid();
        var name = string.IsNullOrWhiteSpace(req.InstanceName) ? null : req.InstanceName.Trim();
        if (name is not null && !await _db.EvolutionInstances.AnyAsync(i => i.InstanceName == name, ct))
            return BadRequest(new { error = "Instancia desconocida." });

        string? phone = null;
        if (!string.IsNullOrWhiteSpace(req.Phone))
        {
            phone = NonDigit.Replace(req.Phone, "");
            if (phone.Length < 6) return BadRequest(new { error = "Número inválido." });
        }

        var s = await GetOrCreateAsync(ct);
        s.InstanceName = name;
        s.Phone = phone;
        if (req.SendHour is int h) s.SendHour = Math.Clamp(h, 0, 23);
        s.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);
        return Ok(new { instanceName = s.InstanceName, phone = s.Phone, sendHour = s.SendHour });
    }

    /// <summary>El texto que se mandaría ahora mismo, sin mandarlo.</summary>
    [HttpGet("preview")]
    public async Task<IActionResult> Preview(CancellationToken ct)
    {
        if (!CurrentUser.IsAdmin(User)) return Forbid();
        var text = await _digest.ComposeAsync(ct);
        return Ok(new { text });
    }

    /// <summary>Envío manual de prueba: no toca LastSentAt (el automático del día sale igual).</summary>
    [HttpPost("send-now")]
    public async Task<IActionResult> SendNow(CancellationToken ct)
    {
        if (!CurrentUser.IsAdmin(User)) return Forbid();
        var res = await _digest.SendAsync(markSent: false, ct);
        if (!res.Sent) return BadRequest(new { error = res.Error });
        return Ok(new { sent = true, phone = res.Phone, instance = res.Instance });
    }

    private async Task<DigestSettings> GetOrCreateAsync(CancellationToken ct)
    {
        var s = await _db.DigestSettings.FirstOrDefaultAsync(ct);
        if (s is null) { s = new DigestSettings { Id = 1 }; _db.DigestSettings.Add(s); }
        return s;
    }
}
