using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SalesHub.Infrastructure.Options;

namespace SalesHub.Infrastructure.Services;

/// <summary>
/// Cliente mínimo de la API de Calendly: horarios libres de un evento y reserva directa (Scheduling
/// API), sin mandarle un link al lead (los links por WhatsApp están restringidos desde el ban).
/// Calendly le manda al lead la invitación por mail con el link de la videollamada.
/// </summary>
public class CalendlyClient
{
    private readonly HttpClient _http;
    private readonly CalendlyOptions _opts;
    private readonly ILogger<CalendlyClient> _log;

    public CalendlyClient(HttpClient http, IOptions<CalendlyOptions> opts, ILogger<CalendlyClient> log)
    {
        _http = http;
        _opts = opts.Value;
        _log = log;
        _http.BaseAddress = new Uri(_opts.BaseUrl.TrimEnd('/') + "/");
        _http.Timeout = TimeSpan.FromSeconds(_opts.TimeoutSeconds);
        if (!string.IsNullOrWhiteSpace(_opts.Token))
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _opts.Token);
    }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_opts.Token);

    public record EventType(string Uri, string Name, int Duration, string SchedulingUrl);

    public async Task<List<EventType>> GetEventTypesAsync(CancellationToken ct)
    {
        if (!IsConfigured) return new();
        try
        {
            var me = await _http.GetFromJsonAsync<JsonElement>("users/me", ct);
            var userUri = me.GetProperty("resource").GetProperty("uri").GetString();
            var res = await _http.GetFromJsonAsync<JsonElement>(
                $"event_types?active=true&user={Uri.EscapeDataString(userUri!)}", ct);
            return res.GetProperty("collection").EnumerateArray().Select(e => new EventType(
                e.GetProperty("uri").GetString()!, e.GetProperty("name").GetString() ?? "",
                e.TryGetProperty("duration", out var d) && d.ValueKind == JsonValueKind.Number ? d.GetInt32() : 0,
                e.TryGetProperty("scheduling_url", out var u) ? u.GetString() ?? "" : "")).ToList();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex, "Calendly: no pude listar los eventos");
            return new();
        }
    }

    /// <summary>Horarios libres (inicio de cada turno, UTC) entre from y to. Calendly acepta hasta 7 días por consulta.</summary>
    public async Task<List<DateTimeOffset>?> GetAvailableTimesAsync(string eventTypeUri, DateTimeOffset from, DateTimeOffset to,
        CancellationToken ct)
    {
        if (!IsConfigured) return null;
        var result = new List<DateTimeOffset>();
        try
        {
            // De a 7 días (límite de la API).
            for (var start = from; start < to; start = start.AddDays(7))
            {
                var end = start.AddDays(7) < to ? start.AddDays(7) : to;
                var url = $"event_type_available_times?event_type={Uri.EscapeDataString(eventTypeUri)}" +
                          $"&start_time={Uri.EscapeDataString(start.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ"))}" +
                          $"&end_time={Uri.EscapeDataString(end.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ"))}";
                var res = await _http.GetFromJsonAsync<JsonElement>(url, ct);
                foreach (var t in res.GetProperty("collection").EnumerateArray())
                    if (t.TryGetProperty("status", out var s) && s.GetString() == "available"
                        && DateTimeOffset.TryParse(t.GetProperty("start_time").GetString(), out var st))
                        result.Add(st);
            }
            return result.OrderBy(x => x).ToList();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex, "Calendly: no pude traer los horarios libres");
            return null;
        }
    }

    public record Booking(bool Ok, string? EventUri, string? Error);

    /// <summary>
    /// Reserva el turno a nombre del lead. Calendly le manda la invitación por mail (con el link de
    /// Meet) y dispara sus recordatorios como si hubiera reservado desde la página.
    /// </summary>
    public async Task<Booking> BookAsync(string eventTypeUri, DateTimeOffset start, string name, string email,
        string timezone, string? phoneE164, CancellationToken ct)
    {
        if (!IsConfigured) return new(false, null, "Calendly sin token");
        var body = new Dictionary<string, object?>
        {
            ["event_type"] = eventTypeUri,
            ["start_time"] = start.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ"),
            ["invitee"] = new Dictionary<string, object?>
            {
                ["name"] = string.IsNullOrWhiteSpace(name) ? email : name,
                ["email"] = email,
                ["timezone"] = timezone,
                ["text_reminder_number"] = string.IsNullOrWhiteSpace(phoneE164) ? null : phoneE164,
            }.Where(kv => kv.Value is not null).ToDictionary(kv => kv.Key, kv => kv.Value),
            ["location"] = new Dictionary<string, object?> { ["kind"] = "google_conference" },
        };
        try
        {
            var resp = await _http.PostAsJsonAsync("invitees", body, ct);
            var raw = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
            {
                _log.LogWarning("Calendly: no se pudo reservar {Start} ({Status}): {Body}", start, (int)resp.StatusCode, raw);
                return new(false, null, $"{(int)resp.StatusCode}: {raw[..Math.Min(raw.Length, 300)]}");
            }
            using var doc = JsonDocument.Parse(raw);
            var res = doc.RootElement.GetProperty("resource");
            var ev = res.TryGetProperty("event", out var e) ? e.GetString() : null;
            return new(true, ev, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex, "Calendly: falló la reserva");
            return new(false, null, ex.Message);
        }
    }
}
