using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SalesHub.Infrastructure.Options;

namespace SalesHub.Infrastructure.Apify;

/// <summary>
/// Checks Apify account usage before dispatching actor runs so we never
/// over-commit memory on the free tier. Cached for 60s so repeated calls
/// don't hammer the usage endpoint.
/// </summary>
public class ApifyUsageMonitor
{
    private readonly HttpClient _http;
    private readonly ApifyOptions _opts;
    private readonly ILogger<ApifyUsageMonitor> _log;
    private (DateTimeOffset At, Snapshot Data)? _cache;

    public ApifyUsageMonitor(HttpClient http, IOptions<ApifyOptions> opts, ILogger<ApifyUsageMonitor> log)
    {
        _http = http;
        _opts = opts.Value;
        _log = log;
    }

    public record Snapshot(int MonthlyUsageUsdCents, int MonthlyLimitUsdCents, int ActiveRuns, double Headroom);

    public async Task<Snapshot?> GetAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_opts.Token)) return null;
        if (_cache is not null && DateTimeOffset.UtcNow - _cache.Value.At < TimeSpan.FromSeconds(60))
            return _cache.Value.Data;

        try
        {
            var req = new HttpRequestMessage(HttpMethod.Get, $"{_opts.BaseUrl}/users/me/limits?token={_opts.Token}");
            using var resp = await _http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode) return null;
            using var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            if (!doc.RootElement.TryGetProperty("data", out var data)) return null;

            int usageCents = data.TryGetProperty("monthlyUsageUsd", out var u) && u.ValueKind == JsonValueKind.Number
                ? (int)(u.GetDouble() * 100) : 0;
            int limitCents = data.TryGetProperty("currentBillingPeriodMaxUsageUsd", out var l) && l.ValueKind == JsonValueKind.Number
                ? (int)(l.GetDouble() * 100) : 500; // default free tier = $5

            var runsReq = new HttpRequestMessage(HttpMethod.Get, $"{_opts.BaseUrl}/actor-runs?token={_opts.Token}&status=RUNNING&limit=50");
            using var runsResp = await _http.SendAsync(runsReq, ct);
            int active = 0;
            if (runsResp.IsSuccessStatusCode)
            {
                using var runsDoc = await JsonDocument.ParseAsync(await runsResp.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
                if (runsDoc.RootElement.TryGetProperty("data", out var rd) && rd.TryGetProperty("items", out var items))
                    active = items.GetArrayLength();
            }

            var headroom = limitCents <= 0 ? 1.0 : 1.0 - (double)usageCents / limitCents;
            var snap = new Snapshot(usageCents, limitCents, active, headroom);
            _cache = (DateTimeOffset.UtcNow, snap);
            return snap;
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Apify usage check failed");
            return null;
        }
    }

    /// <summary>Returns null if safe to run; returns a short reason string if we should abort.</summary>
    public async Task<string?> WhyNotRunAsync(int maxActiveRuns = 2, double minHeadroom = 0.15, CancellationToken ct = default)
    {
        var snap = await GetAsync(ct);
        if (snap is null) return null; // no data = let it run; fail open
        if (snap.ActiveRuns >= maxActiveRuns)
            return $"Ya hay {snap.ActiveRuns} corridas activas en Apify. Esperá a que terminen.";
        if (snap.Headroom < minHeadroom)
            return $"Apify al {(1 - snap.Headroom) * 100:F0}% del presupuesto mensual. Te queda {snap.Headroom * 100:F0}% — pausá o subí el plan.";
        return null;
    }
}
