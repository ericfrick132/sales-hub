using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SalesHub.Infrastructure.Apify;

namespace SalesHub.Api.Controllers;

[ApiController]
[Route("api")]
[Authorize]
public class EnrichController : ControllerBase
{
    private readonly InstagramProfileEnricher _ig;
    private readonly WebsiteCrawlerEnricher _web;
    private readonly GoogleSearchService _search;
    private readonly InstagramCompetitorScraper _igCompetitor;
    private readonly ApifyTikTokSource _tiktok;

    public EnrichController(
        InstagramProfileEnricher ig,
        WebsiteCrawlerEnricher web,
        GoogleSearchService search,
        InstagramCompetitorScraper igCompetitor,
        ApifyTikTokSource tiktok)
    {
        _ig = ig; _web = web; _search = search; _igCompetitor = igCompetitor; _tiktok = tiktok;
    }

    public record SerpRequest(string Query, int MaxResults = 20, string Country = "ar", string Language = "es");
    public record TiktokScrapeRequest(string Hashtag, string Vertical, int MaxResults = 30);

    [HttpPost("leads/{id:guid}/enrich/instagram")]
    public async Task<IActionResult> EnrichInstagram(Guid id, CancellationToken ct)
    {
        var lead = await _ig.EnrichAsync(id, ct);
        return lead is null ? NotFound() : Ok(lead);
    }

    [HttpPost("leads/{id:guid}/enrich/website")]
    public async Task<IActionResult> EnrichWebsite(Guid id, CancellationToken ct)
    {
        var lead = await _web.EnrichAsync(id, ct);
        return lead is null ? NotFound() : Ok(lead);
    }

    [HttpPost("admin/google-search")]
    public async Task<IActionResult> GoogleSearch([FromBody] SerpRequest req, CancellationToken ct)
    {
        if (!CurrentUser.IsAdmin(User)) return Forbid();
        var res = await _search.SearchAsync(req.Query, req.MaxResults, req.Country, req.Language, ct);
        return Ok(res);
    }

    [HttpPost("competitors/{id:guid}/scrape")]
    public async Task<IActionResult> ScrapeCompetitor(Guid id, CancellationToken ct)
    {
        if (!CurrentUser.IsAdmin(User)) return Forbid();
        var (posts, comments) = await _igCompetitor.ScrapeAsync(id, 40, ct);
        return Ok(new { posts, comments });
    }

    [HttpPost("admin/trends/tiktok")]
    public async Task<IActionResult> TikTok([FromBody] TiktokScrapeRequest req, CancellationToken ct)
    {
        if (!CurrentUser.IsAdmin(User)) return Forbid();
        var n = await _tiktok.FetchHashtagAsync(req.Hashtag, req.Vertical, req.MaxResults, ct);
        return Ok(new { saved = n });
    }
}
