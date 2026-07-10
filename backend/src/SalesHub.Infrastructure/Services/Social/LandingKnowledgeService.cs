using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SalesHub.Core.Domain.Entities.Social;
using SalesHub.Infrastructure.Persistence;

namespace SalesHub.Infrastructure.Services.Social;

/// <summary>
/// Destila la landing de un producto en una "ficha de marca" con los datos REALES
/// (qué hace, features, precios/planes, pruebas sociales) para que el generador de
/// contenido no invente. Baja el HTML, lo limpia y se lo pasa a Claude una vez; el
/// resultado se cachea en <see cref="PostingProfile.LandingKnowledge"/> y se refresca
/// cuando está viejo o a pedido.
/// </summary>
public class LandingKnowledgeService
{
    /// <summary>Se re-fetchea sola si la ficha tiene más de esto (además del refresh manual).</summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromDays(14);

    private readonly IHttpClientFactory _httpFactory;
    private readonly ClaudeClient _claude;
    private readonly ILogger<LandingKnowledgeService> _log;

    private static readonly Regex ScriptStyle = new(@"<(script|style)[^>]*>.*?</\1>", RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex Tags = new("<[^>]+>", RegexOptions.Compiled);
    private static readonly Regex Ws = new(@"\s+", RegexOptions.Compiled);

    public LandingKnowledgeService(IHttpClientFactory httpFactory, ClaudeClient claude, ILogger<LandingKnowledgeService> log)
    {
        _httpFactory = httpFactory; _claude = claude; _log = log;
    }

    /// <summary>Refresca si falta ficha, está vieja, o <paramref name="force"/>. Devuelve la ficha (o la cacheada).</summary>
    public async Task<string> EnsureFreshAsync(PostingProfile profile, bool force, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(profile.LandingUrl)) return profile.LandingKnowledge;

        var fresh = !force
            && !string.IsNullOrWhiteSpace(profile.LandingKnowledge)
            && profile.LandingKnowledgeAt is { } at
            && DateTimeOffset.UtcNow - at < MaxAge;
        if (fresh) return profile.LandingKnowledge;

        var text = await FetchTextAsync(profile.LandingUrl, ct);
        if (string.IsNullOrWhiteSpace(text)) return profile.LandingKnowledge; // no rompemos: dejamos la ficha vieja

        var knowledge = await DistillAsync(profile.ProductKey, profile.LandingUrl, text, ct);
        if (string.IsNullOrWhiteSpace(knowledge)) return profile.LandingKnowledge;

        profile.LandingKnowledge = knowledge;
        profile.LandingKnowledgeAt = DateTimeOffset.UtcNow;
        profile.UpdatedAt = DateTimeOffset.UtcNow;
        return knowledge;
    }

    private async Task<string?> FetchTextAsync(string url, CancellationToken ct)
    {
        try
        {
            var http = _httpFactory.CreateClient();
            http.Timeout = TimeSpan.FromSeconds(20);
            http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (compatible; SalesHubBot/1.0)");
            var html = await http.GetStringAsync(url, ct);
            if (string.IsNullOrWhiteSpace(html)) return null;
            var stripped = ScriptStyle.Replace(html, " ");
            stripped = Tags.Replace(stripped, " ");
            stripped = System.Net.WebUtility.HtmlDecode(stripped);
            stripped = Ws.Replace(stripped, " ").Trim();
            // Recortamos para no pasarnos de tokens (la landing es larga; lo útil está arriba).
            return stripped.Length > 12000 ? stripped[..12000] : stripped;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "No pude bajar la landing {Url}", url);
            return null;
        }
    }

    private async Task<string?> DistillAsync(string productKey, string url, string text, CancellationToken ct)
    {
        if (!_claude.IsConfigured) return null;
        var sys = "Sos un analista de producto. A partir del texto de una landing, extraés SOLO datos reales " +
                  "(no inventes nada). Escribís una ficha compacta en español, en viñetas, que otro sistema usará " +
                  "para generar contenido de redes fiel al producto. " +
                  "PALABRA PROHIBIDA: jamás escribas 'no-show' / 'no show' / 'no-shows' — aunque la landing la use. " +
                  "Traducila: 'ausencias', 'turnos que quedan vacíos', 'clientes que no se presentan'.";
        var user =
            $"Producto: {productKey}. Landing: {url}\n\n" +
            "Devolvé una ficha con estas secciones (omití la que no tenga datos en el texto):\n" +
            "QUÉ ES / QUÉ RESUELVE:\nFEATURES CLAVE (bullets):\nPRECIOS Y PLANES (exactos, con moneda; si no hay, escribí 'sin precios en la landing'):\nPRUEBAS/RESULTADOS (números, testimonios):\nDIFERENCIADORES:\nCTA / OFERTA VIGENTE:\n\n=== TEXTO DE LA LANDING ===\n" +
            text +
            "\n=== FIN ===\nSolo la ficha, sin preámbulo.";
        var raw = await _claude.CompleteAsync(sys, user, 900, null, "social", ct);
        return string.IsNullOrWhiteSpace(raw) ? null : raw.Trim();
    }
}
