using SalesHub.Core.Abstractions;
using SalesHub.Core.Domain.Entities;
using SalesHub.Core.Domain.Enums;
using SalesHub.Infrastructure.Persistence;

namespace SalesHub.Infrastructure.Services;

/// <summary>
/// Centraliza el enqueue: si el producto tiene <see cref="Product.OpenerTemplate"/>
/// configurado, mete primero el opener y luego el mensaje principal con +1s en
/// ScheduledAt para garantizar el orden. El espaciado real entre envíos lo da la
/// humanización del seller (DelayMin/Max + tick rate).
/// </summary>
public static class OutboxEnqueueHelper
{
    public static int EnqueueLeadMessages(
        ApplicationDbContext db,
        IMessageRenderer renderer,
        Lead lead,
        Product product,
        Seller seller,
        string whatsappPhone,
        string instanceName,
        DateTimeOffset? scheduledAt = null)
    {
        var when = scheduledAt ?? DateTimeOffset.UtcNow;
        var opener = renderer.RenderOpener(lead, product, seller);
        var main = !string.IsNullOrWhiteSpace(lead.RenderedMessage)
            ? lead.RenderedMessage!
            : renderer.Render(lead, product, seller);

        var count = 0;
        if (!string.IsNullOrWhiteSpace(opener))
        {
            db.Outbox.Add(new MessageOutbox
            {
                Id = Guid.NewGuid(),
                LeadId = lead.Id,
                SellerId = seller.Id,
                EvolutionInstance = instanceName,
                WhatsappPhone = whatsappPhone,
                Message = opener,
                ScheduledAt = when,
                Status = OutboxStatus.Scheduled
            });
            count++;
            when = when.AddSeconds(1);
        }
        db.Outbox.Add(new MessageOutbox
        {
            Id = Guid.NewGuid(),
            LeadId = lead.Id,
            SellerId = seller.Id,
            EvolutionInstance = instanceName,
            WhatsappPhone = whatsappPhone,
            Message = main,
            ScheduledAt = when,
            Status = OutboxStatus.Scheduled
        });
        count++;
        return count;
    }
}
