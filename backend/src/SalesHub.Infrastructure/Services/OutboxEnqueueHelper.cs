using Microsoft.EntityFrameworkCore;
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
                var hasMedia = step.MediaAssetId is not null || (step.MediaAssetIds is { Count: > 0 });
                // Un step sin texto Y sin media no manda nada — lo skipeamos.
                // Si tiene media, va aunque el texto esté vacío (sin caption).
                if (string.IsNullOrWhiteSpace(step.Text) && !hasMedia) continue;

                if (i > 0) when = when.AddSeconds(Math.Max(0, step.DelaySeconds));
                var rendered = i == 0 && !string.IsNullOrWhiteSpace(lead.RenderedMessage) && !hasMedia
                    ? lead.RenderedMessage!
                    : renderer.RenderTemplate(step.Text, lead, product, seller);

                // Rotación de variantes: si hay > 1 audio, elegimos round-robin.
                // El counter es atómico vía UPSERT con RETURNING en Postgres
                // para evitar race entre dos enqueues concurrentes del mismo
                // step (ej. dos sellers asignados al mismo lead en paralelo).
                Guid? mediaAssetId = step.MediaAssetId;
                if (step.MediaAssetIds is { Count: > 0 })
                {
                    var ids = step.MediaAssetIds;
                    int chosenIdx;
                    if (ids.Count == 1)
                    {
                        chosenIdx = 0;
                    }
                    else
                    {
                        chosenIdx = NextRotationIndex(db, product.Id, i, ids.Count);
                    }
                    mediaAssetId = ids[chosenIdx];
                }

                db.Outbox.Add(new MessageOutbox
                {
                    Id = Guid.NewGuid(),
                    LeadId = lead.Id,
                    SellerId = seller.Id,
                    EvolutionInstance = instanceName,
                    WhatsappPhone = whatsappPhone,
                    Message = rendered,
                    MediaAssetId = mediaAssetId,
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

    /// <summary>
    /// UPSERT atómico con RETURNING para round-robin entre variantes. El
    /// índice devuelto es el que hay que usar AHORA; la próxima llamada va a
    /// devolver el siguiente módulo N. Primera vez que existe la tupla
    /// (productId, stepIndex) inserta con last_index = 0 y devuelve 0.
    /// </summary>
    private static int NextRotationIndex(ApplicationDbContext db, Guid productId, int stepIndex, int variantCount)
    {
        const string sql = @"
INSERT INTO message_step_rotations (product_id, step_index, last_index, updated_at)
VALUES ({0}, {1}, 0, now())
ON CONFLICT (product_id, step_index) DO UPDATE
SET last_index = (message_step_rotations.last_index + 1) % {2},
    updated_at = now()
RETURNING last_index;";
        var conn = db.Database.GetDbConnection();
        var opened = false;
        try
        {
            if (conn.State != System.Data.ConnectionState.Open) { conn.Open(); opened = true; }
            using var cmd = conn.CreateCommand();
            cmd.CommandText = string.Format(sql.Replace("{0}", "@p0").Replace("{1}", "@p1").Replace("{2}", "@p2"));
            var p0 = cmd.CreateParameter(); p0.ParameterName = "@p0"; p0.Value = productId; cmd.Parameters.Add(p0);
            var p1 = cmd.CreateParameter(); p1.ParameterName = "@p1"; p1.Value = stepIndex; cmd.Parameters.Add(p1);
            var p2 = cmd.CreateParameter(); p2.ParameterName = "@p2"; p2.Value = variantCount; cmd.Parameters.Add(p2);
            var result = cmd.ExecuteScalar();
            return result is null ? 0 : Convert.ToInt32(result);
        }
        finally
        {
            if (opened) conn.Close();
        }
    }
}
