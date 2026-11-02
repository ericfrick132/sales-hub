using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Cors;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using SalesHub.Core.Abstractions;
using SalesHub.Core.Domain.Enums;
using SalesHub.Infrastructure.Persistence;

namespace SalesHub.Api.Controllers;

/// <summary>
/// Formulario "Pedí una demo / cotizá a medida" de la sección de precios de las landings propias
/// (cards #23/#24 del Trello de efcloud). Público (lo llama el navegador del visitante), así que:
/// CORS solo para los dominios de las landings, rate limit por IP, honeypot y productos fijos.
/// El lead entra por el mismo <see cref="ILeadIngestService"/> que Meta Lead Ads: dedup por
/// (producto, teléfono), vendedor asignado y su primer mensaje en la cola humanizada.
/// </summary>
[ApiController]
[Route("api/public/landing-leads")]
[AllowAnonymous]
[EnableCors(LandingCors.Policy)]
public class PublicLandingLeadsController : ControllerBase
{
    /// <summary>ProductKey de sales-hub de cada landing (construction = ArchiCloud, unistock = DashFlow).</summary>
    private static readonly HashSet<string> Products = new(StringComparer.Ordinal)
        { "gymhero", "turnospro", "playcrew", "construction", "unistock", "bunker" };

    private readonly ILeadIngestService _ingest;
    private readonly IPhoneNormalizer _phones;
    private readonly ApplicationDbContext _db;
    private readonly IAdminAlerter _alerter;
    private readonly ILogger<PublicLandingLeadsController> _log;

    public PublicLandingLeadsController(ILeadIngestService ingest, IPhoneNormalizer phones,
        ApplicationDbContext db, IAdminAlerter alerter, ILogger<PublicLandingLeadsController> log)
    {
        _ingest = ingest; _phones = phones; _db = db; _alerter = alerter; _log = log;
    }

    public record LandingLeadRequest(
        string? ProductKey, string? Name, string? Phone, string? Email, string? Business,
        string? Size, string? Plan, string? Message, string? Page,
        /// <summary>Honeypot: campo oculto que una persona nunca completa.</summary>
        string? Website);

    [HttpPost]
    [EnableRateLimiting(LandingCors.RateLimitPolicy)]
    public async Task<IActionResult> Create([FromBody] LandingLeadRequest req, CancellationToken ct)
    {
        // Bot: respondemos OK para no darle señal, y no creamos nada.
        if (!string.IsNullOrWhiteSpace(req.Website))
        {
            _log.LogInformation("Landing lead descartado por honeypot ({Product})", req.ProductKey);
            return Ok(new { ok = true });
        }

        var productKey = (req.ProductKey ?? string.Empty).Trim().ToLowerInvariant();
        if (!Products.Contains(productKey)) return BadRequest(new { error = "Producto inválido." });

        var name = Clip(req.Name, 80);
        if (name is null || name.Length < 2) return BadRequest(new { error = "Contanos tu nombre." });

        var digits = new string((req.Phone ?? string.Empty).Where(char.IsDigit).ToArray());
        if (digits.Length < 8 || digits.Length > 15)
            return BadRequest(new { error = "Revisá el número de WhatsApp (con código de área)." });
        // Mismo criterio que Meta Lead Ads: estricto para AR (arregla 0/15, 54 duplicado) y si no
        // aplica (otro país), los dígitos tal cual.
        var phone = _phones.NormalizeArStrict(req.Phone) ?? digits;

        var email = Clip(req.Email, 120);
        var result = await _ingest.IngestAsync(new LeadIngestRequest(
            productKey, LeadSource.LandingForm, null, name, Clip(req.Business, 120), phone, email), ct);

        var note = BuildNote(req);
        switch (result.Outcome)
        {
            case LeadIngestOutcome.Created when result.LeadId is Guid id:
            {
                var lead = await _db.Leads.FirstOrDefaultAsync(l => l.Id == id, ct);
                if (lead is not null)
                {
                    lead.Notes = note;
                    if (!lead.Tags.Contains("pidio-demo-web")) lead.Tags.Add("pidio-demo-web");
                    lead.RawDataJson = JsonSerializer.Serialize(req with { Website = null });
                    lead.WebRequestAt = DateTimeOffset.UtcNow;
                    lead.WebRequestJson = lead.RawDataJson;
                    await _db.SaveChangesAsync(ct);
                }
                break;
            }
            case LeadIngestOutcome.Duplicate:
            {
                // Ya lo teníamos (por ejemplo, un frío que ahora pidió demo): no le reencolamos mensajes,
                // pero le dejamos la nota y el tag para que el vendedor lo vea en el CRM.
                var lead = await _db.Leads.FirstOrDefaultAsync(l => l.ProductKey == productKey && l.WhatsappPhone == phone, ct);
                if (lead is not null)
                {
                    lead.Notes = string.IsNullOrWhiteSpace(lead.Notes) ? note : $"{lead.Notes}\n{note}";
                    if (!lead.Tags.Contains("pidio-demo-web")) lead.Tags.Add("pidio-demo-web");
                    // Pedido nuevo: vuelve arriba en "Pedidos web" y queda "Sin llamar" desde ahora.
                    lead.WebRequestAt = DateTimeOffset.UtcNow;
                    lead.WebRequestJson = JsonSerializer.Serialize(req with { Website = null });
                    lead.UpdatedAt = DateTimeOffset.UtcNow;
                    await _db.SaveChangesAsync(ct);
                }
                break;
            }
            case LeadIngestOutcome.NoProduct:
                _log.LogError("Landing lead: el producto {Product} no existe en sales-hub", productKey);
                return StatusCode(500, new { error = "No pudimos registrar el pedido. Escribinos por WhatsApp." });
            case LeadIngestOutcome.NoPhone:
                return BadRequest(new { error = "Revisá el número de WhatsApp (con código de área)." });
        }

        // La web promete "te llamamos dentro de los próximos 10 minutos": aviso inmediato al número
        // maestro con todo lo necesario para llamar (best-effort, nunca tira).
        if (result.Outcome is LeadIngestOutcome.Created or LeadIngestOutcome.Duplicate)
        {
            var seller = await _db.Leads.Where(l => l.ProductKey == productKey && l.WhatsappPhone == phone)
                .Select(l => l.Seller != null ? l.Seller.DisplayName : null).FirstOrDefaultAsync(ct);
            var origen = (req.Page ?? string.Empty).Contains("#salida") ? "popup de salida" : "sección de precios";
            var alert = new StringBuilder($"📞 LLAMAR YA — {productKey}: {name} · +{phone}");
            if (!string.IsNullOrWhiteSpace(email)) alert.Append($" · {email}");
            alert.Append($"\nPidió que lo llamen desde la web ({origen}{(string.IsNullOrWhiteSpace(req.Page) ? "" : ", " + Clip(req.Page, 80))}).");
            if (Clip(req.Business, 80) is { } neg) alert.Append($"\nNegocio: {neg}.");
            if (Clip(req.Plan, 40) is { } pl) alert.Append($" Plan: {pl}.");
            alert.Append(result.Outcome == LeadIngestOutcome.Duplicate ? "\nYa estaba en el CRM." : "\nNuevo en el CRM.");
            if (!string.IsNullOrWhiteSpace(seller)) alert.Append($" Vendedor: {seller}.");
            await _alerter.AlertAsync(alert.ToString(), ct);
        }

        _log.LogInformation("Landing lead {Product} → {Outcome} (plan={Plan}, page={Page})",
            productKey, result.Outcome, req.Plan ?? "-", req.Page ?? "-");
        return Ok(new { ok = true });
    }

    private static string BuildNote(LandingLeadRequest r)
    {
        var ar = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, TimeZoneInfo.FindSystemTimeZoneById("America/Argentina/Buenos_Aires"));
        var sb = new StringBuilder($"Pidió demo/cotización en la web ({ar:dd/MM HH:mm} ART).");
        void Add(string label, string? v, int max) { var c = Clip(v, max); if (c is not null) sb.Append($" {label}: {c}."); }
        Add("Negocio", r.Business, 120);
        Add("Tamaño", r.Size, 60);
        Add("Plan de interés", r.Plan, 60);
        Add("Mail", r.Email, 120);
        Add("Mensaje", r.Message, 500);
        Add("Página", r.Page, 120);
        return sb.ToString();
    }

    private static string? Clip(string? v, int max)
    {
        if (string.IsNullOrWhiteSpace(v)) return null;
        var t = v.Trim().ReplaceLineEndings(" ");
        return t.Length > max ? t[..max] : t;
    }
}

/// <summary>Nombres de la política CORS y del rate limit del formulario público de las landings.</summary>
public static class LandingCors
{
    public const string Policy = "landings";
    public const string RateLimitPolicy = "landing-leads";

    /// <summary>Dominios de las landings. Se puede pisar con Cors:LandingOrigins (coma-separado).</summary>
    public static readonly string[] DefaultOrigins =
    {
        "https://gymhero.fitness", "https://www.gymhero.fitness",
        "https://turnos-pro.com", "https://www.turnos-pro.com",
        "https://playcrew.com.ar", "https://www.playcrew.com.ar",
        "https://archicloud.tech", "https://www.archicloud.tech",
        "https://bunker-app.com", "https://www.bunker-app.com",
        "https://dashflow.com.ar", "https://www.dashflow.com.ar",
    };

    /// <summary>
    /// IP real del visitante detrás del proxy de DigitalOcean (do-connecting-ip, si no el primer
    /// X-Forwarded-For). Sin esto todas las requests compartirían el balde del proxy.
    /// </summary>
    public static string ClientIp(HttpContext ctx)
    {
        var h = ctx.Request.Headers;
        var ip = h["do-connecting-ip"].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(ip)) ip = h["X-Forwarded-For"].FirstOrDefault()?.Split(',')[0].Trim();
        return string.IsNullOrWhiteSpace(ip) ? ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown" : ip;
    }
}
