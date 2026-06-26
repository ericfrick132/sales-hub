using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SalesHub.Core.Abstractions;
using SalesHub.Core.Domain.Entities;
using SalesHub.Core.Domain.Enums;
using SalesHub.Infrastructure.Persistence;

namespace SalesHub.Infrastructure.Services;

/// <summary>
/// Ingests inbound WhatsApp messages from Evolution webhooks and records outbound
/// messages sent by the UI. Matches the phone to an existing lead and updates lead
/// status to Replied so the vendor's inbox surfaces the conversation.
/// </summary>
public class ConversationService
{
    private readonly ApplicationDbContext _db;
    private readonly IEvolutionClient _evo;
    private readonly ILogger<ConversationService> _log;
    private static readonly Regex NonDigit = new(@"\D", RegexOptions.Compiled);

    public ConversationService(ApplicationDbContext db, IEvolutionClient evo, ILogger<ConversationService> log)
    {
        _db = db; _evo = evo; _log = log;
    }

    public record IncomingMessage(
        string InstanceName,
        string FromJid,
        string? FromPhone,
        string? MessageId,
        string Text,
        DateTimeOffset Timestamp,
        string RawJson);

    /// <summary>Called by the Evolution webhook on every inbound message.</summary>
    public async Task<bool> HandleIncomingAsync(IncomingMessage incoming, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(incoming.Text)) return false;
        var phone = incoming.FromPhone ?? ExtractPhone(incoming.FromJid);
        if (phone is null)
        {
            _log.LogDebug("Inbound message without resolvable phone: {Jid}", incoming.FromJid);
            return false;
        }
        // Normalizar a solo dígitos (FromPhone puede traer separadores).
        phone = NonDigit.Replace(phone, "");

        var instance = await _db.EvolutionInstances
            .Include(i => i.Seller)
            .FirstOrDefaultAsync(i => i.InstanceName == incoming.InstanceName, ct);
        if (instance?.Seller is null)
        {
            _log.LogWarning("Inbound message for unknown instance {I}", incoming.InstanceName);
            return false;
        }

        // Match tolerante: los teléfonos de los leads están guardados en formatos
        // inconsistentes (con/sin +, espacios, guiones, 0 inicial, el 9 argentino),
        // así que comparamos por los últimos 8 dígitos (el número de abonado, la
        // parte estable en todos los formatos). Primero entre los leads del seller
        // de la instancia; si no hay, ampliamos a cualquier lead.
        var suffix = phone.Length >= 8 ? phone[^8..] : phone;
        var lead = await MatchLeadByPhoneAsync(instance.SellerId, suffix, ct)
                ?? await MatchLeadByPhoneAsync(null, suffix, ct);
        if (lead is null)
        {
            // Número desconocido: ¿es un lead de anuncio (click-to-WhatsApp)? El texto
            // pre-armado del ad trae "activar <app>". Si sí, lo creamos taggeado WhatsAppAd.
            lead = await TryCreateAdLeadAsync(incoming, phone, instance, ct);
            if (lead is null)
            {
                _log.LogInformation("Inbound message from unknown number {Phone} (no es lead de anuncio)", phone);
                return false;
            }
            _log.LogInformation("Lead de anuncio creado: {Lead} app={Product} tel={Phone}", lead.Id, lead.ProductKey, phone);
        }

        // Dedup by WhatsApp message id.
        if (!string.IsNullOrWhiteSpace(incoming.MessageId))
        {
            var dupe = await _db.ConversationMessages.AnyAsync(m => m.WhatsappMessageId == incoming.MessageId, ct);
            if (dupe) return true;
        }

        _db.ConversationMessages.Add(new ConversationMessage
        {
            Id = Guid.NewGuid(),
            LeadId = lead.Id,
            SellerId = lead.SellerId,
            Direction = MessageDirection.Inbound,
            Status = MessageDeliveryStatus.Received,
            Text = incoming.Text,
            WhatsappMessageId = incoming.MessageId,
            EvolutionInstance = incoming.InstanceName,
            Timestamp = incoming.Timestamp,
            IsRead = false,
            RawJson = incoming.RawJson
        });

        // Update lead state: first reply triggers status transition.
        var isFirstReply = lead.FirstReplyAt is null;
        if (isFirstReply) lead.FirstReplyAt = incoming.Timestamp;
        if (lead.Status is LeadStatus.Sent or LeadStatus.Queued or LeadStatus.Assigned)
        {
            lead.Status = LeadStatus.Replied;
        }
        lead.UpdatedAt = DateTimeOffset.UtcNow;

        // El lead mandó algo nuevo → la sugerencia anterior (si había) quedó
        // vieja. La limpiamos para que el ConversationAgent regenere incluyendo
        // este mensaje.
        lead.AiSuggestedReply = null;
        lead.AiSuggestedReplyAt = null;

        // Cortar el drip: si el lead respondió, los siguientes steps de
        // outreach inicial ya no tienen sentido (ahora la conversación queda
        // a manos del seller). Solo cancelamos los pendientes — los que ya
        // se mandaron quedan como Sent, no se tocan.
        if (isFirstReply)
        {
            var pending = await _db.Outbox
                .Where(o => o.LeadId == lead.Id && o.Status == OutboxStatus.Scheduled)
                .ToListAsync(ct);
            foreach (var o in pending) o.Status = OutboxStatus.Cancelled;
            if (pending.Count > 0)
                _log.LogInformation("Lead {Lead} respondió — {N} steps pendientes cancelados", lead.Id, pending.Count);
        }

        await _db.SaveChangesAsync(ct);
        _log.LogInformation("Inbound msg stored: lead={Lead} text={Text}", lead.Id, incoming.Text[..Math.Min(50, incoming.Text.Length)]);
        return true;
    }

    /// <summary>
    /// Ingesta un DM entrante de Instagram (leído del inbox por el poller). Matchea el
    /// lead por InstagramHandle y aplica las mismas transiciones que el inbound de WhatsApp:
    /// marca Replied, corta el drip y limpia la sugerencia de IA vieja. Dedup por el
    /// item_id de IG (guardado en WhatsappMessageId con prefijo "ig:").
    /// </summary>
    public async Task<bool> HandleInstagramInboundAsync(
        string handle, string externalId, string text, DateTimeOffset timestamp, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        var h = handle.Trim().TrimStart('@');
        if (h.Length == 0) return false;

        // Dedup por id externo de IG.
        var extKey = string.IsNullOrWhiteSpace(externalId) ? null : $"ig:{externalId}";
        if (extKey is not null)
        {
            var dupe = await _db.ConversationMessages.AnyAsync(m => m.WhatsappMessageId == extKey, ct);
            if (dupe) return false;
        }

        var lead = await MatchLeadByInstagramHandleAsync(h, ct);
        if (lead is null)
        {
            _log.LogInformation("IG inbound de @{Handle} sin lead asociado", h);
            return false;
        }

        _db.ConversationMessages.Add(new ConversationMessage
        {
            Id = Guid.NewGuid(),
            LeadId = lead.Id,
            SellerId = lead.SellerId,
            Direction = MessageDirection.Inbound,
            Status = MessageDeliveryStatus.Received,
            Text = text,
            WhatsappMessageId = extKey,
            EvolutionInstance = null,
            Timestamp = timestamp,
            IsRead = false
        });

        var isFirstReply = lead.FirstReplyAt is null;
        if (isFirstReply) lead.FirstReplyAt = timestamp;
        if (lead.Status is LeadStatus.Sent or LeadStatus.Queued or LeadStatus.Assigned)
            lead.Status = LeadStatus.Replied;
        lead.UpdatedAt = DateTimeOffset.UtcNow;

        // El lead escribió → la sugerencia anterior quedó vieja; el ConversationAgent regenera.
        lead.AiSuggestedReply = null;
        lead.AiSuggestedReplyAt = null;

        if (isFirstReply)
        {
            var pending = await _db.Outbox
                .Where(o => o.LeadId == lead.Id && o.Status == OutboxStatus.Scheduled)
                .ToListAsync(ct);
            foreach (var o in pending) o.Status = OutboxStatus.Cancelled;
            if (pending.Count > 0)
                _log.LogInformation("Lead {Lead} respondió por IG — {N} steps pendientes cancelados", lead.Id, pending.Count);
        }

        await _db.SaveChangesAsync(ct);
        _log.LogInformation("IG inbound guardado: lead={Lead} @{Handle}", lead.Id, h);
        return true;
    }

    /// <summary>
    /// Busca el lead más reciente cuyo InstagramHandle matchea (case-insensitive,
    /// tolerando el @ inicial guardado de cualquier lado).
    /// </summary>
    private async Task<Lead?> MatchLeadByInstagramHandleAsync(string handle, CancellationToken ct)
    {
        var norm = handle.ToLower();
        return await _db.Leads
            .Where(l => l.InstagramHandle != null
                && l.InstagramHandle.ToLower().Replace("@", "") == norm)
            .OrderByDescending(l => l.CreatedAt)
            .FirstOrDefaultAsync(ct);
    }

    /// <summary>Called when the UI sends a reply manually. Does NOT go through the humanized outbox.</summary>
    public async Task<ConversationMessage?> SendReplyAsync(Guid sellerId, Guid leadId, string text, CancellationToken ct)
    {
        var lead = await _db.Leads
            .Include(l => l.Seller).ThenInclude(s => s!.EvolutionInstance)
            .FirstOrDefaultAsync(l => l.Id == leadId, ct);
        if (lead is null) return null;
        if (lead.SellerId != sellerId) return null;
        if (string.IsNullOrWhiteSpace(lead.WhatsappPhone)) return null;

        var seller = lead.Seller!;
        var instance = seller.EvolutionInstance;
        if (instance is null || instance.Status != InstanceStatus.Connected)
            throw new InvalidOperationException("Evolution instance no conectada");

        var ok = await _evo.SendTextAsync(instance.InstanceName, lead.WhatsappPhone, text, ct);
        var entry = new ConversationMessage
        {
            Id = Guid.NewGuid(),
            LeadId = lead.Id,
            SellerId = seller.Id,
            Direction = MessageDirection.Outbound,
            Status = ok ? MessageDeliveryStatus.Sent : MessageDeliveryStatus.Failed,
            Text = text,
            EvolutionInstance = instance.InstanceName,
            Timestamp = DateTimeOffset.UtcNow,
            IsRead = true
        };
        _db.ConversationMessages.Add(entry);
        lead.UpdatedAt = DateTimeOffset.UtcNow;
        // El vendedor respondió: la sugerencia ya cumplió su función.
        lead.AiSuggestedReply = null;
        lead.AiSuggestedReplyAt = null;
        await _db.SaveChangesAsync(ct);
        return entry;
    }

    public async Task MarkReadAsync(Guid sellerId, Guid leadId, CancellationToken ct)
    {
        var msgs = await _db.ConversationMessages
            .Where(m => m.LeadId == leadId && m.Direction == MessageDirection.Inbound && !m.IsRead)
            .ToListAsync(ct);
        foreach (var m in msgs)
        {
            m.IsRead = true;
            m.ReadAt = DateTimeOffset.UtcNow;
        }
        await _db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Busca el lead más reciente cuyo teléfono termina en <paramref name="suffix"/>.
    /// Si <paramref name="sellerId"/> no es null, restringe a los leads de ese
    /// seller. Los teléfonos de los leads están en formatos inconsistentes (con
    /// +, espacios, guiones, paréntesis); los strippeamos con string.Replace —
    /// que Npgsql sí traduce a SQL (Regex.Replace NO se traduce).
    /// </summary>
    private async Task<Lead?> MatchLeadByPhoneAsync(Guid? sellerId, string suffix, CancellationToken ct)
    {
        var q = _db.Leads.Where(l =>
            l.WhatsappPhone != null
            && l.WhatsappPhone
                .Replace(" ", "").Replace("-", "").Replace("+", "")
                .Replace("(", "").Replace(")", "").Replace(".", "")
                .EndsWith(suffix));
        if (sellerId is not null)
            q = q.Where(l => l.SellerId == sellerId);
        return await q.OrderByDescending(l => l.CreatedAt).FirstOrDefaultAsync(ct);
    }

    // Intención típica del texto pre-armado de un anuncio click-to-WhatsApp.
    private static readonly Regex AdIntentRx = new(
        @"activar|quiero|me gustar|me interesa|informaci[oó]n|empezar|comenzar|probar|sumar",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Si un número DESCONOCIDO escribe el texto pre-armado de un anuncio (ej.
    /// "me gustaría activar Gym Hero para mi gimnasio"), crea el lead taggeado como
    /// <see cref="LeadSource.WhatsAppAd"/>, con el producto detectado del texto y
    /// asignado al seller de la instancia (el dueño del número que recibió el ad).
    /// Devuelve null si no parece un lead de anuncio (no creamos nada).
    /// </summary>
    private async Task<Lead?> TryCreateAdLeadAsync(IncomingMessage incoming, string phone, EvolutionInstance instance, CancellationToken ct)
    {
        if (!AdIntentRx.IsMatch(incoming.Text)) return null;

        var lower = incoming.Text.ToLowerInvariant();
        var compact = lower.Replace(" ", "");
        var products = await _db.Products.Where(p => p.Active).ToListAsync(ct);
        // OJO: guardas de longitud mínima. Un producto con ProductKey vacío hace que
        // compact.Contains("") sea SIEMPRE true → matchearía primero y dejaría el lead sin
        // producto (rompía el onboarding). Exigimos key/displayname con largo real.
        var product = products.FirstOrDefault(p =>
            (p.ProductKey.Length >= 2 && compact.Contains(p.ProductKey.ToLowerInvariant()))
            || (p.DisplayName.Length >= 3 && lower.Contains(p.DisplayName.ToLowerInvariant())));
        if (product is null) return null; // no sabemos de qué app → no creamos un lead mal taggeado

        var lead = new Lead
        {
            Id = Guid.NewGuid(),
            ProductKey = product.ProductKey,
            Source = LeadSource.WhatsAppAd,
            Name = ExtractPushName(incoming.RawJson) ?? "Lead de anuncio",
            WhatsappPhone = phone,
            WhatsappValidated = true,
            SellerId = instance.SellerId,
            AssignedAt = DateTimeOffset.UtcNow,
            Status = LeadStatus.Replied,        // ya escribió ellos
            FirstReplyAt = incoming.Timestamp,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        _db.Leads.Add(lead);
        return lead;
    }

    /// <summary>Nombre del contacto que manda Evolution en el payload (pushName), o null.</summary>
    private static string? ExtractPushName(string rawJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(rawJson);
            if (doc.RootElement.TryGetProperty("pushName", out var pn) && pn.ValueKind == JsonValueKind.String)
            {
                var name = pn.GetString();
                return string.IsNullOrWhiteSpace(name) ? null : name!.Trim();
            }
        }
        catch { /* no-op */ }
        return null;
    }

    private static string? ExtractPhone(string? jid)
    {
        if (string.IsNullOrWhiteSpace(jid)) return null;
        var at = jid.IndexOf('@');
        var raw = at > 0 ? jid[..at] : jid;
        var digits = NonDigit.Replace(raw, "");
        return string.IsNullOrWhiteSpace(digits) ? null : digits;
    }
}
