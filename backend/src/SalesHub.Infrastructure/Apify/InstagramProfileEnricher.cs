using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SalesHub.Core.Domain.Entities;
using SalesHub.Infrastructure.Options;
using SalesHub.Infrastructure.Persistence;

namespace SalesHub.Infrastructure.Apify;

public class InstagramProfileEnricher
{
    private static readonly Regex PhoneRx = new(@"(\+?\d[\d\s\-\(\)]{7,}\d)", RegexOptions.Compiled);
    private readonly ApifyHttpClient _client;
    private readonly ApifyOptions _opts;
    private readonly ApplicationDbContext _db;
    private readonly ILogger<InstagramProfileEnricher> _log;

    public InstagramProfileEnricher(ApifyHttpClient client, IOptions<ApifyOptions> opts, ApplicationDbContext db, ILogger<InstagramProfileEnricher> log)
    {
        _client = client; _opts = opts.Value; _db = db; _log = log;
    }

    public async Task<Lead?> EnrichAsync(Guid leadId, CancellationToken ct)
    {
        var lead = _db.Leads.FirstOrDefault(l => l.Id == leadId);
        if (lead is null || string.IsNullOrWhiteSpace(lead.InstagramHandle)) return null;

        var input = new { usernames = new[] { lead.InstagramHandle } };
        var items = await _client.RunActorSyncAsync(_opts.InstagramProfile.ActorId, input, _opts.RunTimeoutSeconds, ct);
        if (items.Length == 0) return lead;

        var profile = items[0];
        var bio = GetStr(profile, "biography");
        var website = GetStr(profile, "externalUrl") ?? GetStr(profile, "website");
        var phone = GetStr(profile, "businessPhoneNumber") ?? (bio is null ? null : PhoneRx.Match(bio).Value);

        if (!string.IsNullOrWhiteSpace(website)) lead.Website ??= website;
        if (!string.IsNullOrWhiteSpace(phone) && string.IsNullOrWhiteSpace(lead.RawPhone)) lead.RawPhone = phone;
        lead.RawDataJson = profile.GetRawText();
        lead.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);
        _log.LogInformation("Enriched lead {Id} from IG profile @{Handle}", lead.Id, lead.InstagramHandle);
        return lead;
    }

    private static string? GetStr(JsonElement e, string p)
        => e.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
