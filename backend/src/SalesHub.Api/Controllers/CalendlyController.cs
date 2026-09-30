using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SalesHub.Api.Auth;
using SalesHub.Infrastructure.Services;

namespace SalesHub.Api.Controllers;

/// <summary>Calendly para la configuración: eventos del usuario y vista previa de los horarios que ofrecería el bot.</summary>
[ApiController]
[Route("api/calendly")]
[Authorize]
public class CalendlyController : ControllerBase
{
    private readonly CalendlyClient _calendly;
    public CalendlyController(CalendlyClient calendly) => _calendly = calendly;

    [HttpGet("event-types")]
    public async Task<IActionResult> EventTypes(CancellationToken ct)
    {
        if (!CurrentUser.IsAdmin(User)) return Forbid();
        if (!_calendly.IsConfigured) return Ok(new { configured = false, items = Array.Empty<object>() });
        var items = await _calendly.GetEventTypesAsync(ct);
        return Ok(new { configured = true, items });
    }

    /// <summary>Los 2 horarios que ofrecería el bot ahora para ese evento (no reserva nada).</summary>
    [HttpGet("preview")]
    public async Task<IActionResult> Preview([FromQuery] string eventType, CancellationToken ct)
    {
        if (!CurrentUser.IsAdmin(User)) return Forbid();
        var now = DateTimeOffset.UtcNow;
        var free = await _calendly.GetAvailableTimesAsync(eventType, now.AddMinutes(5), now.AddDays(6), ct);
        if (free is null) return BadRequest(new { error = "No pude leer los horarios de Calendly." });
        var two = DemoScheduling.PickTwo(free, now);
        return Ok(new { free = free.Count, offer = two.Select(t => DemoScheduling.Say(t, now)) });
    }
}
