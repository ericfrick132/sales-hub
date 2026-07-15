using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SalesHub.Core.Domain.Entities;
using SalesHub.Infrastructure.Options;
using SalesHub.Infrastructure.Persistence;

namespace SalesHub.Infrastructure.Instagram;

/// <summary>
/// Baja los posts de un competidor con una cuenta de IG LOGUEADA (browser + proxy) y los guarda
/// en CompetitorPost. REEMPLAZO del scraper de Apify (InstagramCompetitorScraper) — la inspiración
/// ya no depende de Apify. Requiere al menos una cuenta IG logueada; si no hay, no scrapea (y lo
/// loguea claro). No trae comentarios: el endpoint web solo da metadata del post (la pantalla de
/// comentarios negativos de /competitors se alimentaba de Apify; queda sin fuente por ahora).
/// </summary>
public class InstagramCompetitorBrowserScraper
{
    private static readonly Regex HashtagRx = new(@"#(\w+)", RegexOptions.Compiled);

    private readonly ApplicationDbContext _db;
    private readonly InstagramEncryptionService _crypto;
    private readonly InstagramOptions _opts;
    private readonly ILogger<InstagramCompetitorBrowserScraper> _log;

    public InstagramCompetitorBrowserScraper(ApplicationDbContext db, InstagramEncryptionService crypto,
        IOptions<InstagramOptions> opts, ILogger<InstagramCompetitorBrowserScraper> log)
    {
        _db = db; _crypto = crypto; _opts = opts.Value; _log = log;
    }

    /// <summary>Devuelve (posts nuevos, comentarios) — comentarios siempre 0 (ver clase).</summary>
    public async Task<(int posts, int comments)> ScrapeAsync(Guid competitorId, int maxPosts, CancellationToken ct)
    {
        var competitor = await _db.Competitors.FirstOrDefaultAsync(c => c.Id == competitorId, ct);
        if (competitor is null || competitor.Platform != "instagram") return (0, 0);

        // Cuenta logueada para el fetch (read-only). Preferimos una con proxy (sale por la IP
        // residencial / túnel) y rotamos por LastUsedAt para no cargar siempre la misma.
        var account = await _db.InstagramAccounts
            .Where(a => a.IsActive && !a.IsActionBlocked && a.IsLoggedIn && a.SessionCookiesJson != null)
            .OrderByDescending(a => a.ProxyUrl != null)
            .ThenBy(a => a.LastUsedAt)
            .FirstOrDefaultAsync(ct);
        if (account is null)
        {
            _log.LogWarning("Competitor @{Handle}: no hay cuenta IG logueada para scrapear — la inspiración queda sin fuente hasta loguear una.", competitor.Handle);
            return (0, 0);
        }

        List<InstagramScrapedPost> scraped;
        try
        {
            await using var client = new InstagramClient(_db, _crypto, _opts, _log);
            await client.InitializeAsync(account, ct);
            if (!await client.CheckSessionAsync(ct))
            {
                _log.LogWarning("Competitor @{Handle}: la sesión de {User} está caída, no se pudo scrapear.", competitor.Handle, account.Username);
                return (0, 0);
            }
            scraped = await client.ScrapeCompetitorPostsAsync(competitor.Handle, maxPosts, ct);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Competitor @{Handle}: scrape por browser falló (cuenta {User}).", competitor.Handle, account.Username);
            return (0, 0);
        }

        int newPosts = 0;
        foreach (var p in scraped)
        {
            if (await _db.CompetitorPosts.AnyAsync(x => x.CompetitorId == competitor.Id && x.ExternalPostId == p.ExternalId, ct))
                continue;
            _db.CompetitorPosts.Add(new CompetitorPost
            {
                Id = Guid.NewGuid(),
                CompetitorId = competitor.Id,
                ExternalPostId = p.ExternalId,
                PostUrl = p.Shortcode is not null ? $"https://www.instagram.com/p/{p.Shortcode}/" : null,
                Caption = p.Caption,
                PostedAt = p.PostedAt,
                Likes = p.Likes,
                CommentsCount = p.CommentsCount,
                Hashtags = ExtractHashtags(p.Caption ?? ""),
                MediaUrl = p.IsVideo ? (p.VideoUrl ?? p.DisplayUrl) : p.DisplayUrl,
                ThumbnailUrl = p.DisplayUrl,
                IsVideo = p.IsVideo,
                RawJson = p.RawJson
            });
            newPosts++;
        }

        competitor.LastScrapedAt = DateTimeOffset.UtcNow;
        account.LastUsedAt = DateTimeOffset.UtcNow; // rotación
        await _db.SaveChangesAsync(ct);
        _log.LogInformation("IG competitor @{Handle} (browser/{User}): {P} posts nuevos", competitor.Handle, account.Username, newPosts);
        return (newPosts, 0);
    }

    private static List<string> ExtractHashtags(string text)
        => HashtagRx.Matches(text).Select(m => m.Groups[1].Value).Distinct().ToList();
}
