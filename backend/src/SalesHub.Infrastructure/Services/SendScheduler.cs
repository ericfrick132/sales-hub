using Microsoft.EntityFrameworkCore;
using SalesHub.Core.Abstractions;
using SalesHub.Core.Domain.Entities;
using SalesHub.Core.Domain.Enums;
using SalesHub.Infrastructure.Persistence;

namespace SalesHub.Infrastructure.Services;

/// <summary>
/// Humanized scheduler: computes when the next outbox item for a seller should fire,
/// enforcing active hours, warmup ramp, daily cap with variance, inter-message jitter
/// and burst pauses. Uses a deterministic RNG seeded by (sellerId + date) so
/// skip-day / daily cap are stable across restarts.
/// </summary>
public class SendScheduler : ISendScheduler
{
    private readonly ApplicationDbContext _db;

    public SendScheduler(ApplicationDbContext db) { _db = db; }

    public bool IsSkipDay(Seller seller, DateOnly today)
    {
        if (seller.SkipDayProbabilityPct <= 0) return false;
        var rng = Deterministic(seller.Id, today, "skip");
        return rng.Next(100) < seller.SkipDayProbabilityPct;
    }

    public int ComputeTodayCap(Seller seller, DateOnly today)
    {
        // Warmup ramp: day 1 = ~1/3 cap, linearly up to cap at WarmupDays.
        var warmupStart = seller.WarmupStartedAt is null ? today : DateOnly.FromDateTime(seller.WarmupStartedAt.Value.UtcDateTime);
        var daysIn = (today.DayNumber - warmupStart.DayNumber) + 1;
        var baseCap = seller.DailyCap;
        int cap = baseCap;
        if (seller.WarmupDays > 0 && daysIn <= seller.WarmupDays)
        {
            var fraction = (double)daysIn / seller.WarmupDays;
            var floor = Math.Max(5, (int)Math.Round(baseCap * 0.3));
            cap = Math.Max(floor, (int)Math.Round(baseCap * fraction));
        }

        // Variance ±pct, deterministic per day.
        if (seller.DailyVariancePct > 0)
        {
            var rng = Deterministic(seller.Id, today, "cap");
            var drift = (rng.NextDouble() * 2 - 1) * seller.DailyVariancePct / 100.0;
            cap = (int)Math.Round(cap * (1 + drift));
        }
        return Math.Max(1, cap);
    }

    public async Task<DateTimeOffset?> ComputeNextScheduleAsync(Seller seller, DateTimeOffset reference, CancellationToken ct = default)
    {
        var tz = SafeTz(seller.Timezone);
        var localRef = TimeZoneInfo.ConvertTime(reference, tz);
        var today = DateOnly.FromDateTime(localRef.DateTime);

        if (IsSkipDay(seller, today))
            return NextDayStart(seller, today, tz);

        var cap = ComputeTodayCap(seller, today);
        var sentToday = await _db.Outbox
            .CountAsync(o => o.SellerId == seller.Id
                          && o.Status == OutboxStatus.Sent
                          && o.SentAt != null
                          && o.SentAt.Value >= today.ToDateTime(TimeOnly.MinValue)
                          && o.SentAt.Value < today.AddDays(1).ToDateTime(TimeOnly.MinValue), ct);

        if (sentToday >= cap)
            return NextDayStart(seller, today, tz);

        // Active hours window
        var dayStart = today.ToDateTime(new TimeOnly(seller.ActiveHoursStart, 0));
        var dayEnd = today.ToDateTime(new TimeOnly(seller.ActiveHoursEnd, 0));
        var localNow = localRef.DateTime;

        var earliest = localNow < dayStart ? dayStart : localNow;
        if (earliest >= dayEnd)
            return NextDayStart(seller, today, tz);

        // Inter-message jitter + burst pauses
        var rng = new Random(HashCombine(seller.Id, reference.UtcTicks));
        var delaySec = rng.Next(seller.DelayMinSeconds, seller.DelayMaxSeconds + 1);

        // Burst: if we already sent a full burst in the last 30 min, add burst pause
        var burstWindow = TimeSpan.FromMinutes(30);
        var recentSent = await _db.Outbox
            .Where(o => o.SellerId == seller.Id && o.SentAt != null && o.SentAt > reference.Subtract(burstWindow))
            .OrderByDescending(o => o.SentAt)
            .Take(seller.BurstSize + 1)
            .CountAsync(ct);
        if (recentSent >= seller.BurstSize)
        {
            var pause = rng.Next(seller.BurstPauseMinSeconds, seller.BurstPauseMaxSeconds + 1);
            delaySec = Math.Max(delaySec, pause);
        }

        var localNext = earliest.AddSeconds(delaySec);
        if (localNext >= dayEnd)
            return NextDayStart(seller, today, tz);

        return TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(localNext, DateTimeKind.Unspecified), tz);
    }

    private static DateTimeOffset NextDayStart(Seller seller, DateOnly today, TimeZoneInfo tz)
    {
        var nextLocal = today.AddDays(1).ToDateTime(new TimeOnly(seller.ActiveHoursStart, 0));
        return TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(nextLocal, DateTimeKind.Unspecified), tz);
    }

    private static Random Deterministic(Guid sellerId, DateOnly date, string tag)
    {
        unchecked
        {
            var hash = sellerId.GetHashCode();
            hash = (hash * 397) ^ date.DayNumber;
            hash = (hash * 397) ^ tag.GetHashCode();
            return new Random(hash);
        }
    }

    private static int HashCombine(Guid id, long ticks)
    {
        unchecked
        {
            var hash = id.GetHashCode();
            hash = (hash * 397) ^ ticks.GetHashCode();
            return hash;
        }
    }

    private static TimeZoneInfo SafeTz(string id)
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
        catch { return TimeZoneInfo.Utc; }
    }
}
