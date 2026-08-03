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
        // Backfill del numero vinculado: las instancias conectadas viejas no lo tienen guardado
        // (solo el flujo de QR de apps lo estampaba). Una consulta a Evolution por instancia,
        // una sola vez (??=), y queda persistido.
        var dirty = false;
        if (sellers.Any(s => s.EvolutionInstance is { Status: InstanceStatus.Connected } i && string.IsNullOrWhiteSpace(i.ConnectedPhoneNumber)))
        {
            var owners = await _evo.GetInstanceOwnersAsync(ct); // 1 solo call para todas
            foreach (var s in sellers)
            {
                var inst = s.EvolutionInstance;
                if (inst is null || inst.Status != InstanceStatus.Connected || !string.IsNullOrWhiteSpace(inst.ConnectedPhoneNumber))
                    continue;
                if (owners.TryGetValue(inst.InstanceName, out var num)) { inst.ConnectedPhoneNumber = num; dirty = true; }
            }
        }
        if (dirty) await _db.SaveChangesAsync(ct);
        var devices = await LoadDevicesAsync(sellers.Select(s => s.Id), ct);
        return sellers.Select(s => ToDto(s, devices.GetValueOrDefault(s.Id))).ToList();
    }

    /// <summary>Devuelve el seller logueado (para que él mismo lea sus gauges sin admin).</summary>
    [HttpGet("me")]
    public async Task<ActionResult<SellerDto>> Me(CancellationToken ct)
    {
        var id = CurrentUser.Id(User);
        if (id == Guid.Empty) return Forbid();
        var seller = await _db.Sellers.Include(s => s.EvolutionInstance).FirstOrDefaultAsync(s => s.Id == id, ct);
        if (seller is null) return NotFound();
        var devices = await LoadDevicesAsync(new[] { seller.Id }, ct);
        return ToDto(seller, devices.GetValueOrDefault(seller.Id));
    }

    [HttpPost]
    public async Task<ActionResult<SellerDto>> Create([FromBody] CreateSellerRequest req, CancellationToken ct)
    {
        if (!CurrentUser.IsAdmin(User)) return Forbid();
        // Sin key no hay nombre de instancia: un SellerKey vacío generaba la instancia
        // huérfana "seller_" (caso real: rompió el selector de /transcripcion).
        if (string.IsNullOrWhiteSpace(req.SellerKey))
            return BadRequest(new { error = "seller_key es obligatorio" });
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
            if (req.AutoArchiveChats is not null) seller.AutoArchiveChats = req.AutoArchiveChats.Value;
            if (req.VerticalsWhitelist is not null) seller.VerticalsWhitelist = req.VerticalsWhitelist;
            if (req.RegionsAssigned is not null) seller.RegionsAssigned = req.RegionsAssigned;
            if (req.KeywordRules is not null) seller.KeywordRules = req.KeywordRules;
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
        var devices = await LoadDevicesAsync(new[] { seller.Id }, ct);
        return ToDto(seller, devices.GetValueOrDefault(seller.Id));
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
        // Una línea puede salir por Evolution (QR) o por un dispositivo físico (bridge):
        // cualquiera de las dos habilita el envío.
        if (req.Enabled && (seller.EvolutionInstance is null || seller.EvolutionInstance.Status != InstanceStatus.Connected))
        {
            var hasDevice = await _db.Devices.AnyAsync(d => d.SellerId == id, ct);
            if (!hasDevice)
                return BadRequest(new { error = "Vinculá un dispositivo o conectá WhatsApp por QR antes de activar el envío" });
        }
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

        var line = await _db.EvolutionInstances.AsNoTracking()
            .Where(x => x.SellerId == id).Select(x => new { x.InstanceName, x.ProxyUrl }).FirstOrDefaultAsync(ct);
        var instanceName = line?.InstanceName;
        var proxyUrl = line?.ProxyUrl;
        if (instanceName is null)
        {
            if (string.IsNullOrWhiteSpace(sellerKey))
                return BadRequest(new { error = "El vendedor no tiene seller_key: cargásela antes de conectar la línea" });
            instanceName = $"seller_{sellerKey}";
            _db.EvolutionInstances.Add(new EvolutionInstance
            {
                Id = Guid.NewGuid(),
                SellerId = id,
                InstanceName = instanceName
            });
            await _db.SaveChangesAsync(ct);
        }

        // Aplica el proxy de salida de la línea (o el global) al asegurar la instancia.
        await _evo.EnsureInstanceAsync(instanceName, ct, proxyUrl);
        var qr = await _evo.GetQrCodeAsync(instanceName, ct);
        var info = await _evo.GetInstanceStatusAsync(instanceName, ct);

        var now = DateTimeOffset.UtcNow;
        // Map raw Evolution status to our enum so the DB reflects current state immediately
        // (no lag waiting for the InstanceMonitor tick).
        var mapped = info.Status switch
        {
            "open" or "connected" => InstanceStatus.Connected,
            "connecting" or "qr" => InstanceStatus.Connecting,
            "close" or "disconnected" or "not_found" => InstanceStatus.Disconnected,
            _ => InstanceStatus.Unknown
        };
        await _db.EvolutionInstances
            .Where(x => x.SellerId == id)
            .ExecuteUpdateAsync(s => s
                .SetProperty(e => e.LastQrCodeBase64, qr)
                .SetProperty(e => e.QrCodeGeneratedAt, now)
                .SetProperty(e => e.UpdatedAt, now)
                .SetProperty(e => e.Status, mapped)
                .SetProperty(e => e.LastStatusCheckAt, now), ct);
        if (mapped == InstanceStatus.Connected)
        {
            await _db.EvolutionInstances
                .Where(x => x.SellerId == id && x.ConnectedAt == null)
                .ExecuteUpdateAsync(s => s.SetProperty(e => e.ConnectedAt, now), ct);
        }

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

    /// <summary>Proxy de salida de la línea de WhatsApp de este vendedor (1 IP por número).</summary>
    [HttpGet("{id:guid}/instance/proxy")]
    public async Task<IActionResult> GetProxy(Guid id, CancellationToken ct)
    {
        if (!CurrentUser.IsAdmin(User) && CurrentUser.Id(User) != id) return Forbid();
        var proxy = await _db.EvolutionInstances.AsNoTracking()
            .Where(x => x.SellerId == id).Select(x => x.ProxyUrl).FirstOrDefaultAsync(ct);
        return Ok(new { proxyUrl = proxy });
    }

    /// <summary>Setea (o limpia, si viene vacío) el proxy de salida y lo aplica en Evolution al toque.</summary>
    [HttpPut("{id:guid}/instance/proxy")]
    public async Task<IActionResult> SetProxy(Guid id, [FromBody] SetProxyRequest req, CancellationToken ct)
    {
        if (!CurrentUser.IsAdmin(User) && CurrentUser.Id(User) != id) return Forbid();
        var sellerKey = await _db.Sellers.AsNoTracking()
            .Where(s => s.Id == id).Select(s => (string?)s.SellerKey).FirstOrDefaultAsync(ct);
        if (sellerKey is null) return NotFound();
        var proxy = string.IsNullOrWhiteSpace(req.ProxyUrl) ? null : req.ProxyUrl.Trim();

        var instance = await _db.EvolutionInstances.FirstOrDefaultAsync(x => x.SellerId == id, ct);
        if (instance is null)
        {
            if (string.IsNullOrWhiteSpace(sellerKey))
                return BadRequest(new { error = "El vendedor no tiene seller_key: cargásela antes de configurar el proxy" });
            instance = new EvolutionInstance { Id = Guid.NewGuid(), SellerId = id, InstanceName = $"seller_{sellerKey}" };
            _db.EvolutionInstances.Add(instance);
        }
        instance.ProxyUrl = proxy;
        instance.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);

        // Aplica el cambio en Evolution ahora (si la instancia ya existe); best-effort.
        await _evo.EnsureInstanceAsync(instance.InstanceName, ct, proxy);
        return Ok(new { proxyUrl = proxy });
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

    /// <summary>Device asignado por seller (si tiene más de uno, el de heartbeat más reciente).</summary>
    private async Task<Dictionary<Guid, Device>> LoadDevicesAsync(IEnumerable<Guid> sellerIds, CancellationToken ct)
    {
        var ids = sellerIds.ToList();
        var devices = await _db.Devices.AsNoTracking()
            .Where(d => d.SellerId != null && ids.Contains(d.SellerId.Value))
            .ToListAsync(ct);
        return devices
            .GroupBy(d => d.SellerId!.Value)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(d => d.LastHeartbeatAt ?? DateTimeOffset.MinValue).First());
    }

    private static SellerDeviceDto? ToDeviceDto(Device? d)
    {
        if (d is null) return null;
        var online = d.Status == DeviceStatus.Online
                     && d.LastHeartbeatAt is not null
                     && DateTimeOffset.UtcNow - d.LastHeartbeatAt.Value < TimeSpan.FromSeconds(60);
        return new SellerDeviceDto(d.Id, d.Name, d.Status.ToString(), online, d.BatteryLevel, d.LastHeartbeatAt);
    }

    private static SellerDto ToDto(Seller s, Device? device = null) => new(
        s.Id, s.SellerKey, s.DisplayName, s.Email, s.Role.ToString(), s.IsActive, s.SendingEnabled,
        s.WhatsappPhone, s.EvolutionInstance?.InstanceName, s.EvolutionInstance?.Status,
        s.VerticalsWhitelist, s.RegionsAssigned, s.KeywordRules, s.SendMode, s.DailyCap, s.DailyVariancePct, s.WarmupDays, s.WarmupStartedAt,
        s.ActiveHoursStart, s.ActiveHoursEnd, s.Timezone,
        s.DelayMinSeconds, s.DelayMaxSeconds, s.BurstSize, s.BurstPauseMinSeconds, s.BurstPauseMaxSeconds,
        s.PreSendTypingMinSeconds, s.PreSendTypingMaxSeconds, s.ReadIncomingFirst,
        s.SkipDayProbabilityPct, s.TypoProbabilityPct, s.EvolutionInstance?.ConnectedPhoneNumber,
        s.AutoArchiveChats, ToDeviceDto(device));
}
