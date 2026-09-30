using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SalesHub.Api.Auth;
using SalesHub.Core.Domain.Entities;
using SalesHub.Infrastructure.Persistence;
using SalesHub.Infrastructure.Services;

namespace SalesHub.Api.Controllers;

/// <summary>
/// Diccionario de tipos de mensaje del lead (/diccionario): cobertura en vivo, edición de
/// patrones y respuestas, probador y clasificación de los mensajes viejos.
/// </summary>
[ApiController]
[Route("api/intents")]
[Authorize]
public class IntentsController : ControllerBase
{
    private readonly ApplicationDbContext _db;
    private readonly IntentClassifier _classifier;

    public IntentsController(ApplicationDbContext db, IntentClassifier classifier)
    {
        _db = db; _classifier = classifier;
    }

    [HttpGet]
    public async Task<IActionResult> List([FromQuery] int days = 30, CancellationToken ct = default)
    {
        if (!CurrentUser.IsAdmin(User)) return Forbid();
        await _classifier.GetMatcherAsync(ct); // siembra si está vacía
        var since = DateTimeOffset.UtcNow.AddDays(-Math.Clamp(days, 1, 3650));

        var counts = await _db.ConversationMessages.AsNoTracking()
            .Where(m => m.Direction == MessageDirection.Inbound && m.Timestamp >= since && m.IntentKey != null)
            .GroupBy(m => m.IntentKey!)
            .Select(g => new { Key = g.Key, N = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.N, ct);
        var unclassified = await _db.ConversationMessages.CountAsync(m => m.Direction == MessageDirection.Inbound && m.IntentKey == null, ct);
        var total = counts.Values.Sum();
        var other = counts.GetValueOrDefault(IntentMatcher.Other) + counts.GetValueOrDefault(IntentMatcher.Empty);

        var intents = await _db.ReplyIntents.AsNoTracking().OrderBy(i => i.SortOrder).ToListAsync(ct);
        return Ok(new
        {
            days,
            total,
            covered = total - other,
            other = counts.GetValueOrDefault(IntentMatcher.Other),
            unclassified,
            intents = intents.Select(i => new
            {
                i.Id, i.Key, i.Name, i.Pattern, i.MaxWords, i.Action, i.Reply, i.ReplyByProduct, i.Note, i.Examples,
                i.SortOrder, i.Enabled, i.AutoReply,
                count = counts.GetValueOrDefault(i.Key),
            }),
        });
    }

    /// <summary>Los últimos mensajes de un tipo (o "otro"), para revisar si el patrón acierta.</summary>
    [HttpGet("{key}/messages")]
    public async Task<IActionResult> Messages(string key, [FromQuery] int take = 30, CancellationToken ct = default)
    {
        if (!CurrentUser.IsAdmin(User)) return Forbid();
        var rows = await _db.ConversationMessages.AsNoTracking()
            .Where(m => m.Direction == MessageDirection.Inbound && m.IntentKey == key)
            .OrderByDescending(m => m.Timestamp)
            .Take(Math.Clamp(take, 1, 100))
            .Select(m => new { m.LeadId, m.Text, m.Timestamp, lead = m.Lead!.Name })
            .ToListAsync(ct);
        return Ok(rows);
    }

    public record UpdateRequest(string Name, string Pattern, int? MaxWords, string Action, string? Reply, string? Note, bool Enabled, int SortOrder,
        Dictionary<string, string>? ReplyByProduct);

    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdateRequest req, CancellationToken ct)
    {
        if (!CurrentUser.IsAdmin(User)) return Forbid();
        var i = await _db.ReplyIntents.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (i is null) return NotFound();
        if (string.IsNullOrWhiteSpace(req.Pattern)) return BadRequest(new { error = "El patrón no puede quedar vacío." });
        try { _ = new Regex(req.Pattern, RegexOptions.None, TimeSpan.FromMilliseconds(200)); }
        catch (ArgumentException ex) { return BadRequest(new { error = $"El patrón no es una expresión válida: {ex.Message}" }); }

        i.Name = req.Name.Trim();
        i.Pattern = req.Pattern.Trim();
        i.MaxWords = req.MaxWords is > 0 ? req.MaxWords : null;
        i.Action = req.Action.Trim();
        i.Reply = string.IsNullOrWhiteSpace(req.Reply) ? null : req.Reply.Trim();
        i.Note = string.IsNullOrWhiteSpace(req.Note) ? null : req.Note.Trim();
        i.Enabled = req.Enabled;
        if (req.ReplyByProduct is not null)
            i.ReplyByProduct = req.ReplyByProduct
                .Where(kv => !string.IsNullOrWhiteSpace(kv.Key) && !string.IsNullOrWhiteSpace(kv.Value))
                .ToDictionary(kv => kv.Key.Trim(), kv => kv.Value.Trim());
        i.SortOrder = req.SortOrder;
        i.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);
        _classifier.Invalidate();
        return Ok(new { ok = true });
    }

    /// <summary>
    /// Reemplaza el diccionario por la versión del análisis que viene con el deploy (recurso
    /// embebido). Pisa las ediciones hechas a mano: la pantalla pide confirmación.
    /// </summary>
    [HttpPost("reset")]
    public async Task<IActionResult> Reset(CancellationToken ct)
    {
        if (!CurrentUser.IsAdmin(User)) return Forbid();
        var seed = IntentClassifier.LoadSeed();
        await _db.ReplyIntents.ExecuteDeleteAsync(ct);
        _db.ReplyIntents.AddRange(seed);
        await _db.SaveChangesAsync(ct);
        _classifier.Invalidate();
        return Ok(new { ok = true, count = seed.Count });
    }

    /// <summary>
    /// Simulación en vivo: por cada mensaje del lead, qué habría hecho el bot (nada se manda) y qué
    /// se le respondió de verdad después (el siguiente mensaje nuestro dentro de 2 h). Resumen por app.
    /// </summary>
    [HttpGet("simulation")]
    public async Task<IActionResult> Simulation([FromQuery] int days = 30, [FromQuery] string? product = null,
        [FromQuery] string? action = null, [FromQuery] int take = 60, CancellationToken ct = default)
    {
        if (!CurrentUser.IsAdmin(User)) return Forbid();
        var since = DateTimeOffset.UtcNow.AddDays(-Math.Clamp(days, 1, 3650));
        var q = _db.ConversationMessages.AsNoTracking()
            .Where(m => m.Direction == MessageDirection.Inbound && m.Timestamp >= since && m.IntentAction != null);
        if (!string.IsNullOrWhiteSpace(product)) q = q.Where(m => m.Lead!.ProductKey == product);

        var summary = await q.GroupBy(m => new { m.Lead!.ProductKey, m.IntentAction })
            .Select(g => new { g.Key.ProductKey, g.Key.IntentAction, N = g.Count() })
            .ToListAsync(ct);

        if (!string.IsNullOrWhiteSpace(action)) q = q.Where(m => m.IntentAction == action);
        var rows = await q.OrderByDescending(m => m.Timestamp).Take(Math.Clamp(take, 1, 200))
            .Select(m => new
            {
                m.Id, m.LeadId, lead = m.Lead!.Name, product = m.Lead.ProductKey,
                seller = m.Lead.Seller != null ? m.Lead.Seller.DisplayName : null,
                m.Text, m.Timestamp, m.IntentKey, m.IntentConfident, m.IntentAction, m.IntentSimulatedReply,
                actual = _db.ConversationMessages
                    .Where(o => o.LeadId == m.LeadId && o.Direction == MessageDirection.Outbound
                             && o.Timestamp > m.Timestamp && o.Timestamp <= m.Timestamp.AddHours(2))
                    .OrderBy(o => o.Timestamp)
                    .Select(o => new { o.Text, o.Timestamp })
                    .FirstOrDefault(),
            })
            .ToListAsync(ct);

        return Ok(new
        {
            byProduct = summary.GroupBy(s => s.ProductKey).Select(g => new
            {
                product = g.Key,
                total = g.Sum(x => x.N),
                reply = g.Where(x => x.IntentAction == IntentReplyPlanner.Reply).Sum(x => x.N),
                noReply = g.Where(x => x.IntentAction == IntentReplyPlanner.NoReply).Sum(x => x.N),
                toHuman = g.Where(x => x.IntentAction == IntentReplyPlanner.ToHuman).Sum(x => x.N),
            }).OrderByDescending(x => x.total),
            rows = rows.Select(r => new
            {
                r.Id, r.LeadId, r.lead, r.product, r.seller, r.Text, r.Timestamp, r.IntentKey, r.IntentConfident,
                r.IntentAction, r.IntentSimulatedReply,
                actualReply = r.actual?.Text,
                actualAfterMin = r.actual is null ? (int?)null : (int)(r.actual.Timestamp - r.Timestamp).TotalMinutes,
            }),
        });
    }

    public record TestRequest(string Text, string? Previous);

    /// <summary>Probador: qué tipo le asigna el diccionario a un texto.</summary>
    [HttpPost("test")]
    public async Task<IActionResult> Test([FromBody] TestRequest req, CancellationToken ct)
    {
        if (!CurrentUser.IsAdmin(User)) return Forbid();
        var key = await _classifier.ClassifyAsync(req.Text, req.Previous, ct);
        return Ok(new { key, normalized = IntentText.Normalize(req.Text) });
    }

    /// <summary>
    /// Clasifica los mensajes entrantes viejos (o todos, con <paramref name="all"/>, después de editar
    /// patrones). Procesa de a tandas de leads; devuelve cuántos quedan para volver a llamar.
    /// </summary>
    [HttpPost("backfill")]
    public async Task<IActionResult> Backfill([FromQuery] bool all = false, [FromQuery] int leads = 400, CancellationToken ct = default)
    {
        if (!CurrentUser.IsAdmin(User)) return Forbid();
        var matcher = await _classifier.GetMatcherAsync(ct);

        if (all)
        {
            await _db.ConversationMessages.Where(m => m.Direction == MessageDirection.Inbound && m.IntentKey != null)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(m => m.IntentKey, (string?)null)
                    .SetProperty(m => m.IntentConfident, (bool?)null)
                    .SetProperty(m => m.IntentAction, (string?)null)
                    .SetProperty(m => m.IntentSimulatedReply, (string?)null), ct);
        }

        var leadIds = await _db.ConversationMessages.AsNoTracking()
            .Where(m => m.Direction == MessageDirection.Inbound && m.IntentKey == null)
            .Select(m => m.LeadId).Distinct().Take(Math.Clamp(leads, 1, 2000))
            .ToListAsync(ct);

        var updated = 0;
        var productNames = await _db.Products.AsNoTracking().ToDictionaryAsync(p => p.ProductKey, p => p.DisplayName, ct);
        foreach (var chunk in leadIds.Chunk(100))
        {
            var leadInfo = await _db.Leads.AsNoTracking().Where(l => chunk.Contains(l.Id))
                .Select(l => new { l.Id, l.ProductKey, Seller = l.Seller != null ? l.Seller.DisplayName : null })
                .ToDictionaryAsync(l => l.Id, ct);
            var msgs = await _db.ConversationMessages
                .Where(m => chunk.Contains(m.LeadId))
                .OrderBy(m => m.LeadId).ThenBy(m => m.Timestamp)
                .ToListAsync(ct);
            foreach (var g in msgs.GroupBy(m => m.LeadId))
            {
                string? lastOut = null;
                foreach (var m in g)
                {
                    if (m.Direction == MessageDirection.Outbound) { lastOut = m.Text; continue; }
                    if (m.IntentKey is not null) continue;
                    var (key, confident) = matcher.ClassifyWithConfidence(m.Text, lastOut);
                    m.IntentKey = key;
                    m.IntentConfident = confident;
                    // Simulación sobre el historial: qué habría hecho el bot (se compara con lo que se respondió).
                    var info = leadInfo.GetValueOrDefault(m.LeadId);
                    var plan = IntentReplyPlanner.Decide(key, confident, await _classifier.GetRuleAsync(key, ct), lastOut, m.Id,
                        info?.Seller, info?.ProductKey, info is null ? null : productNames.GetValueOrDefault(info.ProductKey));
                    m.IntentAction = plan.Action;
                    m.IntentSimulatedReply = plan.Text;
                    updated++;
                }
            }
            await _db.SaveChangesAsync(ct);
            _db.ChangeTracker.Clear();
        }

        var remaining = await _db.ConversationMessages.CountAsync(m => m.Direction == MessageDirection.Inbound && m.IntentKey == null, ct);
        return Ok(new { updated, remaining });
    }
}
