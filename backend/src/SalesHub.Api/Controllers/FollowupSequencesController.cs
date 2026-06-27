using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SalesHub.Core.Domain.Entities;
using SalesHub.Core.Domain.Enums;
using SalesHub.Infrastructure.Persistence;

namespace SalesHub.Api.Controllers;

/// <summary>
/// CRUD GENÉRICO de secuencias de follow-up. sales-hub es agnóstico: cada secuencia es
/// (app, trigger) + pasos { delay, canal, subject?, body }. Las apps las bajan vía
/// GET /api/hub/followup-config y las ejecutan con su propio motor/canales. No hay nada
/// app-específico acá — es puro store configurable.
/// </summary>
[ApiController]
[Route("api/followup-sequences")]
[Authorize(Roles = "Admin")]
public class FollowupSequencesController : ControllerBase
{
    private readonly ApplicationDbContext _db;
    public FollowupSequencesController(ApplicationDbContext db) { _db = db; }

    public record StepDto(int Order, int DelayMinutes, string Channel, string? Subject, string Body);
    public record SequenceDto(Guid Id, string ProductKey, string DisplayName, string Trigger, bool Enabled, List<StepDto> Steps);
    public record ProductRef(string ProductKey, string DisplayName, bool ReengageActive);
    public record SaveSequenceRequest(string ProductKey, string Trigger, bool Enabled, List<StepDto> Steps);

    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken ct)
    {
        var prods = await _db.Products.AsNoTracking()
            .Where(p => p.Active && p.ProductKey != "")
            .OrderBy(p => p.DisplayName)
            .Select(p => new { p.ProductKey, p.DisplayName })
            .ToListAsync(ct);
        // Re-enganche inteligente ACTIVO = la app alimenta SUS PROPIOS leads a sales-hub
        // (sources app-fed: OTP/onboarding/reengage/demo/ad), no los prospectos que scrapeamos.
        // Las que no pushean sus leads quedan marcadas como TODO en la UI.
        var fedSources = new[]
        {
            LeadSource.ProductOnboarding, LeadSource.ProductReengage,
            LeadSource.DemoSignup, LeadSource.WhatsAppAd,
        };
        var fedKeys = (await _db.Leads.AsNoTracking()
            .Where(l => fedSources.Contains(l.Source))
            .Select(l => l.ProductKey).Distinct().ToListAsync(ct)).ToHashSet();
        var products = prods
            .Select(p => new ProductRef(p.ProductKey, p.DisplayName, fedKeys.Contains(p.ProductKey)))
            .ToList();
        var nameByKey = products.ToDictionary(p => p.ProductKey, p => p.DisplayName);

        var seqs = await _db.FollowupSequences.AsNoTracking()
            .OrderBy(s => s.ProductKey).ThenBy(s => s.Trigger).ToListAsync(ct);

        var sequences = seqs.Select(s => new SequenceDto(
            s.Id, s.ProductKey, nameByKey.TryGetValue(s.ProductKey, out var dn) ? dn : s.ProductKey,
            s.Trigger, s.Enabled,
            s.Steps.OrderBy(x => x.Order).Select(x => new StepDto(x.Order, x.DelayMinutes, x.Channel, x.Subject, x.Body)).ToList()));

        return Ok(new { sequences, products });
    }

    [HttpPut]
    public async Task<IActionResult> Upsert([FromBody] SaveSequenceRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.ProductKey) || string.IsNullOrWhiteSpace(req.Trigger))
            return BadRequest(new { error = "productKey y trigger requeridos" });
        if (!await _db.Products.AnyAsync(p => p.ProductKey == req.ProductKey, ct))
            return NotFound(new { error = "Producto desconocido" });

        var trigger = req.Trigger.Trim();
        var s = await _db.FollowupSequences.FirstOrDefaultAsync(x => x.ProductKey == req.ProductKey && x.Trigger == trigger, ct);
        if (s is null) { s = new FollowupSequence { ProductKey = req.ProductKey, Trigger = trigger }; _db.FollowupSequences.Add(s); }

        s.Enabled = req.Enabled;
        s.Steps = (req.Steps ?? new())
            .OrderBy(x => x.Order)
            .Select((x, i) => new FollowupStep
            {
                Order = i,
                DelayMinutes = Math.Max(0, x.DelayMinutes),
                Channel = (x.Channel ?? "").Trim().ToLowerInvariant(),
                Subject = string.IsNullOrWhiteSpace(x.Subject) ? null : x.Subject.Trim(),
                Body = (x.Body ?? "").Trim(),
            })
            .ToList();
        s.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);
        return Ok(new { ok = true, id = s.Id });
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        var s = await _db.FollowupSequences.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (s is null) return NotFound();
        _db.FollowupSequences.Remove(s);
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }
}
