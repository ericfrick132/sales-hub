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
        DateTimeOffset? scheduledAt = null,
        MessageChannel channel = MessageChannel.WhatsApp)
    {
        var when = scheduledAt ?? DateTimeOffset.UtcNow;
        var count = 0;

        // Instagram DM = solo texto: no mandamos media (el cliente IG no la soporta),
        // así que skipeamos steps sin texto y no adjuntamos assets.
        var textOnly = channel == MessageChannel.Instagram;

        // Leads CALIENTES (app-fed / anuncios, source >= 400: ya nos conocen o dejaron sus
        // datos) saltan la fila del outreach frío scrapeado. Sin esto, un lead de formulario
        // espera DÍAS detrás de miles de fríos en la cola FIFO y se enfría — se pierde.
        var priority = (int)lead.Source >= 400 ? 70 : 50;

        // Resolver qué cadencia usar para este lead (override por categoría
        // o default del producto).
        var (steps, cadenceCategory) = ResolveStepsForLead(lead, product);
        if (steps is { Count: > 0 })
        {
            // Modelo nuevo: cada step se renderiza con los placeholders del
            // producto (mismo motor que MessageTemplate). El primero usa
            // RenderedMessage si lo tenemos pre-rendereado; el resto se
            // renderiza ad-hoc desde el template del step.
            for (var i = 0; i < steps.Count; i++)
            {
                var step = steps[i];
                var hasMedia = !textOnly && (step.MediaAssetId is not null || (step.MediaAssetIds is { Count: > 0 }));
                // Un step sin texto Y sin media no manda nada — lo skipeamos.
                // Si tiene media, va aunque el texto esté vacío (sin caption).
                // En IG (textOnly) un step sin texto se skipea aunque tenga media.
                if (string.IsNullOrWhiteSpace(step.Text) && !hasMedia) continue;

                if (i > 0) when = when.AddSeconds(Math.Max(0, step.DelaySeconds));
                // El shortcut de RenderedMessage (pre-render legacy de assign-time) solo vale
                // para la cadencia DEFAULT: en overrides (origen/categoría) el copy es otro, y
                // los productos relay (/hub/outbound) mandan este snapshot SIN re-render — acá
                // pisar el step 0 con el template frío re-introduciría el bug de Meta Lead Ads.
                var rendered = i == 0 && cadenceCategory.Length == 0
                    && !string.IsNullOrWhiteSpace(lead.RenderedMessage) && !hasMedia
                    ? lead.RenderedMessage!
                    : renderer.RenderTemplate(step.Text, lead, product, seller);

                // Snapshot del media: si hay variantes, dejamos la primera. La rotación
                // round-robin real se decide en OutboxSender al momento de mandar — así
                // sobrevive a cambios de variantes entre enqueue y envío (agregar/sacar
                // audios) y no consumimos el contador dos veces (enqueue + send).
                Guid? mediaAssetId = textOnly ? null : step.MediaAssetId;
                if (!textOnly && step.MediaAssetIds is { Count: > 0 })
                {
                    mediaAssetId = step.MediaAssetIds[0];
                }

                db.Outbox.Add(new MessageOutbox
                {
                    Id = Guid.NewGuid(),
                    LeadId = lead.Id,
                    SellerId = seller.Id,
                    Channel = channel,
                    EvolutionInstance = instanceName,
                    WhatsappPhone = whatsappPhone,
                    // Snapshot al momento del enqueue — útil como preview/debug pero el
                    // sender va a re-renderizar desde la config del producto al momento de
                    // mandar (ver OutboxSender). Si la cadencia cambia entre enqueue y
                    // envío, sale lo nuevo.
                    Message = rendered,
                    MediaAssetId = mediaAssetId,
                    StepIndex = i,
                    CadenceCategory = cadenceCategory,
                    Priority = priority,
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
                Channel = channel,
                EvolutionInstance = instanceName,
                WhatsappPhone = whatsappPhone,
                Message = opener,
                Priority = priority,
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
            Channel = channel,
            EvolutionInstance = instanceName,
            WhatsappPhone = whatsappPhone,
            Message = main,
            Priority = priority,
            ScheduledAt = when,
            Status = OutboxStatus.Scheduled
        });
        count++;
        return count;
    }

    /// <summary>
    /// Encola un mensaje del bot de onboarding (ej. intro + 1ª pregunta) como filas de TEXTO
    /// en el MISMO outbox que usa el drip — mismo transporte (directo por OutboxSender o relay
    /// por /hub/outbound) y SIN audio. Splittea por [NUEVO_MENSAJE] igual que OnboardingSendAsync.
    /// StepIndex queda null: el sender manda el snapshot tal cual, no re-resuelve cadencia.
    ///
    /// Se usa para leads que arrancan DERECHO en el onboarding (ej. Meta Lead Ads, que ya
    /// completaron un formulario) en vez del drip de venta con nota de voz.
    /// </summary>
    public static int EnqueueOnboardingText(
        ApplicationDbContext db,
        Lead lead,
        Seller seller,
        string whatsappPhone,
        string instanceName,
        string text,
        DateTimeOffset? scheduledAt = null)
    {
        var parts = text.Split(new[] { "[NUEVO_MENSAJE]" }, StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Trim())
            .Where(p => p.Length > 0)
            .ToList();
        var when = scheduledAt ?? DateTimeOffset.UtcNow;
        var count = 0;
        foreach (var p in parts)
        {
            db.Outbox.Add(new MessageOutbox
            {
                Id = Guid.NewGuid(),
                LeadId = lead.Id,
                SellerId = seller.Id,
                Channel = MessageChannel.WhatsApp,
                EvolutionInstance = instanceName,
                WhatsappPhone = whatsappPhone,
                Message = p,
                // Onboarding = lead caliente de anuncio/form: sale ANTES que el outreach frío
                // (a FIFO pura esperaba días en la cola y llegaba helado).
                Priority = 80,
                ScheduledAt = when,
                Status = OutboxStatus.Scheduled
            });
            count++;
            // +1s para orden estable entre partes que comparten ScheduledAt.
            when = when.AddSeconds(1);
        }
        return count;
    }

    /// <summary>
    /// Prefijo que identifica una cadencia por origen en CadenceCategory y en la
    /// rotación de variantes (ej. "origen:MetaLeadAd"). Los ":" no aparecen en
    /// categorías de búsqueda reales, así que no colisiona.
    /// </summary>
    public const string SourceCadencePrefix = "origen:";

    /// <summary>
    /// Devuelve los steps efectivos a usar para este lead. Precedencia:
    /// 1) override por ORIGEN (lead.Source, ej. MetaLeadAd — el lead ya nos
    ///    dejó sus datos, el opener frío no aplica), 2) override por categoría
    ///    de búsqueda, 3) MessageSteps default del producto. También retorna
    /// la "categoría" lógica para identificar la cadencia en la rotación
    /// (vacío = default, "origen:X" = cadencia por origen).
    /// </summary>
    public static (List<MessageStep> steps, string cadenceCategory) ResolveStepsForLead(Lead lead, Product product)
    {
        if (product.SourceCadences is { Count: > 0 })
        {
            var srcKey = lead.Source.ToString();
            var bySource = product.SourceCadences.FirstOrDefault(
                c => string.Equals(c.Source, srcKey, StringComparison.OrdinalIgnoreCase));
            if (bySource is not null && bySource.Steps is { Count: > 0 })
                return (bySource.Steps, SourceCadencePrefix + bySource.Source);
        }

        var leadCat = (lead.SearchCategory ?? string.Empty).Trim();
        if (!string.IsNullOrWhiteSpace(leadCat) && product.CategoryCadences is { Count: > 0 })
        {
            var match = product.CategoryCadences.FirstOrDefault(
                c => string.Equals(c.Category, leadCat, StringComparison.OrdinalIgnoreCase));
            if (match is not null && match.Steps is { Count: > 0 })
                return (match.Steps, match.Category);
        }
        return (product.MessageSteps ?? new(), string.Empty);
    }

    /// <summary>
    /// UPSERT atómico con RETURNING para round-robin entre variantes. El
    /// índice devuelto es el que hay que usar AHORA; la próxima llamada va a
    /// devolver el siguiente módulo N. PK compuesta (productId, category,
    /// stepIndex) para que cada cadencia rote independiente.
    /// </summary>
    public static int NextRotationIndex(ApplicationDbContext db, Guid productId, string category, int stepIndex, int variantCount)
    {
        // Postgres trata "" y NULL distinto en ON CONFLICT; normalizamos a "".
        var cat = category ?? string.Empty;
        const string sql = @"
INSERT INTO message_step_rotations (product_id, category, step_index, last_index, updated_at)
VALUES (@p0, @p1, @p2, 0, now())
ON CONFLICT (product_id, category, step_index) DO UPDATE
SET last_index = (message_step_rotations.last_index + 1) % @p3,
    updated_at = now()
RETURNING last_index;";
        var conn = db.Database.GetDbConnection();
        var opened = false;
        try
        {
            if (conn.State != System.Data.ConnectionState.Open) { conn.Open(); opened = true; }
            using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            var p0 = cmd.CreateParameter(); p0.ParameterName = "@p0"; p0.Value = productId; cmd.Parameters.Add(p0);
            var p1 = cmd.CreateParameter(); p1.ParameterName = "@p1"; p1.Value = cat; cmd.Parameters.Add(p1);
            var p2 = cmd.CreateParameter(); p2.ParameterName = "@p2"; p2.Value = stepIndex; cmd.Parameters.Add(p2);
            var p3 = cmd.CreateParameter(); p3.ParameterName = "@p3"; p3.Value = variantCount; cmd.Parameters.Add(p3);
            var result = cmd.ExecuteScalar();
            return result is null ? 0 : Convert.ToInt32(result);
        }
        finally
        {
            if (opened) conn.Close();
        }
    }
}
