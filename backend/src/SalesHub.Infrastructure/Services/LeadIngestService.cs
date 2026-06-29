using Microsoft.EntityFrameworkCore;
using SalesHub.Core.Abstractions;
using SalesHub.Core.Domain.Entities;
using SalesHub.Core.Domain.Enums;
using SalesHub.Infrastructure.Persistence;

namespace SalesHub.Infrastructure.Services;

/// <summary>
/// Lógica única de ingest de leads, extraída del LeadImportWorker para que el pull (reengage)
/// y el push (Hub: anuncios / OTP-setup) creen leads exactamente igual.
/// </summary>
public class LeadIngestService : ILeadIngestService
{
    private readonly ApplicationDbContext _db;
    private readonly ILeadAssigner _assigner;
    private readonly IMessageRenderer _renderer;

    public LeadIngestService(ApplicationDbContext db, ILeadAssigner assigner, IMessageRenderer renderer)
    {
        _db = db; _assigner = assigner; _renderer = renderer;
    }

    public async Task<LeadIngestResult> IngestAsync(LeadIngestRequest req, CancellationToken ct = default)
    {
        var product = await _db.Products.FirstOrDefaultAsync(p => p.ProductKey == req.ProductKey, ct);
        if (product is null) return new LeadIngestResult(LeadIngestOutcome.NoProduct, null);

        var phone = CleanPhone(req.Phone);
        if (phone is null) return new LeadIngestResult(LeadIngestOutcome.NoPhone, null);

        // Dedup por (producto, teléfono): si ya lo tenemos, no recreamos.
        if (await _db.Leads.AnyAsync(x => x.ProductKey == req.ProductKey && x.WhatsappPhone == phone, ct))
            return new LeadIngestResult(LeadIngestOutcome.Duplicate, null);

        var lead = new Lead
        {
            Id = Guid.NewGuid(),
            ProductKey = req.ProductKey,
            Source = req.Source,
            ExternalId = req.ExternalId,
            Name = string.IsNullOrWhiteSpace(req.Name) ? (req.BusinessName ?? "Lead") : req.Name!,
            WhatsappPhone = phone,
            Status = LeadStatus.New,
        };
        _db.Leads.Add(lead);

        var sellerId = await _assigner.PickForLeadAsync(req.ProductKey, null, null, ct);
        // Transporte gestionado por la app: el assigner sólo devuelve vendedores con instancia
        // CONECTADA en sales-hub. Para productos relay no hace falta (manda la app), así que
        // caemos a cualquier vendedor activo que maneje el producto, sólo para tracking/CRM.
        if (sellerId is null && product.AppManagedTransport)
        {
            var actives = await _db.Sellers.Where(s => s.IsActive).ToListAsync(ct);
            var match = actives.FirstOrDefault(s =>
                s.VerticalsWhitelist == null || s.VerticalsWhitelist.Count == 0
                || s.VerticalsWhitelist.Contains(req.ProductKey));
            sellerId = match?.Id;
        }
        if (sellerId is not null)
        {
            var seller = await _db.Sellers.Include(s => s.EvolutionInstance)
                .FirstOrDefaultAsync(s => s.Id == sellerId, ct);
            if (seller is not null)
            {
                lead.SellerId = seller.Id;
                lead.AssignedAt = DateTimeOffset.UtcNow;
                lead.Status = LeadStatus.Assigned;
                lead.RenderedMessage = _renderer.Render(lead, product, seller);
                // Relay: encolamos el opener aunque el vendedor no tenga instancia conectada en
                // sales-hub (la app provee el transporte). No-relay: igual que antes.
                var canQueue = lead.RenderedMessage is not null
                    && (seller.EvolutionInstance is not null || product.AppManagedTransport);
                if (canQueue)
                {
                    OutboxEnqueueHelper.EnqueueLeadMessages(
                        _db, _renderer, lead, product, seller, phone,
                        seller.EvolutionInstance?.InstanceName ?? string.Empty);
                    lead.Status = LeadStatus.Queued;
                    lead.QueuedAt = DateTimeOffset.UtcNow;
                }
            }
        }

        await _db.SaveChangesAsync(ct);
        return new LeadIngestResult(LeadIngestOutcome.Created, lead.Id);
    }

    private static string? CleanPhone(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var digits = new string(raw.Where(char.IsDigit).ToArray());
        return digits.Length >= 8 ? digits : null;
    }
}
