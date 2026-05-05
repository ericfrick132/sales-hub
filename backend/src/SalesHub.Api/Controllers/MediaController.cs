using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SalesHub.Core.Domain.Entities;
using SalesHub.Infrastructure.Persistence;

namespace SalesHub.Api.Controllers;

[ApiController]
[Authorize]
public class MediaController : ControllerBase
{
    private readonly ApplicationDbContext _db;

    public MediaController(ApplicationDbContext db) { _db = db; }

    public record MediaAssetDto(Guid Id, string ProductKey, string FileName, string MimeType, long SizeBytes, DateTimeOffset CreatedAt);

    /// <summary>Sube un archivo asociado al producto. Multipart/form-data: campo "file".</summary>
    [HttpPost("api/products/{productKey}/media")]
    [RequestSizeLimit(50_000_000)]
    public async Task<ActionResult<MediaAssetDto>> Upload(string productKey, IFormFile file, CancellationToken ct)
    {
        if (!CurrentUser.IsAdmin(User)) return Forbid();
        if (file is null || file.Length == 0) return BadRequest(new { error = "Falta archivo" });

        var product = await _db.Products.FirstOrDefaultAsync(p => p.ProductKey == productKey, ct);
        if (product is null) return NotFound(new { error = $"Producto '{productKey}' no existe" });

        using var ms = new MemoryStream();
        await file.CopyToAsync(ms, ct);
        var asset = new MediaAsset
        {
            Id = Guid.NewGuid(),
            ProductKey = productKey,
            FileName = file.FileName,
            MimeType = string.IsNullOrWhiteSpace(file.ContentType) ? "application/octet-stream" : file.ContentType,
            SizeBytes = file.Length,
            Content = ms.ToArray()
        };
        _db.MediaAssets.Add(asset);
        await _db.SaveChangesAsync(ct);
        return new MediaAssetDto(asset.Id, asset.ProductKey, asset.FileName, asset.MimeType, asset.SizeBytes, asset.CreatedAt);
    }

    [HttpGet("api/products/{productKey}/media")]
    public async Task<ActionResult<IEnumerable<MediaAssetDto>>> ListByProduct(string productKey, CancellationToken ct)
    {
        var rows = await _db.MediaAssets.AsNoTracking()
            .Where(m => m.ProductKey == productKey)
            .OrderByDescending(m => m.CreatedAt)
            .Select(m => new MediaAssetDto(m.Id, m.ProductKey, m.FileName, m.MimeType, m.SizeBytes, m.CreatedAt))
            .ToListAsync(ct);
        return rows;
    }

    /// <summary>Sirve el binario. Anonimo a propósito: los IDs son GUID y el contenido es material que el lead ve por WhatsApp igual; así el preview del admin funciona sin que el browser tenga que mandar Authorization en un img/iframe.</summary>
    [AllowAnonymous]
    [HttpGet("api/media/{id:guid}")]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct)
    {
        var asset = await _db.MediaAssets.AsNoTracking().FirstOrDefaultAsync(m => m.Id == id, ct);
        if (asset is null) return NotFound();
        return File(asset.Content, asset.MimeType, asset.FileName);
    }

    [HttpDelete("api/products/{productKey}/media/{id:guid}")]
    public async Task<IActionResult> Delete(string productKey, Guid id, CancellationToken ct)
    {
        if (!CurrentUser.IsAdmin(User)) return Forbid();
        var asset = await _db.MediaAssets.FirstOrDefaultAsync(m => m.Id == id && m.ProductKey == productKey, ct);
        if (asset is null) return NotFound();
        _db.MediaAssets.Remove(asset);
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }
}
