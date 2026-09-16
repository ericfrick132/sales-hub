using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SalesHub.Infrastructure.Persistence;
using SalesHub.Infrastructure.Services;

namespace SalesHub.Api.Controllers;

/// <summary>
/// Leads que entraron a cada teléfono (primer mensaje del contacto por esa línea), para el
/// gráfico de /entradas, y la config del reporte diario que sale por WhatsApp. Devuelve las
/// entradas crudas del rango: son pocas (miles como mucho) y así los filtros del gráfico
/// corren en el navegador sin volver a pedir nada.
/// </summary>
[ApiController]
[Route("api/lead-entries")]
[Authorize]
public class LeadEntriesController : ControllerBase
{
    private readonly ApplicationDbContext _db;
    private readonly LeadEntryService _svc;

    public LeadEntriesController(ApplicationDbContext db, LeadEntryService svc)
    {
        _db = db; _svc = svc;
    }

    private const int MaxDays = 366;

    [HttpGet]
    public async Task<IActionResult> Get([FromQuery] DateOnly? from, [FromQuery] DateOnly? to, CancellationToken ct)
    {
        if (!CurrentUser.IsAdmin(User)) return Forbid();
        var today = LeadEntryService.TodayAr();
        var toDay = to ?? today;
        var fromDay = from ?? toDay.AddDays(-29);
        if (fromDay > toDay) (fromDay, toDay) = (toDay, fromDay);
        if (toDay.DayNumber - fromDay.DayNumber >= MaxDays) fromDay = toDay.AddDays(-(MaxDays - 1));

        var entries = await _svc.GetEntriesAsync(fromDay, toDay, ct);
        var lines = await _svc.GetLinesAsync(entries.Select(e => e.Line), ct);
        var products = await _db.Products.AsNoTracking()
            .Where(p => p.ProductKey != "")
            .Select(p => new { key = p.ProductKey, name = p.DisplayName == "" ? p.ProductKey : p.DisplayName })
            .ToListAsync(ct);
        var sellers = await _db.Sellers.AsNoTracking()
            .Select(s => new { id = s.Id, name = s.DisplayName })
            .ToListAsync(ct);

        return Ok(new
        {
            from = fromDay,
            to = toDay,
            today,
            lines = lines.Select(l => new
            {
                l.InstanceName, l.Label, l.Phone,
                Status = l.Status?.ToString(),
                l.ListenOnly,
                SellerLine = l.SellerId is not null,
                l.Registered,
            }),
            products,
            sellers,
            entries = entries.Select(e => new
            {
                e.Line, e.LeadId, e.FirstAt, e.Day, e.Status, e.ProductKey, e.Source, e.SellerId, e.Name, e.Phone
            }),
        });
    }

    // ── Reporte diario por WhatsApp ─────────────────────────────────────────

    public record ReportSettingsRequest(bool Enabled, string RecipientPhone, int SendHour, string? SendInstanceName);

    [HttpGet("report")]
    public async Task<IActionResult> GetReport(CancellationToken ct)
    {
        if (!CurrentUser.IsAdmin(User)) return Forbid();
        var s = await _svc.GetSettingsAsync(ct);
        var lines = await _db.EvolutionInstances.AsNoTracking()
            .Include(i => i.Seller)
            .OrderBy(i => i.CreatedAt)
            .ToListAsync(ct);
        return Ok(new
        {
            s.Enabled, s.RecipientPhone, s.SendHour, s.SendInstanceName,
            s.LastReportedDay, s.LastSentAt, s.LastAttemptAt, s.LastError,
            lines = lines.Select(i => new
            {
                i.InstanceName,
                Label = LeadEntryService.DisplayName(i),
                Phone = i.ConnectedPhoneNumber,
                Status = i.Status.ToString(),
                i.ListenOnly,
            }),
        });
    }

    [HttpPut("report")]
    public async Task<IActionResult> SaveReport([FromBody] ReportSettingsRequest req, CancellationToken ct)
    {
        if (!CurrentUser.IsAdmin(User)) return Forbid();
        var phone = new string((req.RecipientPhone ?? "").Where(char.IsDigit).ToArray());
        if (phone.Length < 8) return BadRequest(new { error = "El número tiene que ir completo, con código de país (ej. 541169370050)." });
        if (req.SendHour is < 0 or > 23) return BadRequest(new { error = "La hora va de 0 a 23." });
        var instance = string.IsNullOrWhiteSpace(req.SendInstanceName) ? null : req.SendInstanceName.Trim();
        if (instance is not null && !await _db.EvolutionInstances.AnyAsync(i => i.InstanceName == instance, ct))
            return BadRequest(new { error = "Esa línea no existe." });

        var s = await _svc.GetSettingsAsync(ct);
        s.Enabled = req.Enabled;
        s.RecipientPhone = phone;
        s.SendHour = req.SendHour;
        s.SendInstanceName = instance;
        // Cambió la config: que el próximo tick reintente ya, sin esperar la media hora.
        s.LastError = null;
        s.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }

    /// <summary>El texto que saldría hoy (el reporte de ayer), sin mandar nada.</summary>
    [HttpGet("report/preview")]
    public async Task<IActionResult> Preview([FromQuery] DateOnly? day, CancellationToken ct)
    {
        if (!CurrentUser.IsAdmin(User)) return Forbid();
        var d = day ?? LeadEntryService.TodayAr().AddDays(-1);
        return Ok(new { day = d, text = await _svc.BuildReportAsync(d, ct) });
    }

    /// <summary>
    /// Manda YA el reporte de ayer para probar. No marca el día como reportado: el automático
    /// sale igual a su hora.
    /// </summary>
    [HttpPost("report/send-now")]
    public async Task<IActionResult> SendNow(CancellationToken ct)
    {
        if (!CurrentUser.IsAdmin(User)) return Forbid();
        var r = await _svc.SendReportAsync(LeadEntryService.TodayAr().AddDays(-1), ct);
        return Ok(new { r.Ok, r.Via, r.Error });
    }
}
