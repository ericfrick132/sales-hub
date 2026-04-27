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

        var instance = await _db.EvolutionInstances
            .Include(i => i.Seller)
            .FirstOrDefaultAsync(i => i.InstanceName == incoming.InstanceName, ct);
        if (instance?.Seller is null)
        {
            _log.LogWarning("Inbound message for unknown instance {I}", incoming.InstanceName);
            return false;
        }

        // Match against a lead assigned to this seller with matching phone.
        var lead = await _db.Leads
            .Where(l => l.SellerId == instance.SellerId && l.WhatsappPhone == phone)
            .OrderByDescending(l => l.CreatedAt)
            .FirstOrDefaultAsync(ct);
        if (lead is null)
        {
            // Maybe assigned to someone else; broaden search.
            lead = await _db.Leads
                .Where(l => l.WhatsappPhone == phone)
                .OrderByDescending(l => l.CreatedAt)
                .FirstOrDefaultAsync(ct);
            if (lead is null)
            {
                _log.LogInformation("Inbound message from unknown number {Phone}", phone);
                return false;
            }
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
        if (lead.FirstReplyAt is null) lead.FirstReplyAt = incoming.Timestamp;
        if (lead.Status is LeadStatus.Sent or LeadStatus.Queued or LeadStatus.Assigned)
        {
            lead.Status = LeadStatus.Replied;
        }
        lead.UpdatedAt = DateTimeOffset.UtcNow;

        await _db.SaveChangesAsync(ct);
        _log.LogInformation("Inbound msg stored: lead={Lead} text={Text}", lead.Id, incoming.Text[..Math.Min(50, incoming.Text.Length)]);
        return true;
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

    private static string? ExtractPhone(string? jid)
    {
        if (string.IsNullOrWhiteSpace(jid)) return null;
        var at = jid.IndexOf('@');
        var raw = at > 0 ? jid[..at] : jid;
        var digits = NonDigit.Replace(raw, "");
        return string.IsNullOrWhiteSpace(digits) ? null : digits;
    }
}
