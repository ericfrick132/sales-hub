using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SalesHub.Core.Abstractions;
using SalesHub.Core.Domain.Enums;
using SalesHub.Infrastructure.Persistence;

namespace SalesHub.Api.Controllers;

/// <summary>
/// Envío 1-a-1 ad-hoc desde el panel de admin. Sirve para testear que Evolution
/// está respondiendo, probar mensajes de voz / imágenes / PDFs antes de
/// activarlos en el flujo automático.
/// </summary>
[ApiController]
[Route("api/test-send")]
[Authorize]
public class TestSendController : ControllerBase
{
    private readonly ApplicationDbContext _db;
    private readonly IEvolutionClient _evo;

    public TestSendController(ApplicationDbContext db, IEvolutionClient evo)
    {
        _db = db; _evo = evo;
    }

    public record TestTextRequest(Guid SellerId, string Phone, string Text);

    /// <summary>Manda un mensaje de texto.</summary>
    [HttpPost("text")]
    public async Task<IActionResult> SendText([FromBody] TestTextRequest req, CancellationToken ct)
    {
        if (!CurrentUser.IsAdmin(User)) return Forbid();
        var (instance, phone, err) = await ResolveAsync(req.SellerId, req.Phone, ct);
        if (err is not null) return BadRequest(new { error = err });
        if (string.IsNullOrWhiteSpace(req.Text)) return BadRequest(new { error = "Texto vacío" });

        var ok = await _evo.SendTextAsync(instance!, phone!, req.Text, ct);
        if (!ok) return StatusCode(502, new { error = "Falló el envío vía Evolution" });
        return Ok(new { ok = true });
    }

    /// <summary>Manda un audio como nota de voz (PTT). Multipart: file=audio.</summary>
    [HttpPost("voice")]
    [RequestSizeLimit(50_000_000)]
    public async Task<IActionResult> SendVoice([FromForm] Guid sellerId, [FromForm] string phone, IFormFile file, CancellationToken ct)
    {
        if (!CurrentUser.IsAdmin(User)) return Forbid();
        if (file is null || file.Length == 0) return BadRequest(new { error = "Falta archivo de audio" });
        var (instance, normalized, err) = await ResolveAsync(sellerId, phone, ct);
        if (err is not null) return BadRequest(new { error = err });

        using var ms = new MemoryStream();
        await file.CopyToAsync(ms, ct);

        var ok = await _evo.SendVoiceNoteAsync(instance!, normalized!, ms.ToArray(), ct);
        if (!ok) return StatusCode(502, new { error = "Falló el envío vía Evolution" });
        return Ok(new { ok = true });
    }

    /// <summary>Manda imagen / PDF / documento. Multipart: file + caption opcional.</summary>
    [HttpPost("media")]
    [RequestSizeLimit(50_000_000)]
    public async Task<IActionResult> SendMedia([FromForm] Guid sellerId, [FromForm] string phone, IFormFile file, [FromForm] string? caption, CancellationToken ct)
    {
        if (!CurrentUser.IsAdmin(User)) return Forbid();
        if (file is null || file.Length == 0) return BadRequest(new { error = "Falta archivo" });
        var (instance, normalized, err) = await ResolveAsync(sellerId, phone, ct);
        if (err is not null) return BadRequest(new { error = err });

        using var ms = new MemoryStream();
        await file.CopyToAsync(ms, ct);

        var mime = string.IsNullOrWhiteSpace(file.ContentType) ? "application/octet-stream" : file.ContentType;
        var ok = await _evo.SendMediaAsync(instance!, normalized!, ms.ToArray(), mime, file.FileName, caption, ct);
        if (!ok) return StatusCode(502, new { error = "Falló el envío vía Evolution" });
        return Ok(new { ok = true });
    }

    private async Task<(string? instance, string? phone, string? error)> ResolveAsync(Guid sellerId, string? phoneRaw, CancellationToken ct)
    {
        if (sellerId == Guid.Empty) return (null, null, "sellerId requerido");
        var phone = new string((phoneRaw ?? string.Empty).Where(char.IsDigit).ToArray());
        if (string.IsNullOrEmpty(phone)) return (null, null, "Número inválido (incluí prefijo de país)");

        var seller = await _db.Sellers.Include(s => s.EvolutionInstance)
            .FirstOrDefaultAsync(s => s.Id == sellerId, ct);
        if (seller?.EvolutionInstance is null) return (null, null, "Vendedor sin instancia Evolution");
        if (seller.EvolutionInstance.Status != InstanceStatus.Connected) return (null, null, "La instancia no está conectada");

        return (seller.EvolutionInstance.InstanceName, phone, null);
    }
}
