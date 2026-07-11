using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SalesHub.Core.Domain.Entities;
using SalesHub.Core.Domain.Enums;
using SalesHub.Infrastructure.Persistence;
using SalesHub.Infrastructure.Services;

namespace SalesHub.Api.Controllers;

/// <summary>
/// Admin del bot de SOPORTE: FAQs curadas por producto (capa 1), knowledge base subida
/// por las apps (capa 2, read-only acá), casos abiertos/escalados, y el TONO de
/// conversación editable que rige todo lo que compone la IA.
/// </summary>
[ApiController]
[Route("api/support")]
[Authorize(Roles = "Admin")]
public class SupportController : ControllerBase
{
    private readonly ApplicationDbContext _db;
    private readonly ToneProvider _tone;
    private readonly ILogger<SupportController> _log;

    public SupportController(ApplicationDbContext db, ToneProvider tone, ILogger<SupportController> log)
    {
        _db = db; _tone = tone; _log = log;
    }

    // ── FAQs ────────────────────────────────────────────────────────────────

    [HttpGet("faqs")]
    public async Task<IActionResult> ListFaqs([FromQuery] string? productKey, CancellationToken ct)
    {
        var q = _db.ProductFaqs.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(productKey)) q = q.Where(f => f.ProductKey == productKey);
        var faqs = await q.OrderBy(f => f.ProductKey).ThenByDescending(f => f.TimesUsed).ToListAsync(ct);
        return Ok(faqs);
    }

    public record FaqUpsertRequest(Guid? Id, string ProductKey, string Question, List<string>? Keywords, string Answer, bool Enabled);

    [HttpPut("faqs")]
    public async Task<IActionResult> UpsertFaq([FromBody] FaqUpsertRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.ProductKey) || string.IsNullOrWhiteSpace(req.Question) || string.IsNullOrWhiteSpace(req.Answer))
            return BadRequest(new { error = "productKey, question y answer requeridos" });
        if (!await _db.Products.AnyAsync(p => p.ProductKey == req.ProductKey, ct))
            return BadRequest(new { error = $"producto desconocido: {req.ProductKey}" });

        ProductFaq? faq = null;
        if (req.Id is { } id) faq = await _db.ProductFaqs.FirstOrDefaultAsync(f => f.Id == id, ct);
        if (faq is null)
        {
            faq = new ProductFaq { Id = Guid.NewGuid(), ProductKey = req.ProductKey.Trim(), Source = "manual" };
            _db.ProductFaqs.Add(faq);
        }
        faq.Question = req.Question.Trim();
        faq.Keywords = (req.Keywords ?? new()).Select(k => k.Trim()).Where(k => k.Length > 0).ToList();
        faq.Answer = req.Answer.Trim();
        faq.Enabled = req.Enabled;
        faq.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);
        return Ok(faq);
    }

    [HttpDelete("faqs/{id:guid}")]
    public async Task<IActionResult> DeleteFaq(Guid id, CancellationToken ct)
    {
        var faq = await _db.ProductFaqs.FirstOrDefaultAsync(f => f.Id == id, ct);
        if (faq is null) return NotFound();
        _db.ProductFaqs.Remove(faq);
        await _db.SaveChangesAsync(ct);
        return Ok(new { ok = true });
    }

    /// <summary>Promueve una respuesta aprobada de una conversación a FAQ canónica.</summary>
    public record FaqPromoteRequest(string ProductKey, string Question, string Answer, List<string>? Keywords);

    [HttpPost("faqs/promote")]
    public async Task<IActionResult> PromoteFaq([FromBody] FaqPromoteRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.ProductKey) || string.IsNullOrWhiteSpace(req.Question) || string.IsNullOrWhiteSpace(req.Answer))
            return BadRequest(new { error = "productKey, question y answer requeridos" });
        var faq = new ProductFaq
        {
            Id = Guid.NewGuid(),
            ProductKey = req.ProductKey.Trim(),
            Question = req.Question.Trim(),
            Answer = req.Answer.Trim(),
            Keywords = (req.Keywords ?? new()).Select(k => k.Trim()).Where(k => k.Length > 0).ToList(),
            Source = "promoted",
        };
        _db.ProductFaqs.Add(faq);
        await _db.SaveChangesAsync(ct);
        _log.LogInformation("FAQ promovida para {Pk}: \"{Q}\"", faq.ProductKey, faq.Question);
        return Ok(faq);
    }

    // ── Knowledge (read-only: la suben las apps vía POST /api/hub/knowledge) ──

    [HttpGet("knowledge")]
    public async Task<IActionResult> ListKnowledge(CancellationToken ct)
    {
        var items = await _db.ProductKnowledge.AsNoTracking()
            .Select(k => new { k.Id, k.ProductKey, k.SourceVersion, k.UploadedAt, Length = k.Content.Length })
            .OrderBy(k => k.ProductKey)
            .ToListAsync(ct);
        return Ok(items);
    }

    [HttpGet("knowledge/{productKey}")]
    public async Task<IActionResult> GetKnowledge(string productKey, CancellationToken ct)
    {
        var k = await _db.ProductKnowledge.AsNoTracking().FirstOrDefaultAsync(x => x.ProductKey == productKey, ct);
        return k is null ? NotFound() : Ok(k);
    }

    // ── Casos ──────────────────────────────────────────────────────────────

    [HttpGet("cases")]
    public async Task<IActionResult> ListCases([FromQuery] string? status, CancellationToken ct)
    {
        var q = _db.SupportCases.AsNoTracking().Include(c => c.Lead).AsQueryable();
        if (!string.IsNullOrWhiteSpace(status) && Enum.TryParse<SupportCaseStatus>(status, true, out var st))
            q = q.Where(c => c.Status == st);
        else if (string.IsNullOrWhiteSpace(status))
            q = q.Where(c => c.Status != SupportCaseStatus.Resolved); // default: los que necesitan atención
        var cases = await q.OrderByDescending(c => c.OpenedAt).Take(200)
            .Select(c => new
            {
                c.Id, c.ProductKey, c.Status, c.BotTurns, c.Summary,
                c.OpenedAt, c.LastBotReplyAt, c.EscalatedAt, c.ResolvedAt,
                LeadId = c.LeadId,
                LeadName = c.Lead != null ? c.Lead.Name : null,
                LeadPhone = c.Lead != null ? c.Lead.WhatsappPhone : null,
            })
            .ToListAsync(ct);
        return Ok(cases);
    }

    public record CasePatchRequest(string Status);

    [HttpPatch("cases/{id:guid}")]
    public async Task<IActionResult> PatchCase(Guid id, [FromBody] CasePatchRequest req, CancellationToken ct)
    {
        var c = await _db.SupportCases.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (c is null) return NotFound();
        if (!Enum.TryParse<SupportCaseStatus>(req.Status, true, out var st))
            return BadRequest(new { error = "status inválido (Open|Escalated|Resolved)" });
        c.Status = st;
        if (st == SupportCaseStatus.Resolved) c.ResolvedAt = DateTimeOffset.UtcNow;
        else c.ResolvedAt = null;
        await _db.SaveChangesAsync(ct);
        return Ok(new { ok = true, c.Id, Status = c.Status.ToString() });
    }

    // ── Tono ───────────────────────────────────────────────────────────────

    [HttpGet("tone")]
    public async Task<IActionResult> GetTone(CancellationToken ct)
    {
        var profiles = await _db.ToneProfiles.AsNoTracking().OrderBy(t => t.Scope).ToListAsync(ct);
        return Ok(new { profiles, defaultTone = ToneProvider.DefaultTone });
    }

    public record ToneUpsertRequest(string Scope, string Content);

    [HttpPut("tone")]
    public async Task<IActionResult> UpsertTone([FromBody] ToneUpsertRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.Scope) || string.IsNullOrWhiteSpace(req.Content))
            return BadRequest(new { error = "scope y content requeridos" });
        var scope = req.Scope.Trim().ToLowerInvariant();
        if (scope != "global" && !await _db.Products.AnyAsync(p => p.ProductKey == scope, ct))
            return BadRequest(new { error = $"scope debe ser \"global\" o un productKey válido (recibido: {scope})" });

        var t = await _db.ToneProfiles.FirstOrDefaultAsync(x => x.Scope == scope, ct);
        if (t is null)
        {
            t = new ToneProfile { Id = Guid.NewGuid(), Scope = scope };
            _db.ToneProfiles.Add(t);
        }
        t.Content = req.Content.Trim();
        t.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);
        _tone.Invalidate();
        return Ok(t);
    }

    [HttpDelete("tone/{scope}")]
    public async Task<IActionResult> DeleteTone(string scope, CancellationToken ct)
    {
        var t = await _db.ToneProfiles.FirstOrDefaultAsync(x => x.Scope == scope.ToLowerInvariant(), ct);
        if (t is null) return NotFound();
        _db.ToneProfiles.Remove(t);
        await _db.SaveChangesAsync(ct);
        _tone.Invalidate();
        return Ok(new { ok = true });
    }
}
