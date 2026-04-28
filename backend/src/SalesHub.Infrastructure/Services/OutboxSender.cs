using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SalesHub.Core.Abstractions;
using SalesHub.Core.Domain.Entities;
using SalesHub.Core.Domain.Enums;
using SalesHub.Infrastructure.Persistence;

namespace SalesHub.Infrastructure.Services;

/// <summary>
/// Picks at most one scheduled outbox item per seller per tick, enforces humanization,
/// sends via Evolution, updates Lead + SellerDailyStats.
/// </summary>
public class OutboxSender
{
    private readonly ApplicationDbContext _db;
    private readonly IEvolutionClient _evo;
    private readonly ISendScheduler _scheduler;
    private readonly ILogger<OutboxSender> _log;

    public OutboxSender(ApplicationDbContext db, IEvolutionClient evo, ISendScheduler scheduler, ILogger<OutboxSender> log)
    {
        _db = db; _evo = evo; _scheduler = scheduler; _log = log;
    }

    public async Task<int> TickAsync(CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        // Cualquier usuario con SendingEnabled + WhatsApp conectado envía, sea Seller o Admin.
        // El Admin que conecta su WhatsApp y prende el switch debe poder mandar como un vendedor más.
        var sellers = await _db.Sellers
            .Include(s => s.EvolutionInstance)
            .Where(s => s.IsActive && s.SendingEnabled)
            .ToListAsync(ct);

        var sent = 0;
        foreach (var seller in sellers)
        {
            if (seller.EvolutionInstance is null || seller.EvolutionInstance.Status != InstanceStatus.Connected) continue;

            var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, SafeTz(seller.Timezone)).DateTime);
            if (_scheduler.IsSkipDay(seller, today)) continue;

            var cap = _scheduler.ComputeTodayCap(seller, today);
            var sentToday = await _db.Outbox
                .CountAsync(o => o.SellerId == seller.Id
                              && o.Status == OutboxStatus.Sent
                              && o.SentAt != null
                              && o.SentAt.Value >= today.ToDateTime(TimeOnly.MinValue)
                              && o.SentAt.Value < today.AddDays(1).ToDateTime(TimeOnly.MinValue), ct);
            if (sentToday >= cap) continue;

            // Enforce active hours window.
            var local = TimeZoneInfo.ConvertTime(now, SafeTz(seller.Timezone)).DateTime;
            if (local.Hour < seller.ActiveHoursStart || local.Hour >= seller.ActiveHoursEnd) continue;

            var next = await _db.Outbox
                .Where(o => o.SellerId == seller.Id
                         && o.Status == OutboxStatus.Scheduled
                         && o.ScheduledAt <= now)
                .OrderBy(o => o.ScheduledAt)
                .FirstOrDefaultAsync(ct);
            if (next is null) continue;

            next.Status = OutboxStatus.Sending;
            next.LockedAt = now;
            next.Attempts++;
            await _db.SaveChangesAsync(ct);

            try
            {
                if (seller.ReadIncomingFirst)
                {
                    await _evo.MarkAllChatsReadAsync(seller.EvolutionInstance.InstanceName, ct);
                }

                var typing = Random.Shared.Next(seller.PreSendTypingMinSeconds, seller.PreSendTypingMaxSeconds + 1);
                var jid = $"{next.WhatsappPhone}@s.whatsapp.net";
                await _evo.SetPresenceTypingAsync(seller.EvolutionInstance.InstanceName, jid, typing, ct);
                await Task.Delay(TimeSpan.FromSeconds(typing), ct);

                var ok = await _evo.SendTextAsync(seller.EvolutionInstance.InstanceName, next.WhatsappPhone, next.Message, ct);
                if (!ok) throw new InvalidOperationException("Evolution rejected the send");

                next.Status = OutboxStatus.Sent;
                next.SentAt = DateTimeOffset.UtcNow;

                var lead = await _db.Leads.FirstOrDefaultAsync(l => l.Id == next.LeadId, ct);
                if (lead is not null)
                {
                    lead.Status = LeadStatus.Sent;
                    lead.SentAt = DateTimeOffset.UtcNow;
                }

                // Record the outbound message in the conversation thread so the seller
                // sees their initial message in /conversations.
                _db.ConversationMessages.Add(new ConversationMessage
                {
                    Id = Guid.NewGuid(),
                    LeadId = next.LeadId,
                    SellerId = next.SellerId,
                    Direction = MessageDirection.Outbound,
                    Status = MessageDeliveryStatus.Sent,
                    Text = next.Message,
                    EvolutionInstance = next.EvolutionInstance,
                    Timestamp = DateTimeOffset.UtcNow,
                    IsRead = true
                });

                var statsKey = new { SellerId = seller.Id, Date = today };
                var stats = await _db.DailyStats.FindAsync(new object[] { statsKey.SellerId, statsKey.Date }, ct);
                if (stats is null)
                {
                    stats = new SellerDailyStats { SellerId = seller.Id, Date = today, PlannedCap = cap };
                    _db.DailyStats.Add(stats);
                }
                stats.MessagesSent++;
                await _db.SaveChangesAsync(ct);
                sent++;

                _log.LogInformation("Sent to {Phone} from seller {Seller} ({Instance})", next.WhatsappPhone, seller.DisplayName, seller.EvolutionInstance.InstanceName);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Send failed for outbox {Id}", next.Id);
                next.Status = next.Attempts >= 3 ? OutboxStatus.Failed : OutboxStatus.Scheduled;
                next.Error = ex.Message;
                if (next.Status == OutboxStatus.Scheduled)
                {
                    // Retry later (random 5-15m).
                    next.ScheduledAt = DateTimeOffset.UtcNow.AddMinutes(Random.Shared.Next(5, 16));
                }
                await _db.SaveChangesAsync(ct);
            }
        }
        return sent;
    }

    private static TimeZoneInfo SafeTz(string id)
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
        catch { return TimeZoneInfo.Utc; }
    }
}
