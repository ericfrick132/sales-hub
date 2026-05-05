using SalesHub.Core.Abstractions;
using SalesHub.Core.Domain.Entities;
using SalesHub.Core.Domain.Enums;
using SalesHub.Infrastructure.Persistence;

namespace SalesHub.Infrastructure.Services;

/// <summary>
/// Centraliza el enqueue del drip de outreach inicial.
///
/// Si el producto tiene <see cref="Product.MessageSteps"/> configurado, encola
/// todos los steps en orden con su DelaySeconds acumulado. Si no, fallback al
/// modelo legacy (OpenerTemplate + MessageTemplate) para no romper productos
/// viejos que todavía no migraron al editor de steps.
///
/// Los steps se cancelan apenas el lead responde — eso lo hace
/// <see cref="ConversationService"/> al persistir el inbound.
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
        var count = 0;

        if (product.MessageSteps is { Count: > 0 })
        {
            // Modelo nuevo: cada step se renderiza con los placeholders del
            // producto (mismo motor que MessageTemplate). El primero usa
            // RenderedMessage si lo tenemos pre-rendereado; el resto se
            // renderiza ad-hoc desde el template del step.
            for (var i = 0; i < product.MessageSteps.Count; i++)
            {
                var step = product.MessageSteps[i];
                // Un step sin texto Y sin media no manda nada — lo skipeamos.
                // Si tiene media, va aunque el texto esté vacío (sin caption).
                if (string.IsNullOrWhiteSpace(step.Text) && step.MediaAssetId is null) continue;

                if (i > 0) when = when.AddSeconds(Math.Max(0, step.DelaySeconds));
                var rendered = i == 0 && !string.IsNullOrWhiteSpace(lead.RenderedMessage) && step.MediaAssetId is null
                    ? lead.RenderedMessage!
                    : renderer.RenderTemplate(step.Text, lead, product, seller);

                db.Outbox.Add(new MessageOutbox
                {
                    Id = Guid.NewGuid(),
                    LeadId = lead.Id,
                    SellerId = seller.Id,
                    EvolutionInstance = instanceName,
                    WhatsappPhone = whatsappPhone,
                    Message = rendered,
                    MediaAssetId = step.MediaAssetId,
                    ScheduledAt = when,
                    Status = OutboxStatus.Scheduled
                });
                count++;
                // +1s para garantizar orden estable entre steps adyacentes
                // que comparten el mismo ScheduledAt (delay 0).
                when = when.AddSeconds(1);
            }
            return count;
        }

        // ─── Fallback legacy: opener + main ───────────────────────────────
        var opener = renderer.RenderOpener(lead, product, seller);
        var main = !string.IsNullOrWhiteSpace(lead.RenderedMessage)
            ? lead.RenderedMessage!
            : renderer.Render(lead, product, seller);

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
