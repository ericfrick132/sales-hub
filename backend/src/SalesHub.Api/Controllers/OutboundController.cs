using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SalesHub.Api.Services;
using SalesHub.Core.Domain.Entities;
using SalesHub.Core.Domain.Enums;
using SalesHub.Infrastructure.Persistence;
using SalesHub.Infrastructure.Services;
using SalesHub.Infrastructure.Services.Outbound;

namespace SalesHub.Api.Controllers;

/// <summary>
/// Menú Leads: ranking de prospectos (puntaje y desglose), búsqueda en Places por rubro × zona y las
/// líneas outbound (tope de contactos nuevos por día). Solo admin.
/// </summary>
[ApiController]
[Route("api/outbound")]
[Authorize]
public class OutboundController : ControllerBase
{
    private readonly ApplicationDbContext _db;
    private readonly OutboundService _outbound;
    private readonly ProspectService _prospects;
    private readonly ListenOnlyLines _listenOnly;
    private readonly ILogger<OutboundController> _log;

    public OutboundController(ApplicationDbContext db, OutboundService outbound, ProspectService prospects,
        ListenOnlyLines listenOnly, ILogger<OutboundController> log)
    {
        _db = db; _outbound = outbound; _prospects = prospects; _listenOnly = listenOnly; _log = log;
    }

    public record ContactDto(Guid LeadId, string Name, int Score, string Status, DateTimeOffset CreatedAt,
        DateTimeOffset? SentAt, DateTimeOffset? RepliedAt, string Message, string? Error);

    public record LineDto(Guid Id, string InstanceName, string Label, string? Phone, string Status, bool ListenOnly,
        string? ProductKey, int Cap, int Used, int Total, int MinScore, List<string> Rubros, string? SenderName,
        string? SenderNameEffective, string? Template, bool Paused, bool RequireWhatsapp, bool CanSend, string Reason, DateTimeOffset? NextAt,
        int Eligible, List<ContactDto> Contacts);

    /// <summary>Todo lo de la pestaña: las líneas outbound, los teléfonos que se pueden pasar a outbound y los catálogos.</summary>
    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken ct)
    {
        if (!CurrentUser.IsAdmin(User)) return Forbid();
        var lines = await _db.EvolutionInstances.AsNoTracking()
            .Where(i => i.SellerId == null)
            .OrderBy(i => i.CreatedAt)
            .ToListAsync(ct);

        var outboundLines = new List<LineDto>();
        foreach (var l in lines.Where(l => l.OutboundCap > 0)) outboundLines.Add(await ToDtoAsync(l, ct));

        var products = await _db.Products.AsNoTracking().Where(p => p.Active && p.ProductKey != "" && p.DisplayName != "")
            .OrderBy(p => p.DisplayName).Select(p => new { p.ProductKey, p.DisplayName }).ToListAsync(ct);

        return Ok(new
        {
            lines = outboundLines,
            otherLines = lines.Where(l => l.OutboundCap <= 0)
                .Select(l => new { l.Id, l.InstanceName, label = l.Label ?? l.InstanceName, l.ProductKey, status = l.Status.ToString(), l.ListenOnly }),
            products,
            rubros = ProspectScorer.Rubros.Select(r => new { r.Key, r.Label, r.SearchTerms }),
            zones = ProspectScorer.Zones.Select(z => new { z.Key, z.Label }),
            defaultTemplate = OutboundService.DefaultTemplate,
            // La extensión de Chrome de la Mac que lee los Instagram (latido cada 2 min = la Mac está prendida).
            readers = AudienceController.ReaderStatus(),
        });
    }

    private async Task<LineDto> ToDtoAsync(EvolutionInstance l, CancellationToken ct)
    {
        var state = await _outbound.StateAsync(l, ct);
        var contacts = await _db.OutboundContacts.AsNoTracking()
            .Where(c => c.InstanceName == l.InstanceName)
            .OrderByDescending(c => c.CreatedAt)
            .Select(c => new ContactDto(c.LeadId, c.Lead!.Name, c.Score, c.Status, c.CreatedAt, c.SentAt, c.RepliedAt, c.Message, c.Error))
            .ToListAsync(ct);
        var eligible = l.ProductKey is null ? 0 : await _outbound.CandidatesFor(l).CountAsync(ct);
        return new LineDto(l.Id, l.InstanceName, l.Label ?? l.InstanceName, l.ConnectedPhoneNumber, l.Status.ToString(),
            l.ListenOnly, l.ProductKey, l.OutboundCap, state.Used, await _outbound.TotalAsync(l.InstanceName, ct), l.OutboundMinScore, l.OutboundRubros, l.OutboundSenderName,
            await _outbound.SenderNameAsync(l, ct), l.OutboundTemplate, l.OutboundPausedAt is not null,
            l.OutboundRequireWhatsapp, state.CanSend, state.Reason, state.NextAt, eligible, contacts);
    }

    public record SaveLineRequest(int Cap, int MinScore, List<string>? Rubros, string? SenderName, string? Template, bool Paused,
        bool RequireWhatsapp = true);

    /// <summary>
    /// Prende/edita el outbound de un teléfono (sin vendedor). El tope es de contactos nuevos por día; 0 lo
    /// apaga. Al prenderlo se saca el "solo escucha" (si no, no podría mandar).
    /// </summary>
    [HttpPut("lines/{id:guid}")]
    public async Task<IActionResult> SaveLine(Guid id, [FromBody] SaveLineRequest req, CancellationToken ct)
    {
        if (!CurrentUser.IsAdmin(User)) return Forbid();
        var line = await _db.EvolutionInstances.FirstOrDefaultAsync(i => i.Id == id && i.SellerId == null, ct);
        if (line is null) return NotFound(new { error = "No existe ese teléfono." });
        if (string.IsNullOrWhiteSpace(line.ProductKey)) return BadRequest(new { error = "El teléfono no tiene app." });

        var cap = Math.Clamp(req.Cap, 0, 50);
        var rubros = (req.Rubros ?? new()).Where(r => ProspectScorer.RubroByKey(r) is not null).Distinct().ToList();
        var template = string.IsNullOrWhiteSpace(req.Template) || req.Template.Trim() == OutboundService.DefaultTemplate
            ? null : req.Template.Trim();

        line.OutboundCap = cap;
        line.OutboundMinScore = Math.Clamp(req.MinScore, 0, 100);
        line.OutboundRubros = rubros;
        line.OutboundSenderName = string.IsNullOrWhiteSpace(req.SenderName) ? null : req.SenderName.Trim()[..Math.Min(40, req.SenderName.Trim().Length)];
        line.OutboundTemplate = template;
        line.OutboundPausedAt = req.Paused ? (line.OutboundPausedAt ?? DateTimeOffset.UtcNow) : null;
        line.OutboundRequireWhatsapp = req.RequireWhatsapp;
        if (cap > 0 && line.ListenOnly)
        {
            line.ListenOnly = false;
            _listenOnly.Invalidate();
        }
        line.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);
        return Ok(await ToDtoAsync(line, ct));
    }

    /// <summary>El mensaje que le saldría al próximo lead de la línea (sin verificar su WhatsApp ni mandar nada).</summary>
    [HttpGet("lines/{id:guid}/preview")]
    public async Task<IActionResult> Preview(Guid id, CancellationToken ct)
    {
        if (!CurrentUser.IsAdmin(User)) return Forbid();
        var line = await _db.EvolutionInstances.AsNoTracking().FirstOrDefaultAsync(i => i.Id == id && i.SellerId == null, ct);
        if (line?.ProductKey is null) return NotFound(new { error = "No existe ese teléfono." });
        var product = await _db.Products.AsNoTracking().FirstAsync(p => p.ProductKey == line.ProductKey, ct);
        var next = await _outbound.CandidatesFor(line).Take(3).ToListAsync(ct);
        var sender = await _outbound.SenderNameAsync(line, ct) ?? "(sin nombre)";
        return Ok(next.Select(l => new
        {
            leadId = l.Id, l.Name, score = l.ProspectScore,
            message = CopyStyle.Clean(_outbound.Render(l, product, line, sender)),
        }));
    }

    public record RankingRow(Guid LeadId, string Name, string? Rubro, string? City, string? Address, double? Rating,
        int? Reviews, string? Website, string? Instagram, string? Phone, string? PhoneOrigin, string? BookingPlatform,
        int? InstagramFollowers, string? InstagramBioLink, DateTimeOffset? InstagramCheckedAt, int? Score, JsonElement? Detail, string Status, bool Excluded, bool Reserved, bool Eligible, string? Blocked,
        string? MapsUrl, DateTimeOffset CreatedAt);

    /// <summary>
    /// Ranking de prospectos de una app, mejores arriba. Por defecto solo los que la línea outbound podría
    /// contactar; con all=true también los que no (con el motivo).
    /// </summary>
    [HttpGet("ranking")]
    public async Task<IActionResult> Ranking([FromQuery] string productKey = "turnospro", [FromQuery] string? rubro = null,
        [FromQuery] bool all = false, [FromQuery] int take = 150, CancellationToken ct = default)
    {
        if (!CurrentUser.IsAdmin(User)) return Forbid();
        var eligibleIds = _outbound.Eligible(productKey).Select(l => l.Id);

        var q = _db.Leads.AsNoTracking().Where(l => l.ProductKey == productKey && l.ProspectScore != null && (int)l.Source < 400);
        if (!all) q = q.Where(l => eligibleIds.Contains(l.Id));
        if (!string.IsNullOrWhiteSpace(rubro)) q = q.Where(l => l.ProspectRubro == rubro);

        var rows = await q.OrderByDescending(l => l.ProspectScore).ThenByDescending(l => l.TotalReviews)
            .Take(Math.Clamp(take, 1, 500))
            .Select(l => new
            {
                l.Id, l.Name, l.ProspectRubro, l.City, l.Address, l.Rating, l.TotalReviews, l.Website, l.InstagramHandle,
                l.WhatsappPhone, l.PhoneOrigin, l.BookingPlatform, l.InstagramFollowers, l.InstagramBioLink, l.InstagramCheckedAt,
                l.ProspectScore, l.ProspectScoreDetail, l.Status, l.Tags,
                l.PlaceId, l.SentAt, l.FirstReplyAt, l.BusinessStatus, l.CreatedAt,
                Eligible = eligibleIds.Contains(l.Id),
                Contacted = _db.OutboundContacts.Any(c => c.LeadId == l.Id) || _db.ConversationMessages.Any(m => m.LeadId == l.Id),
                Queued = _db.Outbox.Any(o => o.LeadId == l.Id && o.Status == OutboxStatus.Scheduled),
                Manual = l.SellerId != null && l.ManualAssignedAt != null,
            })
            .ToListAsync(ct);

        var result = rows.Select(r =>
        {
            var excluded = r.Tags.Contains(OutboundService.ExcludedTag);
            string? blocked = r.Eligible ? null
                : excluded ? "excluido a mano"
                : r.Status == LeadStatus.NoWhatsApp ? "no tiene WhatsApp"
                : r.Contacted || r.SentAt != null || r.FirstReplyAt != null ? "ya hablamos"
                : r.Queued ? "tiene cadencia en cola"
                : r.Manual ? "asignado a mano a un vendedor"
                : r.BookingPlatform?.StartsWith("TurnosPro") == true ? "ya es cliente"
                : string.IsNullOrWhiteSpace(r.WhatsappPhone) ? "sin teléfono"
                : r.BusinessStatus is not null and not "OPERATIONAL" ? "cerrado"
                : $"estado {r.Status}";
            JsonElement? detail = null;
            if (!string.IsNullOrWhiteSpace(r.ProspectScoreDetail))
                try { detail = JsonDocument.Parse(r.ProspectScoreDetail).RootElement.Clone(); } catch (JsonException) { }
            return new RankingRow(r.Id, r.Name, r.ProspectRubro, r.City, r.Address, r.Rating, r.TotalReviews, r.Website,
                r.InstagramHandle, r.WhatsappPhone, r.PhoneOrigin, r.BookingPlatform, r.InstagramFollowers, r.InstagramBioLink,
                r.InstagramCheckedAt, r.ProspectScore, detail,
                r.Status.ToString(), excluded, r.Tags.Contains(OutboundService.ReservedTag), r.Eligible, blocked,
                r.PlaceId is null ? null : $"https://www.google.com/maps/place/?q=place_id:{r.PlaceId}", r.CreatedAt);
        }).ToList();

        var counts = new
        {
            eligible = await _outbound.Eligible(productKey).CountAsync(ct),
            eligibleOver50 = await _outbound.Eligible(productKey).CountAsync(l => l.ProspectScore >= 50, ct),
            verified = await _outbound.Eligible(productKey).CountAsync(l => l.PhoneOrigin == "whatsapp-publicado" || l.PhoneOrigin == "whatsapp-instagram", ct),
            igPending = await IgPending(productKey).CountAsync(ct),
            igChecked = await _outbound.Eligible(productKey).CountAsync(l => l.InstagramCheckedAt != null, ct),
            igSearchPending = await _outbound.Eligible(productKey).CountAsync(l => l.Tags.Contains(OutboundService.ReservedTag)
                && l.InstagramCheckedAt == null && (l.InstagramHandle == null || l.InstagramHandle == "")
                && (l.Website == null || !l.Website.Contains("instagram.com/")), ct),
            // Listos para la línea: analizados a fondo (web + Instagram) y con WhatsApp verificado.
            ready = await _outbound.Eligible(productKey).CountAsync(l => l.ProspectEnrichedAt != null && l.InstagramCheckedAt != null
                && (l.PhoneOrigin == "whatsapp-publicado" || l.PhoneOrigin == "whatsapp-instagram"), ct),
            reserved = await _db.Leads.CountAsync(l => l.ProductKey == productKey && l.Tags.Contains(OutboundService.ReservedTag), ct),
            pendingEnrich = await _db.Leads.CountAsync(l => l.ProductKey == productKey && (int)l.Source < 400
                && l.ProspectEnrichedAt == null && l.SentAt == null
                && (l.Status == LeadStatus.New || l.Status == LeadStatus.Assigned), ct),
            byRubro = await _outbound.Eligible(productKey).GroupBy(l => l.ProspectRubro)
                .Select(g => new { rubro = g.Key, count = g.Count(), over50 = g.Count(x => x.ProspectScore >= 50) })
                .ToListAsync(ct),
        };
        return Ok(new { rows = result, counts });
    }

    // ── Instagram (lo lee Claude in Chrome con la sesión de Eric) ───────────────────────

    /// <summary>Contactables con Instagram conocido (handle o web de Instagram) que todavía no se leyeron.</summary>
    private IQueryable<Lead> IgPending(string productKey) =>
        OutboundService.InstagramPendingQuery(_db).Where(l => l.ProductKey == productKey
            && l.Tags.Contains(OutboundService.ReservedTag)); // los mismos que le da la cola al lector

    /// <summary>Perfiles de Instagram a leer, los de mejor puntaje primero.</summary>
    [HttpGet("instagram/pending")]
    public async Task<IActionResult> InstagramPending([FromQuery] string productKey = "turnospro", [FromQuery] int take = 40,
        CancellationToken ct = default)
    {
        if (!CurrentUser.IsAdmin(User)) return Forbid();
        var rows = await IgPending(productKey)
            .OrderByDescending(l => l.ProspectScore)
            .Take(Math.Clamp(take, 1, 300))
            .Select(l => new { l.Id, l.InstagramHandle, l.Website, l.Name })
            .ToListAsync(ct);
        return Ok(rows.Select(r => new { leadId = r.Id, handle = HandleOf(r.InstagramHandle, r.Website), name = r.Name })
            .Where(r => r.handle is not null));
    }

    private static string? HandleOf(string? handle, string? website) => OutboundService.InstagramHandleOf(handle, website);

    /// <summary>Carga lo leído de cada perfil (bio, links, seguidores) y recalcula el puntaje.</summary>
    [HttpPost("instagram")]
    public async Task<IActionResult> InstagramProfiles([FromBody] List<ProspectEnricher.InstagramProfile> profiles,
        [FromServices] ProspectEnricher enricher, CancellationToken ct)
    {
        if (!CurrentUser.IsAdmin(User)) return Forbid();
        var ids = profiles.Select(p => p.LeadId).Distinct().ToList();
        var leads = await _db.Leads.Where(l => ids.Contains(l.Id)).ToListAsync(ct);
        var batches = new Dictionary<string, ProspectEnricher.Batch>();
        int verified = 0, competitor = 0;
        foreach (var p in profiles)
        {
            var lead = leads.FirstOrDefault(l => l.Id == p.LeadId);
            if (lead is null) continue;
            if (!batches.TryGetValue(lead.ProductKey, out var batch))
                batches[lead.ProductKey] = batch = await enricher.LoadBatchAsync(lead.ProductKey, ct);
            await enricher.ApplyInstagramAsync(lead, p, batch, ct);
            if (lead.PhoneOrigin is "whatsapp-instagram" or "whatsapp-publicado") verified++;
            if (lead.BookingPlatform is not null) competitor++;
        }
        await _db.SaveChangesAsync(ct);
        return Ok(new { applied = leads.Count, verified, competitor });
    }

    public record SearchRequest(string ProductKey, string Rubro, string Zone, int PerQuery = 10);

    /// <summary>Una búsqueda en Google Places (rubro × zona). Cada resultado es una consulta de detalle paga: ojo con el tamaño.</summary>
    [HttpPost("search")]
    public async Task<IActionResult> Search([FromBody] SearchRequest req, CancellationToken ct)
    {
        if (!CurrentUser.IsAdmin(User)) return Forbid();
        try
        {
            return Ok(await _prospects.SearchAsync(req.ProductKey, req.Rubro, req.Zone, req.PerQuery, ct));
        }
        catch (ArgumentException ex) { return BadRequest(new { error = ex.Message }); }
        catch (HttpRequestException ex)
        {
            _log.LogWarning(ex, "Places falló buscando {Rubro} en {Zona}", req.Rubro, req.Zone);
            return StatusCode(502, new { error = "Google Places no respondió. Probá de nuevo en un rato." });
        }
    }

    /// <summary>Recalcula el puntaje de todos los fríos sin contactar de la app (sin leer las webs de nuevo).</summary>
    [HttpPost("rescore")]
    public async Task<IActionResult> Rescore([FromQuery] string productKey = "turnospro", CancellationToken ct = default)
    {
        if (!CurrentUser.IsAdmin(User)) return Forbid();
        return Ok(new { rescored = await _prospects.RescoreAllAsync(productKey, ct) });
    }

    public record ExcludeRequest(bool Excluded);

    /// <summary>Saca (o vuelve a meter) a un lead del outbound: no se le escribe aunque tenga buen puntaje.</summary>
    [HttpPost("leads/{id:guid}/exclude")]
    public async Task<IActionResult> Exclude(Guid id, [FromBody] ExcludeRequest req, CancellationToken ct)
    {
        if (!CurrentUser.IsAdmin(User)) return Forbid();
        var lead = await _db.Leads.FirstOrDefaultAsync(l => l.Id == id, ct);
        if (lead is null) return NotFound();
        var has = lead.Tags.Contains(OutboundService.ExcludedTag);
        if (req.Excluded && !has) lead.Tags = lead.Tags.Append(OutboundService.ExcludedTag).ToList();
        if (!req.Excluded && has) lead.Tags = lead.Tags.Where(t => t != OutboundService.ExcludedTag).ToList();
        lead.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);
        return Ok(new { excluded = req.Excluded });
    }
}
