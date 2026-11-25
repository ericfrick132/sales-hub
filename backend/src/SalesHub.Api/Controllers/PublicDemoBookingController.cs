using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Cors;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using SalesHub.Core.Abstractions;
using SalesHub.Core.Domain.Enums;
using SalesHub.Infrastructure.Persistence;
using SalesHub.Infrastructure.Services;

namespace SalesHub.Api.Controllers;

/// <summary>
/// Agenda de demo de la landing de anuncios (gymhero.fitness/agendar-demo): el visitante contesta las
/// preguntas que definen su plan, elige día y hora entre los horarios libres del Google Calendar (el mismo
/// que usa el bot de WhatsApp, así no se pisan) y deja sus datos. Reserva el evento con Meet, crea o
/// actualiza el lead como "Demo agendada" y le avisa al equipo.
/// Público, con las mismas defensas que <see cref="PublicLandingLeadsController"/>: CORS solo para las
/// landings, rate limit por IP y honeypot. Si no hay agenda o ningún horario le sirve, la página usa el
/// formulario de siempre (landing-leads).
/// </summary>
[ApiController]
[Route("api/public/demo-booking")]
[AllowAnonymous]
[EnableCors(LandingCors.Policy)]
public class PublicDemoBookingController : ControllerBase
{
    /// <summary>Las preguntas son las de GymHero (definen su plan); otra app necesita las suyas.</summary>
    private static readonly HashSet<string> Products = new(StringComparer.Ordinal) { "gymhero" };

    /// <summary>Días corridos que muestra la agenda (hoy incluido): cubre una semana hábil.</summary>
    public const int HorizonDays = 7;

    private const string WebTag = "pidio-demo-web";
    private const string BookedTag = "agendo-demo-web";

    // Respuestas válidas de cada pregunta y cómo se le muestran al vendedor.
    private static readonly Dictionary<string, string> Centers = new(StringComparer.Ordinal)
    {
        ["gimnasio"] = "Gimnasio", ["crossfit-funcional"] = "CrossFit o funcional", ["pilates-yoga"] = "Pilates o yoga",
        ["artes-marciales"] = "Artes marciales", ["otro"] = "Otro centro deportivo",
    };
    private static readonly Dictionary<string, string> Sites = new(StringComparer.Ordinal)
        { ["1"] = "Una sede", ["2-3"] = "Dos o tres sedes", ["4+"] = "Más de tres sedes" };
    private static readonly Dictionary<string, string> Routines = new(StringComparer.Ordinal)
        { ["si"] = "Les arma rutinas", ["no"] = "No arma rutinas" };
    private static readonly Dictionary<string, string> AccessModes = new(StringComparer.Ordinal)
    {
        ["qr"] = "Ingreso con QR", ["huella-facial"] = "Ingreso con huella o facial (hay que integrar el lector)",
        ["no"] = "No controla el ingreso",
    };

    private static readonly Regex EmailRx = new(@"^[^\s@]+@[^\s@]+\.[^\s@]{2,}$", RegexOptions.Compiled);
    private static readonly Regex AdIdRx = new(@"^\d{6,25}$", RegexOptions.Compiled);

    // Los horarios se leen de Google como mucho una vez por minuto, aunque entren muchas visitas.
    private sealed record SlotCache(DateTimeOffset At, List<DateTimeOffset>? Slots);
    private static readonly SemaphoreSlim CacheGate = new(1, 1);
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(60);
    private static volatile SlotCache? _cache;

    // Tope global de reservas por hora: si alguien se pone a llenar la agenda con datos falsos desde
    // varias IP, frena solo y la página ofrece coordinar por WhatsApp.
    private const int MaxBookingsPerHour = 12;
    private static readonly Queue<DateTimeOffset> RecentBookings = new();

    private readonly GoogleCalendarClient _calendar;
    private readonly ILeadIngestService _ingest;
    private readonly IPhoneNormalizer _phones;
    private readonly ApplicationDbContext _db;
    private readonly IAdminAlerter _alerter;
    private readonly ILogger<PublicDemoBookingController> _log;

    public PublicDemoBookingController(GoogleCalendarClient calendar, ILeadIngestService ingest, IPhoneNormalizer phones,
        ApplicationDbContext db, IAdminAlerter alerter, ILogger<PublicDemoBookingController> log)
    {
        _calendar = calendar; _ingest = ingest; _phones = phones; _db = db; _alerter = alerter; _log = log;
    }

    /// <summary>
    /// Plan que le corresponde según las respuestas: es lo que define cada plan en la página de precios
    /// (sedes para Multi-sede y A medida, rutinas para Coaching Pro).
    /// </summary>
    public static string SuggestedPlan(string sites, string routines) => sites switch
    {
        "4+" => "A medida",
        "2-3" => "Multi-sede",
        _ => routines == "si" ? "Coaching Pro" : "Starter",
    };

    public record Answers(string? Center, string? Sites, string? Routines, string? Access);

    public record Attribution(string? UtmSource, string? UtmMedium, string? UtmCampaign, string? UtmContent,
        string? Fbclid, string? Fbp);

    public record BookRequest(
        string? ProductKey, string? Name, string? Phone, string? Email, string? Business, string? City,
        Answers? Answers, DateTimeOffset? Slot, string? Page, Attribution? Attribution,
        /// <summary>Honeypot: campo oculto que una persona nunca completa.</summary>
        string? Website);

    /// <summary>Horarios libres de los próximos días, agrupados por día (hora de Argentina).</summary>
    [HttpGet("slots")]
    [EnableRateLimiting(LandingCors.SlotsRateLimitPolicy)]
    public async Task<IActionResult> Slots([FromQuery] string? productKey, CancellationToken ct)
    {
        if (!Products.Contains((productKey ?? string.Empty).Trim().ToLowerInvariant()))
            return BadRequest(new { error = "Producto inválido." });
        var free = await FreeAsync(fresh: false, ct);
        var now = DateTimeOffset.UtcNow;
        var days = free is null ? new List<object>() : ToDays(free.Where(s => s > now).ToList(), now);
        return Ok(new { available = days.Count > 0, days });
    }

    [HttpPost]
    [EnableRateLimiting(LandingCors.RateLimitPolicy)]
    public async Task<IActionResult> Book([FromBody] BookRequest req, CancellationToken ct)
    {
        // Bot: respondemos OK para no darle señal, y no reservamos ni creamos nada.
        if (!string.IsNullOrWhiteSpace(req.Website))
        {
            _log.LogInformation("Reserva de demo descartada por honeypot ({Product})", req.ProductKey);
            return Ok(new { ok = true });
        }

        var productKey = (req.ProductKey ?? string.Empty).Trim().ToLowerInvariant();
        if (!Products.Contains(productKey)) return BadRequest(new { error = "Producto inválido." });

        var name = Clip(req.Name, 80);
        if (name is null || name.Length < 2) return BadRequest(new { error = "Contanos tu nombre." });

        var digits = new string((req.Phone ?? string.Empty).Where(char.IsDigit).ToArray());
        if (digits.Length < 8 || digits.Length > 15)
            return BadRequest(new { error = "Revisá el número de WhatsApp (con código de área)." });
        var phone = _phones.NormalizeArStrict(req.Phone) ?? digits;

        var email = Clip(req.Email, 120);
        if (email is null || !EmailRx.IsMatch(email))
            return BadRequest(new { error = "Revisá tu mail: ahí te llega el link de la videollamada." });

        var a = req.Answers;
        if (a?.Center is null || !Centers.TryGetValue(a.Center, out var center)
            || a.Sites is null || !Sites.TryGetValue(a.Sites, out var sites)
            || a.Routines is null || !Routines.TryGetValue(a.Routines, out var routines)
            || a.Access is null || !AccessModes.TryGetValue(a.Access, out var access))
            return BadRequest(new { error = "Faltan respuestas de las preguntas." });

        if (req.Slot is not { } slot) return BadRequest(new { error = "Elegí un día y un horario." });

        var product = await _db.Products.AsNoTracking().FirstOrDefaultAsync(p => p.ProductKey == productKey, ct);
        if (product is null)
        {
            _log.LogError("Reserva de demo: el producto {Product} no existe en sales-hub", productKey);
            return StatusCode(500, new { error = "No pudimos agendar la demo. Probá de nuevo en un rato." });
        }

        var now = DateTimeOffset.UtcNow;
        // Ya tiene una demo por delante (doble clic, o volvió a entrar): no se agenda otra. No se dice
        // cuándo es, para que el formulario no sirva para averiguar la agenda de un teléfono ajeno.
        var existing = await _db.Leads.AsNoTracking()
            .Where(l => l.ProductKey == productKey && l.WhatsappPhone == phone)
            .Select(l => new { l.DemoScheduledAt }).FirstOrDefaultAsync(ct);
        if (existing?.DemoScheduledAt is { } already && already > now)
            return Ok(new { ok = true, already = true });

        // El horario tiene que ser uno de los que ofrece la agenda ahora (no cualquier hora que mande el navegador).
        var free = await FreeAsync(fresh: true, ct);
        if (free is null) return StatusCode(503, new { error = "La agenda no está disponible en este momento.", agendaOff = true });
        if (!free.Any(f => f.UtcDateTime == slot.UtcDateTime))
            return Conflict(new { error = "Ese horario se acaba de ocupar. Elegí otro.", days = ToDays(free.Where(s => s > now).ToList(), now) });

        if (!TryCountBooking(now))
        {
            _log.LogWarning("Reserva de demo: tope de {Max} reservas por hora alcanzado", MaxBookingsPerHour);
            return StatusCode(503, new { error = "La agenda no está disponible en este momento.", agendaOff = true });
        }

        var business = Clip(req.Business, 120);
        var city = Clip(req.City, 80);
        var plan = SuggestedPlan(a.Sites, a.Routines);
        var app = DemoScheduling.AppName(productKey);
        var when = DemoScheduling.SayAbsolute(slot);
        var summary = $"{center}. {sites}. {routines}. {access}. Plan sugerido: {plan}.";

        var booking = await _calendar.BookAsync(slot, $"Demo {app} - {business ?? name}",
            $"Demo de {app} agendada desde la web.\nNegocio: {business ?? "-"}{(city is null ? "" : $" ({city})")}\n" +
            $"Contacto: {name}\nWhatsApp: +{phone}\nMail: {email}\n{summary}", email, ct);
        _cache = null; // la próxima visita ya no ve este horario
        if (!booking.Ok)
        {
            _log.LogWarning("Reserva de demo: Google Calendar no la tomó ({Error})", booking.Error);
            var again = await FreeAsync(fresh: true, ct);
            return Conflict(new
            {
                error = "Ese horario se acaba de ocupar. Elegí otro.",
                days = again is null ? new List<object>() : ToDays(again.Where(s => s > now).ToList(), now),
            });
        }

        var result = await _ingest.IngestAsync(new LeadIngestRequest(
            productKey, LeadSource.LandingForm, null, name, business, phone, email, DemoScheduledAt: slot), ct);

        var lead = await _db.Leads.Include(l => l.Seller)
            .FirstOrDefaultAsync(l => l.ProductKey == productKey && l.WhatsappPhone == phone, ct);
        if (lead is not null)
        {
            var ar = DemoScheduling.Local(now);
            var note = $"Agendó la demo en la web para {when} ({ar:dd/MM HH:mm} ART). {summary}" +
                       $"{(business is null ? "" : $" Negocio: {business}.")}{(city is null ? "" : $" Ciudad: {city}.")} Mail: {email}." +
                       $"{(Clip(req.Page, 120) is { } pg ? $" Página: {pg}." : "")}";
            lead.Notes = string.IsNullOrWhiteSpace(lead.Notes) ? note : $"{lead.Notes}\n{note}";
            foreach (var tag in new[] { WebTag, BookedTag })
                if (!lead.Tags.Contains(tag)) lead.Tags.Add(tag);
            // Misma forma que el formulario de precios, para que se vea en la pestaña "Pedidos web".
            var asWebRequest = JsonSerializer.Serialize(new PublicLandingLeadsController.LandingLeadRequest(
                productKey, name, phone, email, business, null, plan, $"Demo agendada para {when}. {summary}", req.Page, null));
            if (result.Outcome == LeadIngestOutcome.Created) lead.RawDataJson = JsonSerializer.Serialize(req with { Website = null });
            lead.WebRequestAt = now;
            lead.WebRequestJson = asWebRequest;
            lead.Status = LeadStatus.DemoScheduled;
            lead.DemoScheduledAt = slot;
            lead.City ??= city;
            // El anuncio que lo trajo ({{ad.id}} viaja en utm_content), si el lead no venía ya de otro.
            if (lead.AdId is null && req.Attribution?.UtmContent?.Trim() is { } ad && AdIdRx.IsMatch(ad)) lead.AdId = ad;
            lead.UpdatedAt = now;
            await _db.SaveChangesAsync(ct);
        }
        else
            _log.LogError("Reserva de demo: se agendó {When} pero no quedó el lead ({Outcome})", when, result.Outcome);

        var alert = new StringBuilder($"📅 DEMO AGENDADA — {productKey}: {name} · +{phone} · {email}");
        alert.Append($"\n{char.ToUpperInvariant(when[0])}{when[1..]}, por videollamada (la invitación ya le llegó al mail).");
        if (business is not null || city is not null) alert.Append($"\nNegocio: {business ?? "-"}{(city is null ? "" : $", {city}")}.");
        alert.Append($"\n{summary}");
        alert.Append(result.Outcome == LeadIngestOutcome.Duplicate ? "\nYa estaba en el CRM." : "\nNuevo en el CRM.");
        if (!string.IsNullOrWhiteSpace(lead?.Seller?.DisplayName)) alert.Append($" Vendedor: {lead!.Seller!.DisplayName}.");
        await _alerter.AlertAsync(alert.ToString(), ct);

        _log.LogInformation("Demo agendada en la web {Product} → {Outcome} ({When}, plan {Plan})", productKey, result.Outcome, when, plan);
        return Ok(new { ok = true, when, start = Iso(slot) });
    }

    // ── helpers ─────────────────────────────────────────────────────────────────────────

    /// <summary>Horarios libres de la semana. Null = sin Google Calendar conectado o no se pudo leer.</summary>
    private async Task<List<DateTimeOffset>?> FreeAsync(bool fresh, CancellationToken ct)
    {
        if (!fresh && _cache is { } hit && DateTimeOffset.UtcNow - hit.At < CacheTtl) return hit.Slots;
        await CacheGate.WaitAsync(ct);
        try
        {
            if (!fresh && _cache is { } again && DateTimeOffset.UtcNow - again.At < CacheTtl) return again.Slots;
            var slots = await _calendar.IsConnectedAsync(ct)
                ? await _calendar.GetFreeSlotsAsync(DateTimeOffset.UtcNow, ct, HorizonDays)
                : null;
            _cache = new SlotCache(DateTimeOffset.UtcNow, slots);
            return slots;
        }
        finally { CacheGate.Release(); }
    }

    private static List<object> ToDays(List<DateTimeOffset> slots, DateTimeOffset now)
    {
        var today = DemoScheduling.Local(now).Date;
        return slots.GroupBy(s => DemoScheduling.Local(s).Date).OrderBy(g => g.Key)
            .Select(g => (object)new
            {
                date = g.Key.ToString("yyyy-MM-dd"),
                label = DemoScheduling.DayLabel(g.First()),
                rel = g.Key == today ? "Hoy" : g.Key == today.AddDays(1) ? "Mañana" : null,
                slots = g.OrderBy(s => s).Select(s => new { start = Iso(s), time = DemoScheduling.Local(s).ToString("HH:mm") }).ToList(),
            }).ToList();
    }

    private static string Iso(DateTimeOffset t) => t.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ");

    private static bool TryCountBooking(DateTimeOffset now)
    {
        lock (RecentBookings)
        {
            while (RecentBookings.Count > 0 && now - RecentBookings.Peek() > TimeSpan.FromHours(1)) RecentBookings.Dequeue();
            if (RecentBookings.Count >= MaxBookingsPerHour) return false;
            RecentBookings.Enqueue(now);
            return true;
        }
    }

    private static string? Clip(string? v, int max)
    {
        if (string.IsNullOrWhiteSpace(v)) return null;
        var t = v.Trim().ReplaceLineEndings(" ");
        return t.Length > max ? t[..max] : t;
    }
}
