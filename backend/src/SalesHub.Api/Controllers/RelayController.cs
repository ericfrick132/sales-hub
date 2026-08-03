using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Collections.Concurrent;

namespace SalesHub.Api.Controllers;

/// <summary>
/// Estado y control de los relays Mac que manejan devices Android vía adb.
/// Permite ver el estado de cada relay y tomar control manual.
/// </summary>
[ApiController]
[Route("api/relay")]
[Authorize]
public class RelayController : ControllerBase
{
    /// <summary>
    /// Estado de cada relay activo. Key = device serial, Value = estado.
    /// </summary>
    public static readonly ConcurrentDictionary<string, RelayState> States = new();

    public record RelayState
    {
        public string DeviceSerial { get; set; } = string.Empty;
        public string Action { get; set; } = "idle";           // idle | sending | typing | waiting | paused
        public string? CurrentPhone { get; set; }
        public string? CurrentText { get; set; }
        public DateTimeOffset UpdatedAt { get; set; }
        public int MessagesSentToday { get; set; }
        public int MessagesInQueue { get; set; }
        public string? LastError { get; set; }
    }

    /// <summary>
    /// El relay reporta su estado actual (polling). Sin auth — usa IP interna.
    /// </summary>
    [AllowAnonymous]
    [HttpPut("state")]
    public ActionResult UpdateState([FromBody] RelayState state)
    {
        state.UpdatedAt = DateTimeOffset.UtcNow;
        States[state.DeviceSerial] = state;
        return Ok(new { ok = true });
    }

    /// <summary>
    /// Lista todos los relays activos con su estado.
    /// </summary>
    [HttpGet]
    public ActionResult<List<RelayState>> GetAll()
    {
        return Ok(States.Values.OrderByDescending(s => s.UpdatedAt).ToList());
    }

    /// <summary>
    /// Enviar un comando a un relay (pausar, reanudar, enviar mensaje custom).
    /// </summary>
    [HttpPost("{serial}/command")]
    public async Task<ActionResult> SendCommand(string serial, [FromBody] RelayCommand command)
    {
        if (!States.TryGetValue(serial, out var state))
            return NotFound("Relay no encontrado");

        // El relay hace polling de comandos pendientes
        state.Action = command.Action ?? state.Action;

        return Ok(new { ok = true });
    }

    public record RelayCommand
    {
        public string? Action { get; set; }   // pause | resume | send_now
        public string? Phone { get; set; }
        public string? Text { get; set; }
    }
}
