using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SalesHub.Core.Domain.Entities;
using SalesHub.Infrastructure.Options;
using SalesHub.Infrastructure.Persistence;

namespace SalesHub.Infrastructure.Apify;

public class WebsiteCrawlerEnricher
{
    private static readonly Regex EmailRx = new(@"[\w.\-+]+@[\w.\-]+\.[A-Za-z]{2,}", RegexOptions.Compiled);
    private static readonly Regex PhoneRx = new(@"(\+?\d[\d\s\-\(\)]{7,}\d)", RegexOptions.Compiled);
    private readonly ApifyHttpClient _client;
    private readonly ApifyOptions _opts;
    private readonly ApplicationDbContext _db;
    private readonly ILogger<WebsiteCrawlerEnricher> _log;

    public WebsiteCrawlerEnricher(ApifyHttpClient client, IOptions<ApifyOptions> opts, ApplicationDbContext db, ILogger<WebsiteCrawlerEnricher> log)
    {
        _client = client; _opts = opts.Value; _db = db; _log = log;
    }

    public async Task<Lead?> EnrichAsync(Guid leadId, CancellationToken ct)
    {
        var lead = _db.Leads.FirstOrDefault(l => l.Id == leadId);
        if (lead is null || string.IsNullOrWhiteSpace(lead.Website)) return null;

        var input = new
        {
            startUrls = new[] { new { url = lead.Website } },
            maxCrawlPages = 3,
            maxCrawlDepth = 1
        };
        var items = await _client.RunActorSyncAsync(_opts.WebsiteCrawler.ActorId, input, _opts.RunTimeoutSeconds, ct);
        if (items.Length == 0) return lead;

        var text = string.Join("\n", items.Select(i =>
            i.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : string.Empty));
        var email = EmailRx.Match(text).Value;
        var phone = PhoneRx.Match(text).Value;

        if (string.IsNullOrWhiteSpace(lead.RawPhone) && !string.IsNullOrWhiteSpace(phone)) lead.RawPhone = phone;
        if (!string.IsNullOrWhiteSpace(email))
        {
            lead.Notes = (lead.Notes is null ? "" : lead.Notes + "\n") + $"Email (website): {email}";
        }
        lead.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);
        _log.LogInformation("Website enrichment for {Id}: email={Email} phone={Phone}", lead.Id, email, phone);
        return lead;
    }
}
