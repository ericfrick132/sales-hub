using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SalesHub.Core.Abstractions;
using SalesHub.Core.Domain.Entities;
using SalesHub.Core.Domain.Enums;
using SalesHub.Infrastructure.Persistence;
using SalesHub.Infrastructure.Services;

namespace SalesHub.Api.Controllers;

/// <summary>
/// Intake externo: cuando se registra un tenant/demo nuevo en el propio producto
/// (TurnosPro, GymHero, etc.) ese producto pega acá → creamos un lead y lo metemos en el
/// MISMO runner de outreach (cadencia del producto + autopilot de WhatsApp). Así los que
/// prueban la app reciben follow-up automático. Autenticado por shared secret
/// (header X-Intake-Key contra Intake:ApiKey). No usa JWT porque lo llama otro sistema.
/// </summary>
[ApiController]
[Route("api/intake")]
[AllowAnonymous]
public class IntakeController : ControllerBase
{
    private readonly ApplicationDbContext _db;
    private readonly ILeadAssigner _assigner;
    private readonly IMessageRenderer _renderer;
    private readonly IConfiguration _config;
    private readonly ILogger<IntakeController> _log;

    public IntakeController(ApplicationDbContext db, ILeadAssigner assigner, IMessageRenderer renderer,
        IConfiguration config, ILogger<IntakeController> log)
    {
        _db = db; _assigner = assigner; _renderer = renderer; _config = config; _log = log;
    }

    public record DemoSignupRequest(
        string ProductKey, string Name, string? WhatsappPhone,
        string? InstagramHandle, string? City, string? Province);

    [HttpPost("demo-signup")]
    public async Task<IActionResult> DemoSignup(
        [FromHeader(Name = "X-Intake-Key")] string? key,
        [FromBody] DemoSignupRequest req, CancellationToken ct)
    {
        var expected = _config["Intake:ApiKey"];
        if (string.IsNullOrWhiteSpace(expected) || key != expected) return Unauthorized();

        if (string.IsNullOrWhiteSpace(req.ProductKey) || string.IsNullOrWhiteSpace(req.Name))
            return BadRequest(new { error = "productKey y name son requeridos" });

        var phone = CleanPhone(req.WhatsappPhone);
        var handle = string.IsNullOrWhiteSpace(req.InstagramHandle) ? null : req.InstagramHandle.Trim().TrimStart('@');
        if (phone is null && handle is null)
            return BadRequest(new { error = "Hace falta whatsappPhone o instagramHandle para poder seguir al lead." });

        var product = await _db.Products.FirstOrDefaultAsync(p => p.ProductKey == req.ProductKey, ct);
        if (product is null) return BadRequest(new { error = $"Producto '{req.ProductKey}' no existe" });

        // Idempotencia: no duplicar el mismo contacto para el mismo producto.
        var existing = await _db.Leads.FirstOrDefaultAsync(l => l.ProductKey == req.ProductKey
            && ((phone != null && l.WhatsappPhone == phone) || (handle != null && l.InstagramHandle == handle)), ct);
        if (existing is not null)
            return Ok(new { leadId = existing.Id, status = existing.Status.ToString(), note = "ya existía" });

        var lead = new Lead
        {
            Id = Guid.NewGuid(),
            ProductKey = req.ProductKey,
            Source = LeadSource.DemoSignup,
            Name = req.Name.Trim(),
            WhatsappPhone = phone,
            InstagramHandle = handle,
            City = req.City,
            Province = req.Province,
            Status = LeadStatus.New,
        };
        _db.Leads.Add(lead);

        // Mismo runner: asignar vendedor + encolar la cadencia del producto. El OutboxSender
        // chequea SendingEnabled/instancia al mandar, así que es seguro encolar igual.
        var sellerId = await _assigner.PickForLeadAsync(req.ProductKey, req.Province, req.City, ct);
        if (sellerId is not null)
        {
            var seller = await _db.Sellers.Include(s => s.EvolutionInstance).FirstOrDefaultAsync(s => s.Id == sellerId, ct);
            if (seller is not null)
            {
                lead.SellerId = seller.Id;
                lead.AssignedAt = DateTimeOffset.UtcNow;
                lead.Status = LeadStatus.Assigned;
                lead.RenderedMessage = _renderer.Render(lead, product, seller);
                if (!string.IsNullOrWhiteSpace(phone) && seller.EvolutionInstance is not null && lead.RenderedMessage is not null)
                {
                    OutboxEnqueueHelper.EnqueueLeadMessages(_db, _renderer, lead, product, seller, phone, seller.EvolutionInstance.InstanceName);
                    lead.Status = LeadStatus.Queued;
                    lead.QueuedAt = DateTimeOffset.UtcNow;
                }
            }
        }

        await _db.SaveChangesAsync(ct);
        _log.LogInformation("Intake demo-signup: lead {Id} ({Product}/{Name}) → {Status}", lead.Id, req.ProductKey, req.Name, lead.Status);
        return Ok(new { leadId = lead.Id, status = lead.Status.ToString() });
    }

    private static string? CleanPhone(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var digits = new string(raw.Where(char.IsDigit).ToArray());
        return digits.Length >= 8 ? digits : null;
    }
}
