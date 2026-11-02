using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SalesHub.Core.Domain.Entities;
using SalesHub.Infrastructure.Options;
using SalesHub.Infrastructure.Persistence;

namespace SalesHub.Infrastructure.Services;

/// <summary>
/// Agenda de demos sobre Google Calendar (reemplaza a Calendly, cuyo plan gratis no deja reservar por
/// API): lee los ocupados de la cuenta conectada (freeBusy) y crea el evento con Meet invitando al lead,
/// así Google le manda la invitación al mail (los links por WhatsApp están restringidos desde el ban).
/// </summary>
public class GoogleCalendarClient
{
    public const string Scope = "openid email https://www.googleapis.com/auth/calendar.events https://www.googleapis.com/auth/calendar.freebusy";

    private readonly HttpClient _http;
    private readonly ApplicationDbContext _db;
    private readonly GoogleOptions _opts;
    private readonly ILogger<GoogleCalendarClient> _log;
    private string? _accessToken;
    private DateTimeOffset _accessTokenExp;

    public GoogleCalendarClient(HttpClient http, ApplicationDbContext db, IOptions<GoogleOptions> opts, ILogger<GoogleCalendarClient> log)
    {
        _http = http;
        _db = db;
        _opts = opts.Value;
        _log = log;
        _http.Timeout = TimeSpan.FromSeconds(20);
    }

    public string RedirectUri => _opts.CalendarRedirectUri;

    public async Task<GoogleCalendarSettings> GetSettingsAsync(CancellationToken ct)
    {
        var s = await _db.GoogleCalendarSettings.FirstOrDefaultAsync(ct);
        if (s is not null) return s;
        s = new GoogleCalendarSettings();
        _db.GoogleCalendarSettings.Add(s);
        await _db.SaveChangesAsync(ct);
        return s;
    }

    /// <summary>Cliente OAuth efectivo: el del .env si está, si no el cargado desde la config.</summary>
    public (string? Id, string? Secret) ClientFor(GoogleCalendarSettings s) =>
        !string.IsNullOrWhiteSpace(_opts.OAuthClientId) && !string.IsNullOrWhiteSpace(_opts.OAuthClientSecret)
            ? (_opts.OAuthClientId, _opts.OAuthClientSecret)
            : (s.ClientId, s.ClientSecret);

    /// <summary>¿Hay cuenta conectada? (sin llamar a Google).</summary>
    public async Task<bool> IsConnectedAsync(CancellationToken ct)
    {
        var s = await _db.GoogleCalendarSettings.AsNoTracking().FirstOrDefaultAsync(ct);
        if (s is null || string.IsNullOrWhiteSpace(s.RefreshToken)) return false;
        var (id, secret) = ClientFor(s);
        return !string.IsNullOrWhiteSpace(id) && !string.IsNullOrWhiteSpace(secret);
    }

    public string BuildAuthUrl(string clientId, string state) =>
        "https://accounts.google.com/o/oauth2/v2/auth" +
        $"?client_id={Uri.EscapeDataString(clientId)}" +
        $"&redirect_uri={Uri.EscapeDataString(RedirectUri)}" +
        "&response_type=code&access_type=offline&prompt=consent&include_granted_scopes=true" +
        $"&scope={Uri.EscapeDataString(Scope)}" +
        $"&state={Uri.EscapeDataString(state)}";

    /// <summary>Cambia el code del callback por los tokens y guarda el refresh token + el mail de la cuenta.</summary>
    public async Task<(bool Ok, string? Error)> ExchangeCodeAsync(GoogleCalendarSettings s, string code, CancellationToken ct)
    {
        var (id, secret) = ClientFor(s);
        var resp = await _http.PostAsync("https://oauth2.googleapis.com/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["code"] = code, ["client_id"] = id ?? "", ["client_secret"] = secret ?? "",
            ["redirect_uri"] = RedirectUri, ["grant_type"] = "authorization_code",
        }), ct);
        var raw = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode) return (false, $"{(int)resp.StatusCode}: {raw[..Math.Min(raw.Length, 300)]}");
        using var doc = JsonDocument.Parse(raw);
        var root = doc.RootElement;
        if (!root.TryGetProperty("refresh_token", out var rt) || string.IsNullOrWhiteSpace(rt.GetString()))
            return (false, "Google no devolvió refresh token (sacá el permiso de la app en myaccount.google.com/permissions y conectá de nuevo).");
        s.RefreshToken = rt.GetString();
        s.Email = root.TryGetProperty("id_token", out var idt) ? EmailFromIdToken(idt.GetString()) : null;
        s.PendingState = null;
        s.PendingStateAt = null;
        s.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);
        return (true, null);
    }

    private static string? EmailFromIdToken(string? jwt)
    {
        try
        {
            var part = jwt?.Split('.')[1];
            if (part is null) return null;
            part = part.Replace('-', '+').Replace('_', '/');
            part = part.PadRight(part.Length + (4 - part.Length % 4) % 4, '=');
            using var doc = JsonDocument.Parse(Convert.FromBase64String(part));
            return doc.RootElement.TryGetProperty("email", out var e) ? e.GetString() : null;
        }
        catch { return null; }
    }

    private async Task<string?> AccessTokenAsync(GoogleCalendarSettings s, CancellationToken ct)
    {
        if (_accessToken is not null && _accessTokenExp > DateTimeOffset.UtcNow.AddMinutes(1)) return _accessToken;
        var (id, secret) = ClientFor(s);
        if (string.IsNullOrWhiteSpace(s.RefreshToken) || string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(secret)) return null;
        var resp = await _http.PostAsync("https://oauth2.googleapis.com/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["refresh_token"] = s.RefreshToken!, ["client_id"] = id!, ["client_secret"] = secret!, ["grant_type"] = "refresh_token",
        }), ct);
        var raw = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
        {
            _log.LogWarning("Google Calendar: no pude renovar el token ({Status}): {Body}", (int)resp.StatusCode, raw);
            return null;
        }
        using var doc = JsonDocument.Parse(raw);
        _accessToken = doc.RootElement.GetProperty("access_token").GetString();
        _accessTokenExp = DateTimeOffset.UtcNow.AddSeconds(doc.RootElement.TryGetProperty("expires_in", out var e) ? e.GetInt32() : 3000);
        return _accessToken;
    }

    private async Task<HttpRequestMessage?> AuthedAsync(GoogleCalendarSettings s, HttpMethod method, string url, CancellationToken ct)
    {
        var token = await AccessTokenAsync(s, ct);
        if (token is null) return null;
        var req = new HttpRequestMessage(method, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return req;
    }

    /// <summary>Horarios libres para la demo entre ahora y fin de mañana (hora AR). Null = no se pudo leer el calendario.</summary>
    public async Task<List<DateTimeOffset>?> GetFreeSlotsAsync(DateTimeOffset now, CancellationToken ct)
    {
        var s = await GetSettingsAsync(ct);
        var (from, to) = DemoScheduling.Window(now);
        var busy = await BusyAsync(s, from, to, ct);
        return busy is null ? null : DemoScheduling.FreeSlots(busy, now, s);
    }

    /// <summary>Ocupados del calendario entre from y to. Null = no se pudo leer.</summary>
    private async Task<List<(DateTimeOffset Start, DateTimeOffset End)>?> BusyAsync(GoogleCalendarSettings s,
        DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
    {
        try
        {
            var req = await AuthedAsync(s, HttpMethod.Post, "https://www.googleapis.com/calendar/v3/freeBusy", ct);
            if (req is null) return null;
            req.Content = JsonContent.Create(new
            {
                timeMin = from.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ"),
                timeMax = to.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ"),
                items = new[] { new { id = s.CalendarId } },
            });
            var resp = await _http.SendAsync(req, ct);
            var raw = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
            {
                _log.LogWarning("Google Calendar: freeBusy falló ({Status}): {Body}", (int)resp.StatusCode, raw);
                return null;
            }
            using var doc = JsonDocument.Parse(raw);
            var cal = doc.RootElement.GetProperty("calendars").EnumerateObject().FirstOrDefault().Value;
            if (cal.ValueKind != JsonValueKind.Object) return null;
            if (cal.TryGetProperty("errors", out var errs) && errs.GetArrayLength() > 0)
            {
                _log.LogWarning("Google Calendar: freeBusy con errores: {Errors}", errs.ToString());
                return null;
            }
            return cal.GetProperty("busy").EnumerateArray()
                .Select(b => (DateTimeOffset.Parse(b.GetProperty("start").GetString()!), DateTimeOffset.Parse(b.GetProperty("end").GetString()!)))
                .ToList();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex, "Google Calendar: no pude leer los ocupados");
            return null;
        }
    }

    public record Booking(bool Ok, string? EventId, string? Error);

    /// <summary>
    /// Crea la demo en el calendario con Meet e invita al lead: Google le manda la invitación al mail con
    /// el link de la llamada. Antes vuelve a mirar que el horario siga libre (alguien pudo agendar en el medio).
    /// </summary>
    public async Task<Booking> BookAsync(DateTimeOffset start, string title, string description, string attendeeEmail, CancellationToken ct)
    {
        var s = await GetSettingsAsync(ct);
        var end = start.AddMinutes(s.SlotMinutes);
        try
        {
            if (start < DateTimeOffset.UtcNow.AddMinutes(15)) return new(false, null, "el horario ya paso");
            var busy = await BusyAsync(s, start, end, ct);
            if (busy is null) return new(false, null, "no pude leer el calendario");
            if (busy.Any(b => b.Start < end && b.End > start)) return new(false, null, "el horario ya no esta libre");

            var url = $"https://www.googleapis.com/calendar/v3/calendars/{Uri.EscapeDataString(s.CalendarId)}/events?conferenceDataVersion=1&sendUpdates=all";
            var req = await AuthedAsync(s, HttpMethod.Post, url, ct);
            if (req is null) return new(false, null, "Google Calendar sin conectar o token vencido");
            req.Content = JsonContent.Create(new
            {
                summary = title,
                description,
                start = new { dateTime = start.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ"), timeZone = "America/Argentina/Buenos_Aires" },
                end = new { dateTime = end.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ"), timeZone = "America/Argentina/Buenos_Aires" },
                attendees = new[] { new { email = attendeeEmail } },
                conferenceData = new { createRequest = new { requestId = Guid.NewGuid().ToString("N"), conferenceSolutionKey = new { type = "hangoutsMeet" } } },
                reminders = new { useDefault = true },
            });
            var resp = await _http.SendAsync(req, ct);
            var raw = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
            {
                _log.LogWarning("Google Calendar: no se pudo crear la demo {Start} ({Status}): {Body}", start, (int)resp.StatusCode, raw);
                return new(false, null, $"{(int)resp.StatusCode}: {raw[..Math.Min(raw.Length, 300)]}");
            }
            using var doc = JsonDocument.Parse(raw);
            return new(true, doc.RootElement.TryGetProperty("id", out var id) ? id.GetString() : null, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex, "Google Calendar: falló la reserva");
            return new(false, null, ex.Message);
        }
    }
}
