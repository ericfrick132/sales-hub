using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SalesHub.Api.Dtos;
using SalesHub.Core.Domain.Entities;
using SalesHub.Infrastructure.Persistence;

namespace SalesHub.Api.Controllers;

[ApiController]
[Route("api/products")]
[Authorize]
public class ProductsController : ControllerBase
{
    private readonly ApplicationDbContext _db;
    public ProductsController(ApplicationDbContext db) { _db = db; }

    [HttpGet]
    public async Task<ActionResult<IEnumerable<ProductDto>>> List(CancellationToken ct)
    {
        var products = await _db.Products.OrderBy(p => p.DisplayName).ToListAsync(ct);
        return products.Select(ToDto).ToList();
    }

    [HttpPost]
    public async Task<ActionResult<ProductDto>> Create([FromBody] CreateOrUpdateProductRequest req, CancellationToken ct)
    {
        if (!CurrentUser.IsAdmin(User)) return Forbid();
        if (await _db.Products.AnyAsync(p => p.ProductKey == req.ProductKey, ct))
            return Conflict(new { error = "product_key duplicado" });
        var p = Map(new Product { Id = Guid.NewGuid() }, req);
        _db.Products.Add(p);
        await _db.SaveChangesAsync(ct);
        return ToDto(p);
    }

    [HttpPut("{id:guid}")]
    public async Task<ActionResult<ProductDto>> Update(Guid id, [FromBody] CreateOrUpdateProductRequest req, CancellationToken ct)
    {
        if (!CurrentUser.IsAdmin(User)) return Forbid();
        var p = await _db.Products.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (p is null) return NotFound();
        Map(p, req);
        p.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);
        return ToDto(p);
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        if (!CurrentUser.IsAdmin(User)) return Forbid();
        var p = await _db.Products.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (p is null) return NotFound();
        p.Active = false;
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }

    private static Product Map(Product p, CreateOrUpdateProductRequest r)
    {
        p.ProductKey = r.ProductKey;
        p.DisplayName = r.DisplayName;
        p.Active = r.Active;
        p.Country = r.Country;
        p.CountryName = r.CountryName;
        p.RegionCode = r.RegionCode;
        p.Language = r.Language;
        p.PhonePrefix = r.PhonePrefix;
        p.Categories = r.Categories;
        p.MessageTemplate = r.MessageTemplate;
        p.OpenerTemplate = r.OpenerTemplate ?? string.Empty;
        p.CheckoutUrl = r.CheckoutUrl;
        p.PriceDisplay = r.PriceDisplay;
        p.DailyLimit = r.DailyLimit;
        p.TriggerHours = r.TriggerHours;
        p.SendHourStart = Math.Clamp(r.SendHourStart, 0, 24);
        p.SendHourEnd = Math.Clamp(r.SendHourEnd, 0, 24);
        p.RequiresAssistedSale = r.RequiresAssistedSale;
        p.GooglePlacesDailyLeadCap = r.GooglePlacesDailyLeadCap;
        return p;
    }

    private static ProductDto ToDto(Product p) => new(
        p.Id, p.ProductKey, p.DisplayName, p.Active, p.Country, p.CountryName, p.RegionCode, p.Language,
        p.PhonePrefix, p.Categories, p.MessageTemplate, p.OpenerTemplate ?? string.Empty,
        p.CheckoutUrl, p.PriceDisplay,
        p.DailyLimit, p.TriggerHours, p.SendHourStart, p.SendHourEnd,
        p.RequiresAssistedSale, p.GooglePlacesDailyLeadCap);
}
