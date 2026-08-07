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
    private readonly SalesHub.Infrastructure.Services.BridgeDirectSendService _testSends;

    public DevicesController(ApplicationDbContext db, SalesHub.Infrastructure.Services.BridgeDirectSendService testSends)
    {
        _db = db;
        _testSends = testSends;
    }

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
                AppVersion = d.AppVersion,
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

        var qrUrl = $"https://api.sales.efcloud.tech/downloads/saleshub-bridge.apk?t={token}";

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

    /// <summary>
    /// "Enviar YA" de prueba: el device manda este texto a este número en su próximo
    /// poll (≤30s), salteando cola, caps y pacing. Para testear el fierro end-to-end.
    /// </summary>
    [HttpPost("{id:guid}/test-send")]
    public async Task<ActionResult> TestSend(Guid id, [FromBody] TestSendBody body)
    {
        var device = await _db.Devices.FindAsync(id);
        if (device is null) return NotFound();

        var phone = new string((body.Phone ?? "").Where(char.IsDigit).ToArray());
        if (phone.Length < 8)
            return BadRequest(new { error = "Número inválido: poné el número completo con código de país (ej 549341...)" });
        var text = (body.Text ?? "").Trim();
        if (text.Length == 0)
            return BadRequest(new { error = "El texto no puede estar vacío" });

        var t = _testSends.Queue(id, phone, text);
        return Ok(new { testId = t.TestId, state = t.State, phone, text });
    }

    /// <summary>
    /// Pide al celu que recorra los chats de WhatsApp y reporte lo que los leads
    /// respondieron. Sirve para recuperar las charlas que quedaron en el teléfono desde
    /// antes (las notificaciones solo cubren lo que llega de ahora en más).
    /// </summary>
    [HttpPost("{id:guid}/sweep-chats")]
    public async Task<ActionResult> SweepChats(Guid id)
    {
        var device = await _db.Devices.FindAsync(id);
        if (device is null) return NotFound();
        _testSends.RequestSweep(id);
        return Ok(new { ok = true, message = "El celu va a recorrer los chats en su próximo chequeo (menos de 30s)" });
    }

    /// <summary>Estado del último "enviar YA" de este device (queued/sending/sent/failed).</summary>
    [HttpGet("{id:guid}/test-send")]
    public ActionResult TestSendStatus(Guid id)
    {
        var t = _testSends.Status(id);
        if (t is null) return Ok(new { state = "none" });
        return Ok(new { testId = t.TestId, state = t.State, error = t.Error, phone = t.Phone, createdAt = t.CreatedAt, finishedAt = t.FinishedAt });
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
            QrUrl = $"https://api.sales.efcloud.tech/downloads/saleshub-bridge.apk?t={token}"
        });
    }
}

public class DeviceDto
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public Guid? SellerId { get; set; }
    public string? SellerName { get; set; }
    public string? TailscaleIp { get; set; }
    public string Status { get; set; } = string.Empty;
    public int? BatteryLevel { get; set; }
    public string? AppVersion { get; set; }
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

public class TestSendBody
{
    public string? Phone { get; set; }
    public string? Text { get; set; }
}

public class CreateDeviceBody
{
    public string Name { get; set; } = string.Empty;
    public Guid? SellerId { get; set; }
}
