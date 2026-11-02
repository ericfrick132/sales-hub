using System.Net;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SalesHub.Infrastructure.Persistence;
using SalesHub.Infrastructure.Services;

namespace SalesHub.Api.Controllers;

/// <summary>
/// Google Calendar de las demos del bot: estado de la conexión, cliente OAuth y franja, conectar la
/// cuenta (OAuth con refresh token) y vista previa de los horarios que ofrecería el bot ahora.
/// </summary>
[ApiController]
[Route("api/google-calendar")]
public class GoogleCalendarController : ControllerBase
{
    private readonly GoogleCalendarClient _calendar;
    private readonly ApplicationDbContext _db; // el mismo (scoped) que usa el cliente: guarda sus cambios
    public GoogleCalendarController(GoogleCalendarClient calendar, ApplicationDbContext db) { _calendar = calendar; _db = db; }

    [HttpGet("status")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> Status(CancellationToken ct)
    {
        var s = await _calendar.GetSettingsAsync(ct);
        var (id, secret) = _calendar.ClientFor(s);
        return Ok(new
        {
            hasClient = !string.IsNullOrWhiteSpace(id) && !string.IsNullOrWhiteSpace(secret),
            connected = await _calendar.IsConnectedAsync(ct),
            email = s.Email,
            redirectUri = _calendar.RedirectUri,
            s.StartHour, s.EndHour, s.Days, s.SlotMinutes, s.MinNoticeMinutes,
        });
    }

    public record SettingsDto(string? ClientId, string? ClientSecret, int? StartHour, int? EndHour, string? Days,
        int? SlotMinutes, int? MinNoticeMinutes);

    /// <summary>Guarda el cliente OAuth (si el .env no lo trae) y la franja de demos. Campos null = no se tocan.</summary>
    [HttpPut("settings")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> Save([FromBody] SettingsDto dto, CancellationToken ct)
    {
        var s = await _calendar.GetSettingsAsync(ct);
        if (!string.IsNullOrWhiteSpace(dto.ClientId)) s.ClientId = dto.ClientId.Trim();
        if (!string.IsNullOrWhiteSpace(dto.ClientSecret)) s.ClientSecret = dto.ClientSecret.Trim();
        if (dto.StartHour is >= 0 and <= 23) s.StartHour = dto.StartHour.Value;
        if (dto.EndHour is >= 1 and <= 24) s.EndHour = dto.EndHour.Value;
        if (!string.IsNullOrWhiteSpace(dto.Days)) s.Days = dto.Days.Trim();
        if (dto.SlotMinutes is >= 10 and <= 180) s.SlotMinutes = dto.SlotMinutes.Value;
        if (dto.MinNoticeMinutes is >= 0 and <= 1440) s.MinNoticeMinutes = dto.MinNoticeMinutes.Value;
        if (s.EndHour <= s.StartHour) return BadRequest(new { error = "La hora de fin tiene que ser mayor que la de inicio." });
        s.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);
        return Ok(new { ok = true });
    }

    /// <summary>URL de consentimiento de Google para conectar la cuenta (se abre en otra pestaña).</summary>
    [HttpPost("connect")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> Connect(CancellationToken ct)
    {
        var s = await _calendar.GetSettingsAsync(ct);
        var (id, _) = _calendar.ClientFor(s);
        if (string.IsNullOrWhiteSpace(id)) return BadRequest(new { error = "Falta el cliente OAuth de Google (Client ID y Secret)." });
        s.PendingState = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        s.PendingStateAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);
        return Ok(new { url = _calendar.BuildAuthUrl(id!, s.PendingState) });
    }

    /// <summary>Vuelta del consentimiento de Google (sin JWT: lo valida el state de un solo uso).</summary>
    [HttpGet("callback")]
    [AllowAnonymous]
    public async Task<IActionResult> Callback([FromQuery] string? code, [FromQuery] string? state, [FromQuery] string? error, CancellationToken ct)
    {
        var s = await _calendar.GetSettingsAsync(ct);
        if (!string.IsNullOrWhiteSpace(error)) return Page($"Google no dio permiso: {error}", false);
        var stateOk = !string.IsNullOrWhiteSpace(state) && s.PendingState is not null
                      && CryptographicOperations.FixedTimeEquals(System.Text.Encoding.UTF8.GetBytes(state), System.Text.Encoding.UTF8.GetBytes(s.PendingState))
                      && s.PendingStateAt > DateTimeOffset.UtcNow.AddMinutes(-15);
        if (!stateOk || string.IsNullOrWhiteSpace(code)) return Page("El link vencio o no es valido. Volve a tocar Conectar Google Calendar.", false);
        var (ok, err) = await _calendar.ExchangeCodeAsync(s, code, ct);
        return ok
            ? Page($"Google Calendar conectado{(s.Email is null ? "" : $" ({s.Email})")}. Ya podes cerrar esta pestaña.", true)
            : Page($"No se pudo conectar: {err}", false);
    }

    private ContentResult Page(string text, bool ok) => Content(
        $"<!doctype html><meta charset=utf-8><meta name=viewport content=\"width=device-width\"><title>Google Calendar</title>" +
        $"<body style=\"font-family:system-ui;padding:32px;color:{(ok ? "#166534" : "#991b1b")}\"><p>{WebUtility.HtmlEncode(text)}</p></body>",
        "text/html; charset=utf-8");

    /// <summary>Los horarios que ofrecería el bot ahora (no reserva nada).</summary>
    [HttpGet("preview")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> Preview(CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var free = await _calendar.GetFreeSlotsAsync(now, ct);
        if (free is null) return BadRequest(new { error = "No pude leer el Google Calendar (¿está conectado?)." });
        var two = DemoScheduling.PickTwo(free, now);
        return Ok(new { free = free.Count, offer = two.Select(t => DemoScheduling.Say(t, now)) });
    }
}
