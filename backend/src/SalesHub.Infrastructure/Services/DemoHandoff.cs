using Microsoft.EntityFrameworkCore;
using SalesHub.Core.Domain.Entities;
using SalesHub.Core.Domain.Enums;
using SalesHub.Infrastructure.Persistence;

namespace SalesHub.Infrastructure.Services;

/// <summary>
/// Pase de un lead al vendedor que da las demos (<see cref="Seller.DemoHandoffSellerId"/>).
/// La cold caller lo llama, lo lleva a demo y ahí termina su parte: el lead pasa a ser del que
/// da la demo, pero queda <see cref="Lead.OriginSellerId"/> para que el ganado se le cuente a ella.
/// </summary>
public static class DemoHandoff
{
    public record Result(Guid SellerId, string SellerName);

    /// <summary>
    /// Llamar cuando el lead ENTRA en demo por decisión de una persona (CRM, ficha). Si su dueño
    /// pasa las demos a otro vendedor activo, se lo pasa; no guarda. Devuelve el nuevo dueño, o
    /// null si no hubo pase.
    /// El agente de IA no lo usa a propósito: después de agendar sigue la charla, y cambiar de
    /// dueño en ese momento le haría contestar desde otro número.
    /// </summary>
    public static async Task<Result?> ApplyAsync(ApplicationDbContext db, Lead lead, Guid? actorId, CancellationToken ct)
    {
        if (lead.SellerId is not { } ownerId) return null;

        var owner = await db.Sellers.AsNoTracking().Where(s => s.Id == ownerId)
            .Select(s => new { s.DisplayName, s.DemoHandoffSellerId })
            .FirstOrDefaultAsync(ct);
        if (owner?.DemoHandoffSellerId is not { } targetId || targetId == ownerId) return null;

        // Si el que da las demos está inactivo, el lead se queda donde está.
        var target = await db.Sellers.AsNoTracking().Where(s => s.Id == targetId && s.IsActive)
            .Select(s => new Result(s.Id, s.DisplayName))
            .FirstOrDefaultAsync(ct);
        if (target is null) return null;

        var now = DateTimeOffset.UtcNow;
        lead.OriginSellerId ??= ownerId;
        lead.SellerId = target.SellerId;
        // Que el reparto automático no lo devuelva (mismo criterio que asignar a mano).
        lead.ManualAssignedAt = now;
        lead.UpdatedAt = now;

        // Lo que quedaba de la cadencia salía por la línea de la cold caller: ya no va.
        var pending = await db.Outbox
            .Where(o => o.LeadId == lead.Id && o.Status == OutboxStatus.Scheduled)
            .ToListAsync(ct);
        foreach (var o in pending)
        {
            o.Status = OutboxStatus.Cancelled;
            o.Error = $"Pasó a {target.SellerName} para la demo";
        }

        db.LeadNotes.Add(new LeadNote
        {
            Id = Guid.NewGuid(),
            LeadId = lead.Id,
            SellerId = actorId,
            Kind = LeadNoteKind.System,
            Text = $"Pasó a {target.SellerName} para la demo (lo originó {owner.DisplayName})",
        });
        return target;
    }
}
