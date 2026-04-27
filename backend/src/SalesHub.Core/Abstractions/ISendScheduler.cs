using SalesHub.Core.Domain.Entities;

namespace SalesHub.Core.Abstractions;

public interface ISendScheduler
{
    /// <summary>
    /// Calculates the next scheduled time for a seller, respecting humanization parameters
    /// (active hours, delays, bursts, warmup, skip-day probability).
    /// </summary>
    Task<DateTimeOffset?> ComputeNextScheduleAsync(Seller seller, DateTimeOffset reference, CancellationToken ct = default);

    /// <summary>
    /// Returns the daily cap for a seller today after applying warmup + variance.
    /// </summary>
    int ComputeTodayCap(Seller seller, DateOnly today);

    /// <summary>
    /// Deterministically returns true if today is a skip-day for this seller (rolled at midnight).
    /// </summary>
    bool IsSkipDay(Seller seller, DateOnly today);
}
