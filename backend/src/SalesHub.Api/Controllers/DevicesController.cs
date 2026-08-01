using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SalesHub.Core.Domain.Entities;
using SalesHub.Core.Domain.Enums;
using SalesHub.Infrastructure.Persistence;

namespace SalesHub.Api.Controllers;

/// <summary>
/// CRUD de dispositivos Android físicos y generación de tokens de pairing.
/// </summary>
[ApiController]
[Route("api/devices")]
[Authorize]
public class DevicesController : ControllerBase
{
    private readonly ApplicationDbContext _db;

    public DevicesController(ApplicationDbContext db) => _db = db;

    /// <summary>Lista todos los devices (admin).</summary>
    [HttpGet]
    public async Task<ActionResult<List<DeviceDto>>> GetAll()
    {
        var devices = await _db.Devices
            .Include(d => d.Seller)
            .OrderByDescending(d => d.CreatedAt)
            .Select(d => new DeviceDto
            {
                Id = d.Id,
                Name = d.Name,
                SellerId = d.SellerId,
                SellerName = d.Seller != null ? d.Seller.DisplayName : null,
                TailscaleIp = d.TailscaleIp,
                Status = d.Status.ToString(),
                BatteryLevel = d.BatteryLevel,
                LastHeartbeatAt = d.LastHeartbeatAt
            })
            .ToListAsync();

        return Ok(devices);
    }

    /// <summary>Crear un nuevo device y generar token de pairing.</summary>
    [HttpPost]
    public async Task<ActionResult<DeviceCreatedDto>> Create([FromBody] CreateDeviceBody body)
    {
        if (string.IsNullOrWhiteSpace(body.Name))
            return BadRequest("Name is required");

        var token = Random.Shared.Next(100000, 999999).ToString();

        var device = new Device
        {
            Id = Guid.NewGuid(),
            Name = body.Name,
            SellerId = body.SellerId,
            PairingToken = token,
            PairingTokenExpiresAt = DateTimeOffset.UtcNow.AddMinutes(10),
            Status = DeviceStatus.Pairing
        };

        _db.Devices.Add(device);
        await _db.SaveChangesAsync();

        var qrUrl = $"wss://api.sales.efcloud.tech/ws/devices?token={token}";

        return Ok(new DeviceCreatedDto
        {
            Id = device.Id,
            Name = device.Name,
            PairingToken = token,
            ExpiresAt = device.PairingTokenExpiresAt!.Value,
            QrUrl = qrUrl
        });
    }

    /// <summary>Eliminar un device.</summary>
    [HttpDelete("{id:guid}")]
    public async Task<ActionResult> Delete(Guid id)
    {
        var device = await _db.Devices.FindAsync(id);
        if (device is null) return NotFound();
        _db.Devices.Remove(device);
        await _db.SaveChangesAsync();
        return Ok(new { ok = true });
    }

    /// <summary>Asignar device a un seller.</summary>
    [HttpPut("{id:guid}/assign")]
    public async Task<ActionResult> Assign(Guid id, [FromBody] AssignDeviceBody body)
    {
        var device = await _db.Devices.FindAsync(id);
        if (device is null) return NotFound();

        device.SellerId = body.SellerId;
        await _db.SaveChangesAsync();
        return Ok(new { ok = true });
    }

    /// <summary>Regenerar token de pairing para un device existente.</summary>
    [HttpPost("{id:guid}/regenerate-token")]
    public async Task<ActionResult<DeviceCreatedDto>> RegenerateToken(Guid id)
    {
        var device = await _db.Devices.FindAsync(id);
        if (device is null) return NotFound();

        var token = Random.Shared.Next(100000, 999999).ToString();
        device.PairingToken = token;
        device.PairingTokenExpiresAt = DateTimeOffset.UtcNow.AddMinutes(10);
        device.Status = DeviceStatus.Pairing;
        await _db.SaveChangesAsync();

        return Ok(new DeviceCreatedDto
        {
            Id = device.Id,
            Name = device.Name,
            PairingToken = token,
            ExpiresAt = device.PairingTokenExpiresAt!.Value,
            QrUrl = $"wss://api.sales.efcloud.tech/ws/devices?token={token}"
        });
    }
}

public class DeviceDto
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? SellerName { get; set; }
    public string? TailscaleIp { get; set; }
    public string Status { get; set; } = string.Empty;
    public int? BatteryLevel { get; set; }
    public DateTimeOffset? LastHeartbeatAt { get; set; }
}

public class DeviceCreatedDto
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string PairingToken { get; set; } = string.Empty;
    public DateTimeOffset ExpiresAt { get; set; }
    public string QrUrl { get; set; } = string.Empty;
}

public class AssignDeviceBody
{
    public Guid? SellerId { get; set; }
}

public class CreateDeviceBody
{
    public string Name { get; set; } = string.Empty;
    public Guid? SellerId { get; set; }
}
