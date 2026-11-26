using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Cors;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using SalesHub.Core.Abstractions;
using SalesHub.Core.Domain.Entities;
using SalesHub.Core.Domain.Enums;
using SalesHub.Infrastructure.Persistence;
using SalesHub.Infrastructure.Services;

namespace SalesHub.Api.Controllers;

/// <summary>
/// Agenda de demo de la landing de anuncios (gymhero.fitness/agendar-demo): el visitante elige día y
/// hora, contesta las preguntas que definen su plan y deja sus datos. Deja el lead como "Demo agendada"
/// y le avisa al equipo.
///
/// De dónde salen los horarios:
/// - Con Google Calendar conectado: de sus huecos libres (el mismo calendario que usa el bot de WhatsApp,
///   así no se pisan), y la reserva crea el evento con Meet e invita al lead por mail.
/// - Sin Google Calendar: de la franja configurada (días, horas y duración) menos las demos que ya
///   están agendadas en el CRM. No hay invitación: el vendedor pasa el link por WhatsApp.
///
/// En los dos casos, cada día deja sin ofrecer una parte de los horarios (<see cref="HeldPercent"/>),
/// elegidos al azar pero fijos para ese día. Esos horarios no se pueden reservar.
///
/// Público, con las mismas defensas que <see cref="PublicLandingLeadsController"/>: CORS solo para las
/// landings, rate limit por IP y honeypot.
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

    /// <summary>
    /// Porcentaje de los horarios de cada día que la agenda de la web no ofrece (Eric, 25/11/2026: que no
    /// se vea toda la disponibilidad, ~30% por día, distinto cada día). Se pisa con DemoBooking:HeldPercent.
    /// </summary>
    public const int DefaultHeldPercent = 30;

    private const string WebTag = "pidio-demo-web";
    private const string BookedTag = "agendo-demo-web";

    // Respuestas válidas de cada pregunta y cómo se le muestran al vendedor.
    private static readonly Dictionary<string, string> Centers = new(StringComparer.Ordinal)
    {
        ["gimnasio"] = "Gimnasio", ["crossfit-funcional"] = "CrossFit o funcional", ["pilates-yoga"] = "Pilates o yoga",
        ["artes-marciales"] = "Artes marciales", ["otro"] = "Otro centro deportivo",
    };
    private static readonly Dictionary<string, string> Sites = new(StringComparer.Ordinal)
    {
        ["1"] = "Una sede", ["2"] = "Dos sedes", ["3+"] = "Tres sedes o más",
        // Los de antes del 26/11/2026, por si queda una pestaña abierta con el formulario viejo.
        ["2-3"] = "Dos o tres sedes", ["4+"] = "Más de tres sedes",
    };
    /// <summary>Opción múltiple: llega como los valores separados por coma.</summary>
    private static readonly Dictionary<string, string> PaymentPlaces = new(StringComparer.Ordinal)
        { ["mercadopago"] = "Mercado Pago", ["banco"] = "transferencia al banco", ["efectivo"] = "efectivo" };
    private static readonly Dictionary<string, string> Routines = new(StringComparer.Ordinal)
        { ["si"] = "Les arma rutinas", ["no"] = "No arma rutinas" };
    private static readonly Dictionary<string, string> AccessModes = new(StringComparer.Ordinal)
    {
        ["qr"] = "Ingreso con QR", ["huella-facial"] = "Ingreso con huella o facial (hay que integrar el lector)",
        ["no"] = "No controla el ingreso",
    };

    private static readonly Regex EmailRx = new(@"^[^\s@]+@[^\s@]+\.[^\s@]{2,}$", RegexOptions.Compiled);
    private static readonly Regex AdIdRx = new(@"^\d{6,25}$", RegexOptions.Compiled);

    /// <param name="Google">Hay Google Calendar: la reserva crea el evento con Meet y manda la invitación.</param>
    /// <param name="Grid">Todos los horarios futuros de la semana, se ofrezcan o no.</param>
    /// <param name="Offered">Los que se pueden reservar ahora.</param>
    private sealed record Agenda(bool Google, List<DateTimeOffset> Grid, HashSet<DateTimeOffset> Offered);

    // La agenda se arma como mucho una vez por minuto, aunque entren muchas visitas. Value null = el
    // último intento no pudo leer el calendario (también se recuerda, para no insistirle a Google).
    private sealed record CacheEntry(DateTimeOffset At, Agenda? Value);
    private static readonly SemaphoreSlim CacheGate = new(1, 1);
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(60);
    private static volatile CacheEntry? _cache;

    // Tope global de reservas por hora: si alguien se pone a llenar la agenda con datos falsos desde
    // varias IP, frena solo y la página ofrece coordinar por WhatsApp.
    private const int MaxBookingsPerHour = 12;
    private static readonly Queue<DateTimeOffset> RecentBookings = new();

    private readonly GoogleCalendarClient _calendar;
    private readonly ILeadIngestService _ingest;
    private readonly IPhoneNormalizer _phones;
    private readonly ApplicationDbContext _db;
    private readonly IAdminAlerter _alerter;
    private readonly int _heldPercent;
    private readonly ILogger<PublicDemoBookingController> _log;

    public PublicDemoBookingController(GoogleCalendarClient calendar, ILeadIngestService ingest, IPhoneNormalizer phones,
        ApplicationDbContext db, IAdminAlerter alerter, IConfiguration config, ILogger<PublicDemoBookingController> log)
    {
        _calendar = calendar; _ingest = ingest; _phones = phones; _db = db; _alerter = alerter; _log = log;
        _heldPercent = Math.Clamp(config.GetValue<int?>("DemoBooking:HeldPercent") ?? DefaultHeldPercent, 0, 90);
    }

    /// <summary>
    /// Plan que le corresponde según las respuestas: es lo que define cada plan en la página de precios
    /// (sedes para Multi-sede y A medida, rutinas para Coaching Pro).
    /// </summary>
    public static string SuggestedPlan(string sites, string routines) => sites switch
    {
        "1" => routines == "si" ? "Coaching Pro" : "Starter",
        "4+" => "A medida",
        _ => "Multi-sede",
    };

    /// <summary>"Mercado Pago, transferencia al banco y efectivo". Null si falta o trae algo que no es una opción.</summary>
    public static string? PaymentsText(string? payments)
    {
        var values = (payments ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct().ToList();
        if (values.Count == 0 || values.Any(v => !PaymentPlaces.ContainsKey(v))) return null;
        var labels = values.Select(v => PaymentPlaces[v]).ToList();
        return labels.Count == 1 ? labels[0] : $"{string.Join(", ", labels.Take(labels.Count - 1))} y {labels[^1]}";
    }

    /// <summary>
    /// Los horarios de un día que no se ofrecen: <paramref name="percent"/>% de la grilla completa del día,
    /// elegidos con un orden que depende solo de la fecha y la hora (cada día salen otros, pero durante
    /// el día no cambian aunque se recargue la página o pasen las horas).
    /// </summary>
    public static HashSet<DateTimeOffset> HeldSlots(IReadOnlyList<DateTimeOffset> daySlots, int percent)
    {
        var count = (int)Math.Round(daySlots.Count * percent / 100.0, MidpointRounding.AwayFromZero);
        if (count <= 0) return new();
        return daySlots.OrderBy(s => BitConverter.ToUInt32(
                SHA256.HashData(Encoding.UTF8.GetBytes($"demo-agenda|{DemoScheduling.Local(s):yyyy-MM-dd HH:mm}")), 0))
            .Take(Math.Min(count, daySlots.Count)).ToHashSet();
    }

    /// <param name="Payments">Dónde cobra (opción múltiple). Opcional: el formulario de antes no la preguntaba.</param>
    public record Answers(string? Center, string? Sites, string? Routines, string? Access, string? Payments = null);

    public record Attribution(string? UtmSource, string? UtmMedium, string? UtmCampaign, string? UtmContent,
        string? Fbclid, string? Fbp);

    public record BookRequest(
        string? ProductKey, string? Name, string? Phone, string? Email, string? Business, string? City,
        Answers? Answers, DateTimeOffset? Slot, string? Page, Attribution? Attribution,
        /// <summary>Honeypot: campo oculto que una persona nunca completa.</summary>
        string? Website);

    /// <summary>
    /// Horarios de los próximos días, agrupados por día (hora de Argentina). Cada horario dice si se puede
    /// reservar (<c>busy=false</c>) o no. <c>invite</c>: al reservar le llega la invitación al mail.
    /// </summary>
    [HttpGet("slots")]
    [EnableRateLimiting(LandingCors.SlotsRateLimitPolicy)]
    public async Task<IActionResult> Slots([FromQuery] string? productKey, CancellationToken ct)
    {
        if (!Products.Contains((productKey ?? string.Empty).Trim().ToLowerInvariant()))
            return BadRequest(new { error = "Producto inválido." });
        var agenda = await AgendaAsync(fresh: false, ct);
        var now = DateTimeOffset.UtcNow;
        var days = agenda is null ? new List<object>() : ToDays(agenda, now);
        return Ok(new { available = agenda is not null && agenda.Offered.Any(s => s > now), invite = agenda?.Google ?? false, days });
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
        if (email is not null && !EmailRx.IsMatch(email)) return BadRequest(new { error = "Revisá tu mail." });

        var a = req.Answers;
        if (a?.Center is null || !Centers.TryGetValue(a.Center, out var center)
            || a.Sites is null || !Sites.TryGetValue(a.Sites, out var sites)
            || a.Routines is null || !Routines.TryGetValue(a.Routines, out var routines)
            || a.Access is null || !AccessModes.TryGetValue(a.Access, out var access))
            return BadRequest(new { error = "Faltan respuestas de las preguntas." });
        var payments = PaymentsText(a.Payments);
        if (a.Payments is not null && payments is null)
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
        var agenda = await AgendaAsync(fresh: true, ct);
        if (agenda is null) return StatusCode(503, new { error = "La agenda no está disponible en este momento.", agendaOff = true });
        slot = slot.ToUniversalTime();
        if (!agenda.Offered.Contains(slot))
            return Conflict(new { error = "Ese horario se acaba de ocupar. Elegí otro.", invite = agenda.Google, days = ToDays(agenda, now) });
        // Con Google Calendar la invitación va al mail: sin mail no hay cómo mandarle el link.
        if (agenda.Google && email is null)
            return BadRequest(new { error = "Dejanos tu mail: ahí te llega el link de la videollamada." });

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
        var summary = $"{center}. {sites}. {(payments is null ? "" : $"Cobra con {payments}. ")}{routines}. {access}. Plan sugerido: {plan}.";

        if (agenda.Google)
        {
            var booking = await _calendar.BookAsync(slot, $"Demo {app} - {business ?? name}",
                $"Demo de {app} agendada desde la web.\nNegocio: {business ?? "-"}{(city is null ? "" : $" ({city})")}\n" +
                $"Contacto: {name}\nWhatsApp: +{phone}\nMail: {email}\n{summary}", email!, ct);
            if (!booking.Ok)
            {
                _log.LogWarning("Reserva de demo: Google Calendar no la tomó ({Error})", booking.Error);
                var again = await AgendaAsync(fresh: true, ct);
                return Conflict(new
                {
                    error = "Ese horario se acaba de ocupar. Elegí otro.",
                    invite = again?.Google ?? false,
                    days = again is null ? new List<object>() : ToDays(again, now),
                });
            }
        }

        var result = await _ingest.IngestAsync(new LeadIngestRequest(
            productKey, LeadSource.LandingForm, null, name, business, phone, email, DemoScheduledAt: slot), ct);

        var lead = await _db.Leads.Include(l => l.Seller)
            .FirstOrDefaultAsync(l => l.ProductKey == productKey && l.WhatsappPhone == phone, ct);
        if (lead is not null)
        {
            var ar = DemoScheduling.Local(now);
            var note = $"Agendó la demo en la web para {when} ({ar:dd/MM HH:mm} ART). {summary}" +
                       $"{(business is null ? "" : $" Negocio: {business}.")}{(city is null ? "" : $" Ciudad: {city}.")}" +
                       $"{(email is null ? "" : $" Mail: {email}.")}{(Clip(req.Page, 120) is { } pg ? $" Página: {pg}." : "")}";
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
        _cache = null; // la próxima visita ya no ve este horario

        var alert = new StringBuilder($"📅 DEMO AGENDADA — {productKey}: {name} · +{phone}{(email is null ? "" : $" · {email}")}");
        alert.Append($"\n{char.ToUpperInvariant(when[0])}{when[1..]}, por videollamada.");
        alert.Append(agenda.Google
            ? " La invitación con el link ya le llegó al mail."
            : " No hay Google Calendar conectado: no se le mandó invitación, pasale el link por WhatsApp.");
        if (business is not null || city is not null) alert.Append($"\nNegocio: {business ?? "-"}{(city is null ? "" : $", {city}")}.");
        alert.Append($"\n{summary}");
        alert.Append(result.Outcome == LeadIngestOutcome.Duplicate ? "\nYa estaba en el CRM." : "\nNuevo en el CRM.");
        if (!string.IsNullOrWhiteSpace(lead?.Seller?.DisplayName)) alert.Append($" Vendedor: {lead!.Seller!.DisplayName}.");
        await _alerter.AlertAsync(alert.ToString(), ct);

        _log.LogInformation("Demo agendada en la web {Product} → {Outcome} ({When}, plan {Plan}, google={Google})",
            productKey, result.Outcome, when, plan, agenda.Google);
        return Ok(new { ok = true, when, start = Iso(slot), invite = agenda.Google });
    }

    // ── helpers ─────────────────────────────────────────────────────────────────────────

    /// <summary>La agenda de la semana. Null = hay Google Calendar conectado pero no se pudo leer.</summary>
    private async Task<Agenda?> AgendaAsync(bool fresh, CancellationToken ct)
    {
        if (!fresh && _cache is { } hit && DateTimeOffset.UtcNow - hit.At < CacheTtl) return hit.Value;
        await CacheGate.WaitAsync(ct);
        try
        {
            if (!fresh && _cache is { } again && DateTimeOffset.UtcNow - again.At < CacheTtl) return again.Value;
            var agenda = await BuildAgendaAsync(ct);
            _cache = new CacheEntry(DateTimeOffset.UtcNow, agenda);
            return agenda;
        }
        finally { CacheGate.Release(); }
    }

    private async Task<Agenda?> BuildAgendaAsync(CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var cfg = await _calendar.GetSettingsAsync(ct);
        var google = await _calendar.IsConnectedAsync(ct);

        List<DateTimeOffset>? free;
        if (google)
        {
            free = await _calendar.GetFreeSlotsAsync(now, ct, HorizonDays);
            if (free is null) return null; // conectado pero ilegible: no se ofrece nada antes que pisar un evento real
        }
        else
        {
            // Sin Google Calendar: la franja configurada menos las demos que ya están en el CRM
            // (las que agendó la web, el bot o el vendedor a mano), de cualquier app: el vendedor es el mismo.
            var (from, to) = DemoScheduling.Window(now, HorizonDays);
            var len = Math.Max(cfg.SlotMinutes, 10);
            var booked = await _db.Leads.AsNoTracking()
                .Where(l => l.DemoScheduledAt != null && l.DemoScheduledAt >= from.AddHours(-2) && l.DemoScheduledAt < to
                            && l.Status != LeadStatus.Lost && l.Status != LeadStatus.Blocked)
                .Select(l => l.DemoScheduledAt!.Value).ToListAsync(ct);
            free = DemoScheduling.FreeSlots(booked.Select(b => (b, b.AddMinutes(len))).ToList(), now, cfg, HorizonDays);
        }

        // Grilla de la semana (lo que se ve) y, por día, los horarios que no se ofrecen.
        var earliest = now.AddMinutes(cfg.MinNoticeMinutes);
        var today = DemoScheduling.Local(now).Date;
        var grid = new List<DateTimeOffset>();
        var held = new HashSet<DateTimeOffset>();
        for (var i = 0; i < HorizonDays; i++)
        {
            var day = DemoScheduling.DaySlots(today.AddDays(i), cfg);
            held.UnionWith(HeldSlots(day, _heldPercent));
            grid.AddRange(day.Where(s => s >= earliest));
        }
        var offered = free.Where(s => !held.Contains(s)).ToHashSet();
        return new Agenda(google, grid, offered);
    }

    private static List<object> ToDays(Agenda agenda, DateTimeOffset now)
    {
        var today = DemoScheduling.Local(now).Date;
        return agenda.Grid.Where(s => s > now).GroupBy(s => DemoScheduling.Local(s).Date).OrderBy(g => g.Key)
            .Select(g => (object)new
            {
                date = g.Key.ToString("yyyy-MM-dd"),
                label = DemoScheduling.DayLabel(g.First()),
                rel = g.Key == today ? "Hoy" : g.Key == today.AddDays(1) ? "Mañana" : null,
                slots = g.OrderBy(s => s).Select(s => new
                {
                    start = Iso(s),
                    time = DemoScheduling.Local(s).ToString("HH:mm"),
                    busy = !agenda.Offered.Contains(s),
                }).ToList(),
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
