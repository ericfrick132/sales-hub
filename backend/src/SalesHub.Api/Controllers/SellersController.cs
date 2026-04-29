using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SalesHub.Api.Dtos;
using SalesHub.Core.Abstractions;
using SalesHub.Core.Domain.Entities;
using SalesHub.Core.Domain.Enums;
using SalesHub.Infrastructure.Persistence;

namespace SalesHub.Api.Controllers;

[ApiController]
[Route("api/sellers")]
[Authorize]
public class SellersController : ControllerBase
{
    private readonly ApplicationDbContext _db;
    private readonly IEvolutionClient _evo;

    public SellersController(ApplicationDbContext db, IEvolutionClient evo)
    {
        _db = db; _evo = evo;
    }

    [HttpGet]
    public async Task<ActionResult<IEnumerable<SellerDto>>> List(CancellationToken ct)
    {
        if (!CurrentUser.IsAdmin(User)) return Forbid();
        var sellers = await _db.Sellers.Include(s => s.EvolutionInstance).OrderBy(s => s.DisplayName).ToListAsync(ct);
        return sellers.Select(ToDto).ToList();
    }

    [HttpPost]
    public async Task<ActionResult<SellerDto>> Create([FromBody] CreateSellerRequest req, CancellationToken ct)
    {
        if (!CurrentUser.IsAdmin(User)) return Forbid();
        if (await _db.Sellers.AnyAsync(s => s.Email == req.Email || s.SellerKey == req.SellerKey, ct))
            return Conflict(new { error = "email o seller_key ya existe" });

        var seller = new Seller
        {
            Id = Guid.NewGuid(),
            SellerKey = req.SellerKey,
            DisplayName = req.DisplayName,
            Email = req.Email.ToLowerInvariant(),
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(req.Password),
            VerticalsWhitelist = req.VerticalsWhitelist ?? new(),
            RegionsAssigned = req.RegionsAssigned ?? new(),
            WhatsappPhone = req.WhatsappPhone,
            Role = req.Role,
            IsActive = true,
            WarmupStartedAt = DateTimeOffset.UtcNow
        };
        seller.EvolutionInstance = new EvolutionInstance
        {
            Id = Guid.NewGuid(),
            SellerId = seller.Id,
            InstanceName = $"seller_{req.SellerKey}"
        };
        _db.Sellers.Add(seller);
        await _db.SaveChangesAsync(ct);
        return ToDto(seller);
    }

    [HttpPut("{id:guid}")]
    public async Task<ActionResult<SellerDto>> Update(Guid id, [FromBody] UpdateSellerRequest req, CancellationToken ct)
    {
        var callerId = CurrentUser.Id(User);
        var isAdmin = CurrentUser.IsAdmin(User);
        if (!isAdmin && callerId != id) return Forbid();

        var seller = await _db.Sellers.Include(s => s.EvolutionInstance).FirstOrDefaultAsync(s => s.Id == id, ct);
        if (seller is null) return NotFound();

        if (isAdmin)
        {
            if (req.IsActive is not null) seller.IsActive = req.IsActive.Value;
            if (req.VerticalsWhitelist is not null) seller.VerticalsWhitelist = req.VerticalsWhitelist;
            if (req.RegionsAssigned is not null) seller.RegionsAssigned = req.RegionsAssigned;
        }
        if (req.DisplayName is not null) seller.DisplayName = req.DisplayName;
        if (req.WhatsappPhone is not null) seller.WhatsappPhone = req.WhatsappPhone;
        if (req.Password is not null) seller.PasswordHash = BCrypt.Net.BCrypt.HashPassword(req.Password);

        if (req.SendMode is not null) { seller.SendMode = req.SendMode.Value; ApplyPreset(seller); }
        if (req.DailyCap is not null) seller.DailyCap = req.DailyCap.Value;
        if (req.DailyVariancePct is not null) seller.DailyVariancePct = req.DailyVariancePct.Value;
        if (req.WarmupDays is not null) seller.WarmupDays = req.WarmupDays.Value;
        if (req.ActiveHoursStart is not null) seller.ActiveHoursStart = req.ActiveHoursStart.Value;
        if (req.ActiveHoursEnd is not null) seller.ActiveHoursEnd = req.ActiveHoursEnd.Value;
        if (req.Timezone is not null) seller.Timezone = req.Timezone;
        if (req.DelayMinSeconds is not null) seller.DelayMinSeconds = req.DelayMinSeconds.Value;
        if (req.DelayMaxSeconds is not null) seller.DelayMaxSeconds = req.DelayMaxSeconds.Value;
        if (req.BurstSize is not null) seller.BurstSize = req.BurstSize.Value;
        if (req.BurstPauseMinSeconds is not null) seller.BurstPauseMinSeconds = req.BurstPauseMinSeconds.Value;
        if (req.BurstPauseMaxSeconds is not null) seller.BurstPauseMaxSeconds = req.BurstPauseMaxSeconds.Value;
        if (req.PreSendTypingMinSeconds is not null) seller.PreSendTypingMinSeconds = req.PreSendTypingMinSeconds.Value;
        if (req.PreSendTypingMaxSeconds is not null) seller.PreSendTypingMaxSeconds = req.PreSendTypingMaxSeconds.Value;
        if (req.ReadIncomingFirst is not null) seller.ReadIncomingFirst = req.ReadIncomingFirst.Value;
        if (req.SkipDayProbabilityPct is not null) seller.SkipDayProbabilityPct = req.SkipDayProbabilityPct.Value;
        if (req.TypoProbabilityPct is not null) seller.TypoProbabilityPct = req.TypoProbabilityPct.Value;

        seller.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);
        return ToDto(seller);
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        if (!CurrentUser.IsAdmin(User)) return Forbid();
        var seller = await _db.Sellers.FirstOrDefaultAsync(s => s.Id == id, ct);
        if (seller is null) return NotFound();
        seller.IsActive = false;
        seller.SendingEnabled = false;
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }

    [HttpPost("{id:guid}/sending")]
    public async Task<IActionResult> ToggleSending(Guid id, [FromBody] ToggleSendingRequest req, CancellationToken ct)
    {
        var callerId = CurrentUser.Id(User);
        if (!CurrentUser.IsAdmin(User) && callerId != id) return Forbid();
        var seller = await _db.Sellers.Include(s => s.EvolutionInstance).FirstOrDefaultAsync(s => s.Id == id, ct);
        if (seller is null) return NotFound();
        if (req.Enabled && (seller.EvolutionInstance is null || seller.EvolutionInstance.Status != InstanceStatus.Connected))
            return BadRequest(new { error = "Conectá WhatsApp antes de activar el envío" });
        seller.SendingEnabled = req.Enabled;
        if (req.Enabled && seller.WarmupStartedAt is null) seller.WarmupStartedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);
        return Ok(new { seller.SendingEnabled });
    }

    [HttpGet("{id:guid}/instance/qr")]
    public async Task<ActionResult<QrCodeResponse>> GetQr(Guid id, CancellationToken ct)
    {
        var callerId = CurrentUser.Id(User);
        if (!CurrentUser.IsAdmin(User) && callerId != id) return Forbid();
        var sellerKey = await _db.Sellers.AsNoTracking()
            .Where(s => s.Id == id).Select(s => (string?)s.SellerKey).FirstOrDefaultAsync(ct);
        if (sellerKey is null) return NotFound();

        var instanceName = await _db.EvolutionInstances.AsNoTracking()
            .Where(x => x.SellerId == id).Select(x => x.InstanceName).FirstOrDefaultAsync(ct);
        if (instanceName is null)
        {
            instanceName = $"seller_{sellerKey}";
            _db.EvolutionInstances.Add(new EvolutionInstance
            {
                Id = Guid.NewGuid(),
                SellerId = id,
                InstanceName = instanceName
            });
            await _db.SaveChangesAsync(ct);
        }

        await _evo.EnsureInstanceAsync(instanceName, ct);
        var qr = await _evo.GetQrCodeAsync(instanceName, ct);
        var info = await _evo.GetInstanceStatusAsync(instanceName, ct);

        var now = DateTimeOffset.UtcNow;
        await _db.EvolutionInstances
            .Where(x => x.SellerId == id)
            .ExecuteUpdateAsync(s => s
                .SetProperty(e => e.LastQrCodeBase64, qr)
                .SetProperty(e => e.QrCodeGeneratedAt, now)
                .SetProperty(e => e.UpdatedAt, now), ct);

        return new QrCodeResponse(qr, info.Status);
    }

    [HttpPost("{id:guid}/instance/logout")]
    public async Task<IActionResult> Logout(Guid id, CancellationToken ct)
    {
        var callerId = CurrentUser.Id(User);
        if (!CurrentUser.IsAdmin(User) && callerId != id) return Forbid();
        var seller = await _db.Sellers.Include(s => s.EvolutionInstance).FirstOrDefaultAsync(s => s.Id == id, ct);
        if (seller?.EvolutionInstance is null) return NotFound();
        await _evo.LogoutInstanceAsync(seller.EvolutionInstance.InstanceName, ct);
        seller.EvolutionInstance.Status = InstanceStatus.Disconnected;
        seller.SendingEnabled = false;
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }

    private static void ApplyPreset(Seller s)
    {
        switch (s.SendMode)
        {
            case SendMode.Conservative:
                s.DailyCap = 25; s.DelayMinSeconds = 90; s.DelayMaxSeconds = 300;
                s.BurstSize = 3; s.BurstPauseMinSeconds = 1800; s.BurstPauseMaxSeconds = 3600;
                s.SkipDayProbabilityPct = 10; s.WarmupDays = 10;
                break;
            case SendMode.Balanced:
                s.DailyCap = 50; s.DelayMinSeconds = 45; s.DelayMaxSeconds = 180;
                s.BurstSize = 4; s.BurstPauseMinSeconds = 900; s.BurstPauseMaxSeconds = 2700;
                s.SkipDayProbabilityPct = 5; s.WarmupDays = 7;
                break;
            case SendMode.Aggressive:
                s.DailyCap = 100; s.DelayMinSeconds = 25; s.DelayMaxSeconds = 90;
                s.BurstSize = 6; s.BurstPauseMinSeconds = 600; s.BurstPauseMaxSeconds = 1800;
                s.SkipDayProbabilityPct = 2; s.WarmupDays = 5;
                break;
        }
    }

    private static SellerDto ToDto(Seller s) => new(
        s.Id, s.SellerKey, s.DisplayName, s.Email, s.Role.ToString(), s.IsActive, s.SendingEnabled,
        s.WhatsappPhone, s.EvolutionInstance?.InstanceName, s.EvolutionInstance?.Status,
        s.VerticalsWhitelist, s.RegionsAssigned, s.SendMode, s.DailyCap, s.DailyVariancePct, s.WarmupDays, s.WarmupStartedAt,
        s.ActiveHoursStart, s.ActiveHoursEnd, s.Timezone,
        s.DelayMinSeconds, s.DelayMaxSeconds, s.BurstSize, s.BurstPauseMinSeconds, s.BurstPauseMaxSeconds,
        s.PreSendTypingMinSeconds, s.PreSendTypingMaxSeconds, s.ReadIncomingFirst,
        s.SkipDayProbabilityPct, s.TypoProbabilityPct);
}
