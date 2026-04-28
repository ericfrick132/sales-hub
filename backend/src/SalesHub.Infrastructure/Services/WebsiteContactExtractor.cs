using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using SalesHub.Core.Abstractions;

namespace SalesHub.Infrastructure.Services;

/// <summary>
/// Fetches a business website and extracts a contact phone, Instagram handle, and Facebook URL
/// using regex/heuristics. No browser, just plain HTTP — fast and cheap. Used as a fallback
/// when Google Places didn't return a phone for the lead.
/// </summary>
public class WebsiteContactExtractor : IWebsiteContactExtractor
{
    private static readonly Regex PhoneRegex = new(
        @"(\+?54[\s\-\.]?9?[\s\-\.]?\(?\d{2,4}\)?[\s\-\.]?\d{3,4}[\s\-\.]?\d{4})|(\(?0?\d{2,4}\)?[\s\-\.]?\d{3,4}[\s\-\.]?\d{4})",
        RegexOptions.Compiled);

    private static readonly Regex WhatsappLinkRegex = new(
        @"(?:wa\.me/|api\.whatsapp\.com/send\?phone=|whatsapp\.com/send/?\?phone=)(\+?\d{8,15})",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex TelLinkRegex = new(
        @"href=[""']tel:([+\d\s\-\.\(\)]{7,})[""']",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex InstagramRegex = new(
        @"(?:instagram\.com/|@)([A-Za-z0-9_\.]{2,30})",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex FacebookRegex = new(
        @"https?://(?:www\.|m\.)?facebook\.com/(?:pg/)?([A-Za-z0-9\.\-_]{2,})",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private readonly HttpClient _http;
    private readonly ILogger<WebsiteContactExtractor> _log;

    public WebsiteContactExtractor(HttpClient http, ILogger<WebsiteContactExtractor> log)
    {
        _http = http;
        _log = log;
        _http.Timeout = TimeSpan.FromSeconds(15);
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (compatible; SalesHubBot/1.0)");
    }

    public async Task<WebsiteContactInfo> ExtractAsync(string url, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(url)) return new(null, null, null);
        try
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var u)) return new(null, null, null);
            using var resp = await _http.GetAsync(u, ct);
            if (!resp.IsSuccessStatusCode) return new(null, null, null);
            var html = await resp.Content.ReadAsStringAsync(ct);

            // Trim to a reasonable size — homepages can be MB; the contact info almost always lives near the top.
            if (html.Length > 500_000) html = html[..500_000];

            var phone = ExtractPhone(html);
            var ig = ExtractInstagram(html);
            var fb = ExtractFacebook(u, html);
            return new(phone, ig, fb);
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Website extract failed for {Url}", url);
            return new(null, null, null);
        }
    }

    private static string? ExtractPhone(string html)
    {
        // Order of preference: explicit WhatsApp links → tel: links → loose phone matches.
        var wa = WhatsappLinkRegex.Match(html);
        if (wa.Success) return wa.Groups[1].Value;

        var tel = TelLinkRegex.Match(html);
        if (tel.Success) return tel.Groups[1].Value;

        var p = PhoneRegex.Match(html);
        return p.Success ? p.Value : null;
    }

    private static string? ExtractInstagram(string html)
    {
        foreach (Match m in InstagramRegex.Matches(html))
        {
            var handle = m.Groups[1].Value;
            // Skip trivially-short or generic handles that come from copy in the page.
            if (handle.Length < 3) continue;
            if (handle.Equals("p", StringComparison.OrdinalIgnoreCase)) continue;
            if (handle.Equals("explore", StringComparison.OrdinalIgnoreCase)) continue;
            if (handle.Equals("reel", StringComparison.OrdinalIgnoreCase)) continue;
            return handle.TrimStart('@');
        }
        return null;
    }

    private static string? ExtractFacebook(Uri pageUri, string html)
    {
        foreach (Match m in FacebookRegex.Matches(html))
        {
            var slug = m.Groups[1].Value;
            if (slug.Equals("sharer", StringComparison.OrdinalIgnoreCase)) continue;
            if (slug.Equals("tr", StringComparison.OrdinalIgnoreCase)) continue;
            if (slug.Equals("dialog", StringComparison.OrdinalIgnoreCase)) continue;
            return $"https://facebook.com/{slug}";
        }
        return null;
    }
}
