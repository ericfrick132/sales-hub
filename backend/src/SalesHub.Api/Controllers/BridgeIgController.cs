using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SalesHub.Infrastructure.Instagram;
using SalesHub.Infrastructure.Persistence;

namespace SalesHub.Api.Controllers;

/// <summary>
/// Instagram para la app Android Bridge. El celular pregunta qué hacer y recibe una lista de
/// pasos (<see cref="IgDeviceScripts"/>); cuando termina, reporta el resultado. Lo consulta solo
/// cuando no tiene nada de WhatsApp para mandar, así que WhatsApp siempre va primero. Misma
/// autenticación que el resto del bridge (header X-Bridge-Key).
/// </summary>
[ApiController]
[Route("api/bridge/ig")]
public class BridgeIgController : ControllerBase
{
    private readonly ApplicationDbContext _db;
    private readonly IConfiguration _cfg;
    private readonly IgOutreachCoordinator _coordinator;
    private readonly IgDeviceActivity _activity;

    public BridgeIgController(ApplicationDbContext db, IConfiguration cfg, IgOutreachCoordinator coordinator, IgDeviceActivity activity)
    {
        _db = db; _cfg = cfg; _coordinator = coordinator; _activity = activity;
    }

    /// <summary>
    /// La próxima indicación para este celular: <c>task</c> con sus pasos, o <c>task: null</c> con
    /// cuánto esperar y por qué. Un celular que no manda Instagram recibe siempre lo segundo.
    /// </summary>
    [HttpGet("next")]
    public async Task<ActionResult> Next([FromQuery] Guid? deviceId, [FromQuery] string? v, CancellationToken ct)
    {
        if (!IsAuthorized()) return Unauthorized();
        if (deviceId is null || !await _db.Devices.AnyAsync(d => d.Id == deviceId, ct))
            return Ok(new { task = (object?)null, waitSeconds = 900, reason = "Device desconocido" });

        var next = await _coordinator.NextAsync(deviceId.Value, ct);
        _activity.Set(deviceId.Value, next.Id is null ? next.Reason ?? "Esperando" : next.Label ?? "Trabajando", v);
        if (next.Id is null)
            return Ok(new { task = (object?)null, waitSeconds = next.WaitSeconds, reason = next.Reason });

        return Ok(new
        {
            task = new { id = next.Id, kind = next.Kind, label = next.Label, timeoutSeconds = next.TimeoutSeconds, steps = next.Steps },
            waitSeconds = 0,
            reason = (string?)null,
        });
    }

    public class ResultBody
    {
        public Guid DeviceId { get; set; }
        public bool Ok { get; set; }
        /// <summary>Código del paso que falló (blocked, perfil_no_existe, send_not_confirmed, humano_en_el_celu…).</summary>
        public string? Error { get; set; }
        /// <summary>Lo que juntó un paso collect (los @usuarios de la lista de seguidores).</summary>
        public List<string>? Collected { get; set; }
        /// <summary>Resumen de la pantalla al fallar.</summary>
        public string? Screen { get; set; }
    }

    [HttpPost("{id:guid}/result")]
    public async Task<ActionResult> Result(Guid id, [FromBody] ResultBody body, CancellationToken ct)
    {
        if (!IsAuthorized()) return Unauthorized();
        var found = await _coordinator.ReportAsync(id,
            new IgOutreachCoordinator.DeviceResult(body.DeviceId, body.Ok, body.Error, body.Collected, body.Screen), ct);
        return found ? Ok(new { ok = true }) : NotFound();
    }

    private bool IsAuthorized()
    {
        var expected = _cfg.GetValue<string>("Bridge:ApiKey");
        if (string.IsNullOrWhiteSpace(expected)) return true; // igual que BridgeController
        var provided = Request.Headers["X-Bridge-Key"].FirstOrDefault();
        return string.Equals(expected, provided, StringComparison.Ordinal);
    }
}
