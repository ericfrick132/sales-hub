using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SalesHub.Infrastructure.Persistence;

namespace SalesHub.Api.Controllers;

/// <summary>
/// Interruptor del bot por línea (cada línea = un número de WhatsApp, normalmente uno por app).
/// Lo usa quien atiende (Mateo, sin ser admin) desde /conversaciones: apagado, el bot no contesta
/// en ninguna charla de esa línea. Para una sola charla está el botón del chat (Lead.BotMutedAt).
/// </summary>
[ApiController]
[Route("api/line-bots")]
[Authorize]
public class LineBotsController : ControllerBase
{
    private readonly ApplicationDbContext _db;

    public LineBotsController(ApplicationDbContext db) => _db = db;

    public record LineBotDto(Guid SellerId, string Name, string? Phone, string? App, string? Status, bool BotEnabled,
        DateTimeOffset? PausedAt, string? PausedBy);

    /// <summary>Las líneas activas (vendedor con WhatsApp por Evolution).</summary>
    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        var apps = await _db.Products.AsNoTracking().ToDictionaryAsync(p => p.ProductKey, p => p.DisplayName, ct);
        var rows = await _db.Sellers.AsNoTracking()
            .Where(s => s.IsActive && (s.EvolutionInstance != null || _db.Devices.Any(d => d.SellerId == s.Id)))
            .OrderBy(s => s.DisplayName)
            .Select(s => new
            {
                s.Id, s.DisplayName, s.BotPausedAt, s.BotPausedBy,
                Phone = s.EvolutionInstance != null ? s.EvolutionInstance.ConnectedPhoneNumber : null,
                App = s.EvolutionInstance != null ? s.EvolutionInstance.ProductKey : null,
                Status = s.EvolutionInstance != null ? s.EvolutionInstance.Status.ToString() : null,
                ListenOnly = s.EvolutionInstance != null && s.EvolutionInstance.ListenOnly,
            })
            .ToListAsync(ct);
        // Solo las líneas en uso (conectadas o conectándose) y las que tienen el bot apagado, así
        // siempre se puede volver a prender aunque la línea se haya caído.
        return Ok(rows.Where(r => !r.ListenOnly && (r.Status is "Connected" or "Connecting" || r.BotPausedAt is not null)).Select(r => new LineBotDto(r.Id, r.DisplayName, r.Phone,
            r.App is not null && apps.TryGetValue(r.App, out var n) ? n : r.App, r.Status,
            r.BotPausedAt is null, r.BotPausedAt, r.BotPausedBy)));
    }

    public record ToggleRequest(bool Enabled);

    [HttpPost("{sellerId:guid}")]
    public async Task<IActionResult> Toggle(Guid sellerId, [FromBody] ToggleRequest req, CancellationToken ct)
    {
        var seller = await _db.Sellers.FirstOrDefaultAsync(s => s.Id == sellerId, ct);
        if (seller is null) return NotFound();
        var who = await _db.Sellers.AsNoTracking().Where(s => s.Id == CurrentUser.Id(User))
            .Select(s => s.DisplayName).FirstOrDefaultAsync(ct);
        seller.BotPausedAt = req.Enabled ? null : DateTimeOffset.UtcNow;
        seller.BotPausedBy = req.Enabled ? null : who;
        seller.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);
        return Ok(new { sellerId, botEnabled = req.Enabled });
    }
}
