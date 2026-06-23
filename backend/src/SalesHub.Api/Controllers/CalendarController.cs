using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SalesHub.Infrastructure.Persistence;

namespace SalesHub.Api.Controllers;

/// <summary>
/// Calendario principal unificado para "Hoy": junta el contenido concreto ya agendado
/// (posteos con ScheduledAt) con los "runners" proyectados desde la cadencia configurada
/// (generación de posteos por PostHours/PostDays, captación por TriggerHours). Una sola
/// vista de toda la actividad automática del sistema.
/// </summary>
[ApiController]
[Route("api/calendar")]
[Authorize]
public class CalendarController : ControllerBase
{
    private readonly ApplicationDbContext _db;
    public CalendarController(ApplicationDbContext db) { _db = db; }

    public record CalendarEvent(string Id, string Kind, string Title, DateTimeOffset At, string? Status, string ProductKey);

    [HttpGet]
    public async Task<IActionResult> Get([FromQuery] DateTimeOffset from, [FromQuery] DateTimeOffset to, [FromQuery] string? productKey, CancellationToken ct)
    {
        if (to <= from || (to - from).TotalDays > 70) return BadRequest(new { error = "Rango inválido (máx 70 días)." });
        var pk = string.IsNullOrWhiteSpace(productKey) ? null : productKey;
        var events = new List<CalendarEvent>();

        // 1) Posteos ya agendados (fecha concreta)
        var posts = await _db.SocialPosts.AsNoTracking()
            .Where(s => s.ScheduledAt != null && s.ScheduledAt >= from && s.ScheduledAt < to)
            .Where(s => pk == null || s.ProductKey == pk)
            .Select(s => new { s.Id, s.Concept, s.Caption, s.ScheduledAt, s.Status, s.ProductKey, s.Platform })
            .ToListAsync(ct);
        foreach (var p in posts)
        {
            var title = string.IsNullOrWhiteSpace(p.Concept) ? p.Caption : p.Concept;
            events.Add(new($"post:{p.Id}", "post", $"{p.Platform}: {title}", p.ScheduledAt!.Value, p.Status.ToString(), p.ProductKey));
        }

        // 2) Runners proyectados desde la cadencia configurada
        var profiles = await _db.PostingProfiles.AsNoTracking()
            .Where(p => p.Enabled).Where(p => pk == null || p.ProductKey == pk).ToListAsync(ct);
        var products = await _db.Products.AsNoTracking()
            .Where(p => p.Active).Where(p => pk == null || p.ProductKey == pk).ToListAsync(ct);

        var startDay = from.UtcDateTime.Date;
        var endDay = to.UtcDateTime.Date;
        for (var day = startDay; day <= endDay; day = day.AddDays(1))
        {
            var dow = (int)day.DayOfWeek; // 0=Domingo … 6=Sábado

            // Generación de posteos (PostHours, filtrado por PostDays si está seteado)
            foreach (var prof in profiles)
            {
                if (prof.PostDays.Count > 0 && !prof.PostDays.Contains(dow)) continue;
                foreach (var h in prof.PostHours)
                {
                    if (h is < 0 or > 23) continue;
                    var at = new DateTimeOffset(day.Year, day.Month, day.Day, h, 0, 0, TimeSpan.Zero);
                    if (at >= from && at < to)
                        events.Add(new($"runpost:{prof.ProductKey}:{day:yyyyMMdd}:{h}", "runner-post",
                            $"Genera posteos · {prof.ProductKey}", at, null, prof.ProductKey));
                }
            }

            // Captación de leads (TriggerHours del producto)
            foreach (var prod in products)
            {
                foreach (var h in prod.TriggerHours)
                {
                    if (h is < 0 or > 23) continue;
                    var at = new DateTimeOffset(day.Year, day.Month, day.Day, h, 0, 0, TimeSpan.Zero);
                    if (at >= from && at < to)
                        events.Add(new($"runcapt:{prod.ProductKey}:{day:yyyyMMdd}:{h}", "runner-capt",
                            $"Captación · {prod.ProductKey}", at, null, prod.ProductKey));
                }
            }
        }

        return Ok(events.OrderBy(e => e.At));
    }
}
