using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SalesHub.Core.Abstractions;
using SalesHub.Core.Domain.Entities;
using SalesHub.Core.Domain.Enums;
using SalesHub.Infrastructure.Persistence;
using SalesHub.Infrastructure.Services;

namespace SalesHub.Api.Controllers;

/// <summary>
/// Recibe lo que el userscript del vendedor (Tampermonkey) captura desde
/// Google Maps logueado. El vendedor abre Maps con SU sesión, el script lee
/// los datos del DOM (incluyendo teléfono del panel de detalle, que para
/// usuarios logueados Google sirve sin captcha), y postea acá. El backend
/// solo dedupa, asigna y persiste — nunca scrapea.
/// </summary>
[ApiController]
[Route("api/search-jobs")]
[Authorize]
public class SearchJobsController : ControllerBase
{
    private readonly ApplicationDbContext _db;
    private readonly IPhoneNormalizer _phone;
    private readonly IMessageRenderer _renderer;

    public SearchJobsController(ApplicationDbContext db, IPhoneNormalizer phone, IMessageRenderer renderer)
    {
        _db = db; _phone = phone; _renderer = renderer;
    }

    public record SuggestedQueryDto(
        string ProductKey,
        string ProductName,
        string LocalityGid2,
        string LocalityName,
        string AdminLevel1Name,
        string CountryCode,
        string CountryName,
        string Category,
        string Query,
        string MapsUrl);

    public record CapturedItem(
        string Name,
        string? Phone,
        string? Address,
        string? Website,
        double? Rating,
        int? TotalReviews,
        string? Type,
        string? BusinessStatus,
        double? Latitude,
        double? Longitude);

    public record SubmitCaptureRequest(
        string ProductKey,
        string? LocalityGid2,
        string? Category,
        string? Query,
        CapturedItem[] Items);

    public record CapturedSummary(
        Guid Id,
        string ProductKey,
        string? LocalityName,
        string? Category,
        string Query,
        int Submitted,
        int LeadsCreated,
        int Duplicates,
        int Skipped,
        DateTimeOffset CapturedAt);

    public record SearchJobDto(
        Guid Id,
        string ProductKey,
        string? LocalityName,
        string? Category,
        string Query,
        SearchJobStatus Status,
        int LeadsCreated,
        int RawItems,
        string? Error,
        DateTimeOffset ScheduledAt,
        DateTimeOffset? StartedAt,
        DateTimeOffset? FinishedAt);

    /// <summary>
    /// Cruza las localidades asignadas al caller con cada producto activo y
    /// emite la URL de Google Maps lista para abrir. El userscript inyectado
    /// agrega el botón de captura cuando esa URL se navega.
    /// </summary>
    [HttpGet("suggestions")]
    public async Task<ActionResult<IEnumerable<SuggestedQueryDto>>> Suggestions(CancellationToken ct)
    {
        var sellerId = CurrentUser.Id(User);
        var localities = await _db.SellerLocalities.AsNoTracking()
            .Where(sl => sl.SellerId == sellerId)
            .Include(sl => sl.Locality)
            .Select(sl => sl.Locality!)
            .ToListAsync(ct);
        var products = await _db.Products.AsNoTracking().Where(p => p.Active).ToListAsync(ct);

        var rows = new List<SuggestedQueryDto>();
        foreach (var p in products)
        {
            var cats = p.Categories.Count > 0 ? p.Categories : new List<string> { "" };
            foreach (var loc in localities)
            {
                foreach (var cat in cats)
                {
                    var q = string.IsNullOrWhiteSpace(cat)
                        ? $"{loc.Name}, {loc.CountryName}"
                        : $"{cat} en {loc.Name}, {loc.CountryName}";
                    var url = $"https://www.google.com/maps/search/{Uri.EscapeDataString(q)}/?hl=es-419"
                        + $"#saleshub:productKey={p.ProductKey}|gid2={loc.Gid2}|cat={Uri.EscapeDataString(cat)}";
                    rows.Add(new SuggestedQueryDto(
                        p.ProductKey, p.DisplayName, loc.Gid2, loc.Name, loc.AdminLevel1Name,
                        loc.CountryCode, loc.CountryName, cat, q, url));
                }
            }
        }
        return rows;
    }

    [HttpGet]
    public async Task<ActionResult<IEnumerable<SearchJobDto>>> List(
        [FromQuery] int limit = 50, CancellationToken ct = default)
    {
        var sellerId = CurrentUser.Id(User);
        var isAdmin = CurrentUser.IsAdmin(User);
        var q = _db.SearchJobs.AsNoTracking()
            .Include(j => j.Locality)
            .OrderByDescending(j => j.ScheduledAt)
            .AsQueryable();
        if (!isAdmin) q = q.Where(j => j.SellerId == sellerId);
        var rows = await q.Take(Math.Min(limit, 200))
            .Select(j => new SearchJobDto(
                j.Id, j.ProductKey, j.Locality != null ? j.Locality.Name : null, j.Category, j.Query,
                j.Status, j.LeadsCreated, j.RawItems, j.Error,
                j.ScheduledAt, j.StartedAt, j.FinishedAt))
            .ToListAsync(ct);
        return rows;
    }

    [HttpPost]
    public async Task<ActionResult<CapturedSummary>> Submit([FromBody] SubmitCaptureRequest req, CancellationToken ct)
    {
        var sellerId = CurrentUser.Id(User);
        if (req.Items is null || req.Items.Length == 0)
            return BadRequest(new { error = "Sin items" });

        var product = await _db.Products.FirstOrDefaultAsync(p => p.ProductKey == req.ProductKey, ct);
        if (product is null) return BadRequest(new { error = "Producto desconocido" });

        Locality? locality = null;
        if (!string.IsNullOrWhiteSpace(req.LocalityGid2))
        {
            locality = await _db.Localities.FirstOrDefaultAsync(l => l.Gid2 == req.LocalityGid2, ct);
            if (locality is null) return BadRequest(new { error = "Locality desconocida" });
            if (!CurrentUser.IsAdmin(User))
            {
                var owned = await _db.SellerLocalities
                    .AnyAsync(sl => sl.SellerId == sellerId && sl.LocalityGid2 == locality.Gid2, ct);
                if (!owned) return Forbid();
            }
        }

        var seller = await _db.Sellers.Include(s => s.EvolutionInstance).FirstAsync(s => s.Id == sellerId, ct);

        var query = req.Query?.Trim();
        if (string.IsNullOrWhiteSpace(query))
        {
            query = locality is null
                ? "(captura sin query)"
                : (string.IsNullOrWhiteSpace(req.Category)
                    ? $"{locality.Name}, {locality.CountryName}"
                    : $"{req.Category} en {locality.Name}, {locality.CountryName}");
        }

        var now = DateTimeOffset.UtcNow;
        var created = 0; var duplicates = 0; var skipped = 0;

        foreach (var item in req.Items)
        {
            if (string.IsNullOrWhiteSpace(item.Name)) { skipped++; continue; }
            var normalized = _phone.Normalize(item.Phone, product.PhonePrefix);
            // Sin teléfono no podemos contactar ni dedupear con confianza: skip.
            if (string.IsNullOrWhiteSpace(normalized)) { skipped++; continue; }

            var dup = await _db.Leads.AnyAsync(
                l => l.ProductKey == product.ProductKey && l.WhatsappPhone == normalized, ct);
            if (dup) { duplicates++; continue; }

            var lead = new Lead
            {
                Id = Guid.NewGuid(),
                ProductKey = product.ProductKey,
                Source = LeadSource.BrowserCapture,
                Name = item.Name.Trim(),
                Address = item.Address,
                RawPhone = item.Phone,
                WhatsappPhone = normalized,
                Website = item.Website,
                Rating = item.Rating,
                TotalReviews = item.TotalReviews,
                BusinessStatus = item.BusinessStatus,
                Latitude = item.Latitude,
                Longitude = item.Longitude,
                Country = product.Country,
                LocalityGid2 = locality?.Gid2,
                SearchQuery = query,
                SearchCategory = item.Type ?? req.Category,
                SellerId = seller.Id,
                AssignedAt = now,
                Status = LeadStatus.Assigned,
                CreatedAt = now,
                UpdatedAt = now
            };
            lead.RenderedMessage = _renderer.Render(lead, product, seller);
            lead.WhatsappLink = $"https://wa.me/{lead.WhatsappPhone}?text={Uri.EscapeDataString(lead.RenderedMessage ?? "")}";
            _db.Leads.Add(lead);
            created++;
        }

        var job = new SearchJob
        {
            Id = Guid.NewGuid(),
            SellerId = sellerId,
            ProductKey = product.ProductKey,
            LocalityGid2 = locality?.Gid2,
            Category = req.Category,
            Query = query!,
            Status = SearchJobStatus.Done,
            ScheduledAt = now,
            StartedAt = now,
            FinishedAt = now,
            RawItems = req.Items.Length,
            LeadsCreated = created
        };
        _db.SearchJobs.Add(job);
        await _db.SaveChangesAsync(ct);

        return new CapturedSummary(
            job.Id, job.ProductKey, locality?.Name, job.Category, job.Query,
            req.Items.Length, created, duplicates, skipped, now);
    }
}
