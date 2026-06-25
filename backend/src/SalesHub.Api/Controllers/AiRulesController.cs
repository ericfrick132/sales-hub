using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SalesHub.Core.Domain.Entities;
using SalesHub.Infrastructure.Persistence;
using SalesHub.Infrastructure.Services;

namespace SalesHub.Api.Controllers;

/// <summary>
/// CRUD de reglas duras de la IA de ventas (admin). Se inyectan al system prompt.
/// </summary>
[ApiController]
[Route("api/ai-rules")]
[Authorize]
public class AiRulesController : ControllerBase
{
    private readonly ApplicationDbContext _db;
    private readonly AiRulesProvider _rules;

    public AiRulesController(ApplicationDbContext db, AiRulesProvider rules)
    {
        _db = db;
        _rules = rules;
    }

    public record AiRuleDto(Guid Id, string Text, string? ProductKey, bool IsActive, int SortOrder);
    public record SaveAiRuleRequest(string Text, string? ProductKey, bool IsActive, int SortOrder);

    [HttpGet]
    public async Task<ActionResult<List<AiRuleDto>>> List(CancellationToken ct)
    {
        if (!CurrentUser.IsAdmin(User)) return Forbid();
        var rows = await _db.AiRules.AsNoTracking()
            .OrderBy(r => r.SortOrder).ThenBy(r => r.CreatedAt)
            .Select(r => new AiRuleDto(r.Id, r.Text, r.ProductKey, r.IsActive, r.SortOrder))
            .ToListAsync(ct);
        return Ok(rows);
    }

    [HttpPost]
    public async Task<ActionResult<AiRuleDto>> Create([FromBody] SaveAiRuleRequest req, CancellationToken ct)
    {
        if (!CurrentUser.IsAdmin(User)) return Forbid();
        if (string.IsNullOrWhiteSpace(req.Text)) return BadRequest("El texto de la regla es requerido.");

        var r = new AiRule
        {
            Text = req.Text.Trim(),
            ProductKey = string.IsNullOrWhiteSpace(req.ProductKey) ? null : req.ProductKey.Trim(),
            IsActive = req.IsActive,
            SortOrder = req.SortOrder,
        };
        _db.AiRules.Add(r);
        await _db.SaveChangesAsync(ct);
        _rules.Invalidate();
        return Ok(new AiRuleDto(r.Id, r.Text, r.ProductKey, r.IsActive, r.SortOrder));
    }

    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, [FromBody] SaveAiRuleRequest req, CancellationToken ct)
    {
        if (!CurrentUser.IsAdmin(User)) return Forbid();
        var r = await _db.AiRules.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (r is null) return NotFound();
        if (string.IsNullOrWhiteSpace(req.Text)) return BadRequest("El texto de la regla es requerido.");

        r.Text = req.Text.Trim();
        r.ProductKey = string.IsNullOrWhiteSpace(req.ProductKey) ? null : req.ProductKey.Trim();
        r.IsActive = req.IsActive;
        r.SortOrder = req.SortOrder;
        r.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);
        _rules.Invalidate();
        return NoContent();
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        if (!CurrentUser.IsAdmin(User)) return Forbid();
        var r = await _db.AiRules.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (r is null) return NotFound();
        _db.AiRules.Remove(r);
        await _db.SaveChangesAsync(ct);
        _rules.Invalidate();
        return NoContent();
    }
}
