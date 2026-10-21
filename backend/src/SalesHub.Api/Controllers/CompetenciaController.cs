using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SalesHub.Core.Domain.Entities;
using SalesHub.Core.Domain.Enums;
using SalesHub.Infrastructure.Persistence;

namespace SalesHub.Api.Controllers;

/// <summary>
/// Tab "Competencia" del CRM: los negocios de los listados de la competencia (audience_leads) que ya tienen
/// teléfono, para LLAMARLOS en frío (deck "Cold calling EF Cloud"). La ven todos y no se reparte: lista común,
/// cada uno toma el siguiente. Nunca se les escribe solos: recién con interés o demo agendada se crea el lead
/// del Pipeline con origen <see cref="LeadSource.Competencia"/> (sin cadencia, sin bot, sin DM automático).
/// </summary>
[ApiController]
[Route("api/competencia")]
[Authorize]
public class CompetenciaController : ControllerBase
{
    private static readonly string[] Results = { "no_atiende", "volver", "demo", "interesado", "no_interesa", "equivocado" };

    private readonly ApplicationDbContext _db;

    public CompetenciaController(ApplicationDbContext db) => _db = db;

    public record CallRow(Guid Id, string Name, string ProductKey, string Competitor, string Segment, string? Category,
        string? City, string? Province, string? Country, string? Phone, string? PhoneType, string? PhoneSource,
        string? SecondaryPhone, string? SecondaryPhoneSource, string? Instagram, string? Website,
        string? CallStatus, string? CallReason, string? CallNotes, int CallCount, DateTimeOffset? LastCallAt,
        string? LastCallByName, DateTimeOffset? NextCallAt, Guid? LeadId);
    public record Facet(string Key, int Count);
    public record CallList(int Total, List<CallRow> Rows, List<Facet> Products, List<Facet> Competitors,
        List<Facet> Provinces, List<Facet> Statuses);

    /// <summary>
    /// Lista para llamar. Orden: primero los "volver a llamar" ya vencidos, después los que nunca se llamaron
    /// (bajas antes que activos) y al final el resto por última llamada. <paramref name="status"/>:
    /// "pendiente" (nunca llamado), "hoy" (volver a llamar vencido), o un resultado.
    /// </summary>
    [HttpGet]
    public async Task<ActionResult<CallList>> List(
        [FromQuery] string? productKey, [FromQuery] string? competitor, [FromQuery] string? segment,
        [FromQuery] string? province, [FromQuery] string? status, [FromQuery] string? q,
        [FromQuery] int limit = 100, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        var all = _db.AudienceLeads.AsNoTracking().Where(a => a.Phone != null || a.SecondaryPhone != null);

        var filtered = all;
        if (!string.IsNullOrWhiteSpace(productKey)) filtered = filtered.Where(a => a.ProductKey == productKey);
        if (!string.IsNullOrWhiteSpace(competitor)) filtered = filtered.Where(a => a.Competitor == competitor);
        if (!string.IsNullOrWhiteSpace(segment)) filtered = filtered.Where(a => a.Segment == segment);
        if (!string.IsNullOrWhiteSpace(province)) filtered = filtered.Where(a => a.Province == province);
        if (!string.IsNullOrWhiteSpace(q))
        {
            var term = q.Trim().ToLower();
            var digits = new string(term.Where(char.IsDigit).ToArray());
            filtered = filtered.Where(a => a.Name.ToLower().Contains(term) || (a.City != null && a.City.ToLower().Contains(term))
                || (digits.Length >= 4 && ((a.Phone != null && a.Phone.Contains(digits)) || (a.SecondaryPhone != null && a.SecondaryPhone.Contains(digits)))));
        }
        // Facetas con los filtros de arriba (sin el de estado), para que los chips muestren cuántos hay.
        var statuses = await filtered.GroupBy(a => a.CallStatus).Select(g => new { g.Key, N = g.Count() }).ToListAsync(ct);
        var due = await filtered.CountAsync(a => a.CallStatus == "volver" && a.NextCallAt <= now, ct);

        filtered = status switch
        {
            null or "" => filtered,
            "pendiente" => filtered.Where(a => a.CallStatus == null),
            "hoy" => filtered.Where(a => a.CallStatus == "volver" && a.NextCallAt <= now),
            _ => filtered.Where(a => a.CallStatus == status),
        };

        var total = await filtered.CountAsync(ct);
        var rows = await filtered
            .OrderByDescending(a => a.CallStatus == "volver" && a.NextCallAt <= now)
            .ThenByDescending(a => a.CallStatus == null)
            .ThenByDescending(a => a.Segment == "baja")
            .ThenBy(a => a.LastCallAt)
            .ThenBy(a => a.Name)
            .Take(Math.Clamp(limit, 1, 500))
            .ToListAsync(ct);
        var callerIds = rows.Where(r => r.LastCallBy != null).Select(r => r.LastCallBy!.Value).Distinct().ToList();
        var names = await _db.Sellers.AsNoTracking().Where(s => callerIds.Contains(s.Id))
            .ToDictionaryAsync(s => s.Id, s => s.DisplayName, ct);

        var products = await all.GroupBy(a => a.ProductKey).Select(g => new Facet(g.Key, g.Count())).ToListAsync(ct);
        var competitors = await all.Where(a => productKey == null || productKey == "" || a.ProductKey == productKey)
            .GroupBy(a => a.Competitor).Select(g => new Facet(g.Key, g.Count())).ToListAsync(ct);
        var provinces = await all.Where(a => a.Province != null && (productKey == null || productKey == "" || a.ProductKey == productKey))
            .GroupBy(a => a.Province!).Select(g => new Facet(g.Key, g.Count())).ToListAsync(ct);
        var statusFacets = statuses.Select(s => new Facet(s.Key ?? "pendiente", s.N)).Append(new Facet("hoy", due)).ToList();

        return new CallList(total,
            rows.Select(a => new CallRow(a.Id, a.Name, a.ProductKey, a.Competitor, a.Segment, a.Category, a.City, a.Province,
                a.Country, a.Phone, a.PhoneType, a.PhoneSource, a.SecondaryPhone, a.SecondaryPhoneSource, a.Instagram, a.Website,
                a.CallStatus, a.CallReason, a.CallNotes, a.CallCount, a.LastCallAt,
                a.LastCallBy is { } by && names.TryGetValue(by, out var n) ? n : null, a.NextCallAt, a.LeadId)).ToList(),
            products.OrderByDescending(f => f.Count).ToList(),
            competitors.OrderByDescending(f => f.Count).ToList(),
            provinces.OrderByDescending(f => f.Count).ToList(),
            statusFacets);
    }

    /// <param name="Result">no_atiende, volver, demo, interesado, no_interesa, equivocado.</param>
    /// <param name="Reason">Motivo de baja del competidor / por qué no le interesa.</param>
    /// <param name="NextAt">Volver a llamar (volver) o día y hora de la demo (demo).</param>
    public record CallRequest(string Result, string? Reason, string? Notes, DateTimeOffset? NextAt);

    /// <summary>
    /// Registra una llamada. Con "demo" o "interesado" crea (una sola vez) el lead del Pipeline a nombre de
    /// quien llamó, con origen Competencia, el bot muteado y las notas de la llamada.
    /// </summary>
    [HttpPost("{id:guid}/call")]
    public async Task<ActionResult<object>> Call(Guid id, [FromBody] CallRequest req, CancellationToken ct)
    {
        if (!Results.Contains(req.Result)) return BadRequest(new { error = $"Resultado inválido: {req.Result}" });
        if (req.Result is "volver" or "demo" && req.NextAt is null)
            return BadRequest(new { error = req.Result == "demo" ? "Falta el día y la hora de la demo." : "Falta cuándo volver a llamar." });
        var a = await _db.AudienceLeads.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (a is null) return NotFound();

        var me = CurrentUser.Id(User);
        var now = DateTimeOffset.UtcNow;
        a.CallStatus = req.Result;
        a.CallReason = Cut(req.Reason, 256) ?? a.CallReason;
        a.CallNotes = Cut(req.Notes, 2000) ?? a.CallNotes;
        a.CallCount++;
        a.LastCallAt = now;
        a.LastCallBy = me == Guid.Empty ? null : me;
        a.NextCallAt = req.Result is "volver" or "demo" ? req.NextAt : null;
        a.UpdatedAt = now;

        if (req.Result is "demo" or "interesado" && a.LeadId is null)
        {
            var phone = a.Phone ?? a.SecondaryPhone!;
            // Si ya es lead de esa app (escribió por un anuncio, Maps…), se vincula ese: no se duplica ni se le cambia el origen.
            var suffix = new string(phone.Where(char.IsDigit).ToArray());
            suffix = suffix.Length >= 8 ? suffix[^8..] : suffix;
            a.LeadId = await _db.Leads.Where(l => l.ProductKey == a.ProductKey && l.WhatsappPhone != null && l.WhatsappPhone.EndsWith(suffix))
                .Select(l => (Guid?)l.Id).FirstOrDefaultAsync(ct);
        }
        if (req.Result is "demo" or "interesado" && a.LeadId is null)
        {
            var phone = a.Phone ?? a.SecondaryPhone!;
            var notes = string.Join("\n", new[]
            {
                $"Competencia: {a.Competitor} ({(a.Segment == "baja" ? "se dio de baja" : "lo usa")})",
                a.SecondaryPhone is not null && a.Phone is not null ? $"Otro teléfono: {a.SecondaryPhone}" : null,
                string.IsNullOrWhiteSpace(a.CallReason) ? null : $"Motivo: {a.CallReason}",
                string.IsNullOrWhiteSpace(a.CallNotes) ? null : a.CallNotes,
            }.Where(x => x is not null));
            var lead = new Lead
            {
                Id = Guid.NewGuid(),
                ProductKey = a.ProductKey,
                Source = LeadSource.Competencia,
                ExternalId = Cut($"{a.Dataset}:{a.ExternalId}", 128),
                PlaceId = a.PlaceId,
                Name = a.Name,
                Address = a.Address,
                City = a.City,
                Province = a.Province,
                Country = a.Country ?? "AR",
                RawPhone = phone,
                WhatsappPhone = phone,
                Website = a.Website,
                InstagramHandle = a.Instagram,
                Latitude = a.Latitude,
                Longitude = a.Longitude,
                SellerId = me == Guid.Empty ? null : me,
                AssignedAt = now,
                ManualAssignedAt = now,
                Status = req.Result == "demo" ? LeadStatus.DemoScheduled : LeadStatus.Interested,
                DemoScheduledAt = req.Result == "demo" ? req.NextAt : null,
                BotMutedAt = now,
                Notes = Cut(notes, 4000),
                CreatedAt = now,
                UpdatedAt = now,
            };
            _db.Leads.Add(lead);
            a.LeadId = lead.Id;
        }
        else if (req.Result == "demo" && a.LeadId is { } leadId)
        {
            var lead = await _db.Leads.FirstOrDefaultAsync(l => l.Id == leadId, ct);
            if (lead is not null)
            {
                lead.Status = LeadStatus.DemoScheduled;
                lead.DemoScheduledAt = req.NextAt;
                lead.UpdatedAt = now;
            }
        }
        await _db.SaveChangesAsync(ct);
        return Ok(new { a.Id, a.CallStatus, a.NextCallAt, a.LeadId });
    }

    private static string? Cut(string? s, int max) =>
        string.IsNullOrWhiteSpace(s) ? null : s.Length <= max ? s.Trim() : s[..max].Trim();
}
