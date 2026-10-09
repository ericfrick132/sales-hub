using System.Collections.Concurrent;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using SalesHub.Core.Domain.Entities;
using SalesHub.Infrastructure.Persistence;

namespace SalesHub.Infrastructure.Services;

/// <summary>
/// Trae los tenants de cada app (GET {Url} con X-Api-Key = Hub:ApiKey, la misma key con la
/// que las apps le pushean a sales-hub) y los guarda en app_tenants + una foto diaria en
/// app_metrics_daily. Config: TenantMetrics:Sources[] = { ProductKey, Url },
/// TenantMetrics:UsdArs (cotización para pasar ARS a USD).
/// </summary>
public class TenantMetricsService
{
    public static readonly string[] PaidStatuses = ["active", "past_due"];

    /// <summary>Resultado de la última sync por app (vive en memoria del proceso).</summary>
    public record SyncStatus(DateTimeOffset At, bool Ok, string? Error, int Tenants);
    public static readonly ConcurrentDictionary<string, SyncStatus> LastSync = new();

    private readonly HttpClient _http;
    private readonly ApplicationDbContext _db;
    private readonly IConfiguration _config;
    private readonly ILogger<TenantMetricsService> _log;

    public TenantMetricsService(HttpClient http, ApplicationDbContext db, IConfiguration config, ILogger<TenantMetricsService> log)
    {
        _http = http; _db = db; _config = config; _log = log;
        _http.Timeout = TimeSpan.FromSeconds(30);
    }

    public record Source(string ProductKey, string Url);

    public List<Source> Sources() => _config.GetSection("TenantMetrics:Sources").GetChildren()
        .Select(c => new Source(c["ProductKey"] ?? "", (c["Url"] ?? "").Trim()))
        .Where(s => s.ProductKey != "" && s.Url != "")
        .ToList();

    public decimal UsdArs => _config.GetValue<decimal?>("TenantMetrics:UsdArs") ?? 1400m;

    public decimal ToUsd(decimal amount, string currency) =>
        string.Equals(currency, "USD", StringComparison.OrdinalIgnoreCase) ? amount
        : UsdArs > 0 ? amount / UsdArs : 0;

    public async Task SyncAllAsync(CancellationToken ct)
    {
        var key = _config["Hub:ApiKey"];
        if (string.IsNullOrEmpty(key)) { _log.LogWarning("TenantMetrics: Hub:ApiKey no configurado"); return; }
        foreach (var src in Sources())
        {
            try
            {
                var n = await SyncOneAsync(src, key, ct);
                LastSync[src.ProductKey] = new SyncStatus(DateTimeOffset.UtcNow, true, null, n);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "TenantMetrics {Product}: falló la sync", src.ProductKey);
                LastSync[src.ProductKey] = new SyncStatus(DateTimeOffset.UtcNow, false, ex.Message, 0);
            }
        }
    }

    private async Task<int> SyncOneAsync(Source src, string key, CancellationToken ct)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, src.Url);
        req.Headers.Add("X-Api-Key", key);
        var resp = await _http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode) throw new InvalidOperationException($"HTTP {(int)resp.StatusCode}");
        var body = await resp.Content.ReadFromJsonAsync<TenantMetricsResponse>(cancellationToken: ct)
                   ?? throw new InvalidOperationException("respuesta vacía");

        var now = DateTimeOffset.UtcNow;
        var existing = await _db.AppTenants.Where(t => t.ProductKey == src.ProductKey)
            .ToDictionaryAsync(t => t.ExternalId, ct);
        foreach (var r in body.Tenants ?? [])
        {
            if (string.IsNullOrWhiteSpace(r.Id)) continue;
            if (!existing.TryGetValue(r.Id, out var t))
            {
                t = new AppTenant { ProductKey = src.ProductKey, ExternalId = r.Id };
                _db.AppTenants.Add(t);
                existing[r.Id] = t;
            }
            t.Name = Trunc(r.Name, 256);
            t.Status = (r.Status ?? "").Trim().ToLowerInvariant();
            t.Plan = Trunc(r.Plan, 128);
            t.MonthlyAmount = r.MonthlyAmount;
            t.Currency = string.IsNullOrWhiteSpace(r.Currency) ? "ARS" : r.Currency.Trim().ToUpperInvariant();
            t.CreatedAt = r.CreatedAt;
            t.FirstPaidAt = r.FirstPaidAt;
            t.CancelledAt = r.CancelledAt;
            t.LastSeenAt = now;
        }
        // Tenants que la app dejó de reportar (borrados) → no cuentan más.
        foreach (var gone in existing.Values.Where(t => t.LastSeenAt < now && t.Status != "deleted"))
            gone.Status = "deleted";

        var tenants = existing.Values.Where(t => t.Status != "deleted").ToList();
        var paid = tenants.Where(t => t.Status == "active").ToList();
        var date = TodayAr();
        var daily = await _db.AppMetricsDaily.FirstOrDefaultAsync(d => d.ProductKey == src.ProductKey && d.Date == date, ct);
        if (daily is null) { daily = new AppMetricsDaily { ProductKey = src.ProductKey, Date = date }; _db.AppMetricsDaily.Add(daily); }
        daily.MrrUsd = Math.Round(paid.Sum(t => ToUsd(t.MonthlyAmount, t.Currency)), 2);
        daily.Active = paid.Count;
        daily.Trial = tenants.Count(t => t.Status == "trial");
        daily.PastDue = tenants.Count(t => t.Status == "past_due");
        daily.CapturedAt = now;

        await _db.SaveChangesAsync(ct);
        return tenants.Count;
    }

    public static DateOnly TodayAr()
    {
        TimeZoneInfo tz;
        try { tz = TimeZoneInfo.FindSystemTimeZoneById("America/Argentina/Buenos_Aires"); }
        catch { tz = TimeZoneInfo.CreateCustomTimeZone("AR", TimeSpan.FromHours(-3), "AR", "AR"); }
        return DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, tz).DateTime);
    }

    private static string? Trunc(string? s, int max) => s is null ? null : s.Length <= max ? s : s[..max];

    private record TenantMetricsResponse(List<TenantRow>? Tenants);
    private record TenantRow(
        string? Id, string? Name, string? Status, string? Plan, decimal MonthlyAmount, string? Currency,
        DateTimeOffset? CreatedAt, DateTimeOffset? FirstPaidAt, DateTimeOffset? CancelledAt);
}
