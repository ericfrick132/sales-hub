using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SalesHub.Core.Abstractions;
using SalesHub.Core.Domain.Entities;
using SalesHub.Infrastructure.Persistence;

namespace SalesHub.Api.Controllers;

/// <summary>
/// Audios del pitch del onboarding, grabados desde la plataforma. Al subir se convierten a OGG/Opus
/// (nota de voz). Se guardan varias variantes por app y el motor rota al azar.
/// </summary>
[ApiController]
[Route("api/onboarding-configs/{productKey}/audios")]
[Authorize(Roles = "Admin")]
public class OnboardingAudioController : ControllerBase
{
    private readonly ApplicationDbContext _db;
    private readonly IEvolutionClient _evo;
    public OnboardingAudioController(ApplicationDbContext db, IEvolutionClient evo) { _db = db; _evo = evo; }

    public record AudioDto(Guid Id, int DurationMs, DateTimeOffset CreatedAt);

    [HttpGet]
    public async Task<IActionResult> List(string productKey, CancellationToken ct)
    {
        var items = await _db.OnboardingAudios.AsNoTracking()
            .Where(a => a.ProductKey == productKey)
            .OrderBy(a => a.CreatedAt)
            .Select(a => new AudioDto(a.Id, a.DurationMs, a.CreatedAt))
            .ToListAsync(ct);
        return Ok(items);
    }

    [HttpPost]
    [RequestSizeLimit(25_000_000)]
    public async Task<IActionResult> Upload(string productKey, IFormFile file, CancellationToken ct)
    {
        if (file is null || file.Length == 0) return BadRequest(new { error = "Sin archivo" });
        using var ms = new MemoryStream();
        await file.CopyToAsync(ms, ct);

        // Convierte lo que grabó el navegador (webm/ogg) a OGG/Opus PTT.
        var prepared = await _evo.PrepareVoiceNoteAsync(ms.ToArray(), ct);
        _db.OnboardingAudios.Add(new OnboardingAudio
        {
            ProductKey = productKey,
            Data = prepared.OggBytes,
            MimeType = "audio/ogg",
            DurationMs = prepared.DurationSeconds * 1000,
        });
        await _db.SaveChangesAsync(ct);
        return Ok(new { ok = true });
    }

    [HttpGet("{id:guid}/play")]
    public async Task<IActionResult> Play(string productKey, Guid id, CancellationToken ct)
    {
        var a = await _db.OnboardingAudios.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == id && x.ProductKey == productKey, ct);
        if (a is null) return NotFound();
        return File(a.Data, a.MimeType);
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(string productKey, Guid id, CancellationToken ct)
    {
        var a = await _db.OnboardingAudios.FirstOrDefaultAsync(x => x.Id == id && x.ProductKey == productKey, ct);
        if (a is null) return NotFound();
        _db.OnboardingAudios.Remove(a);
        await _db.SaveChangesAsync(ct);
        return Ok(new { ok = true });
    }
}
