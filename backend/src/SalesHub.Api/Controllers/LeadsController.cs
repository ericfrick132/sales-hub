using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SalesHub.Api.Dtos;
using SalesHub.Core.Abstractions;
using SalesHub.Core.Domain.Entities;
using SalesHub.Core.Domain.Enums;
using SalesHub.Infrastructure.Persistence;
using SalesHub.Infrastructure.Services;

namespace SalesHub.Api.Controllers;

[ApiController]
[Route("api/leads")]
[Authorize]
public class LeadsController : ControllerBase
{
    private readonly ApplicationDbContext _db;
    private readonly IMessageRenderer _renderer;
    private readonly PipelineService _pipeline;
    private readonly IPhoneNormalizer _phone;

    public LeadsController(ApplicationDbContext db, IMessageRenderer renderer, PipelineService pipeline, IPhoneNormalizer phone)
    {
        _db = db; _renderer = renderer; _pipeline = pipeline; _phone = phone;
    }

    public record AssignRequest(Guid SellerId, bool AutoQueue = true);

    [HttpPost("{id:guid}/assign")]
    public async Task<ActionResult<LeadDto>> Assign(Guid id, [FromBody] AssignRequest req, CancellationToken ct)
    {
        if (!CurrentUser.IsAdmin(User)) return Forbid();
        var lead = await _db.Leads.Include(l => l.Product).FirstOrDefaultAsync(l => l.Id == id, ct);
        if (lead is null) return NotFound();

        var seller = await _db.Sellers.Include(s => s.EvolutionInstance).FirstOrDefaultAsync(s => s.Id == req.SellerId, ct);
        if (seller is null) return BadRequest(new { error = "Vendedor no encontrado" });
        if (!seller.IsActive) return BadRequest(new { error = "Vendedor inactivo" });

        lead.SellerId = seller.Id;
        lead.AssignedAt = DateTimeOffset.UtcNow;
        lead.Status = LeadStatus.Assigned;
        if (lead.Product is not null)
        {
            lead.RenderedMessage = _renderer.Render(lead, lead.Product, seller);
            lead.WhatsappLink = string.IsNullOrWhiteSpace(lead.WhatsappPhone)
                ? null
                : $"https://wa.me/{lead.WhatsappPhone}?text={Uri.EscapeDataString(lead.RenderedMessage ?? "")}";
        }

        // Si el admin pidió encolar, lo hacemos siempre que haya instancia + teléfono + mensaje.
        // El OutboxSender va a chequear SendingEnabled + Status=Connected al momento de mandar,
        // así que es seguro encolar aunque el seller esté momentáneamente desconectado o pausado:
        // los items se quedan Scheduled hasta que el seller pueda mandar.
        if (req.AutoQueue
            && seller.EvolutionInstance is not null
            && !string.IsNullOrWhiteSpace(lead.WhatsappPhone)
            && lead.RenderedMessage is not null)
        {
            _db.Outbox.Add(new MessageOutbox
            {
                Id = Guid.NewGuid(),
                LeadId = lead.Id,
                SellerId = seller.Id,
                EvolutionInstance = seller.EvolutionInstance.InstanceName,
                WhatsappPhone = lead.WhatsappPhone,
                Message = lead.RenderedMessage,
                ScheduledAt = DateTimeOffset.UtcNow,
                Status = OutboxStatus.Scheduled
            });
            lead.Status = LeadStatus.Queued;
            lead.QueuedAt = DateTimeOffset.UtcNow;
        }

        await _db.SaveChangesAsync(ct);
        lead.Seller = seller;
        return ToDto(lead);
    }

    [HttpPost("reassign-orphans")]
    public async Task<ActionResult<PipelineService.ReassignOrphansResult>> ReassignOrphans(
        [FromQuery] bool autoQueue = true, CancellationToken ct = default)
    {
        if (!CurrentUser.IsAdmin(User)) return Forbid();
        var result = await _pipeline.ReassignOrphansAsync(autoQueue, ct);
        return result;
    }

    public record MapLeadDto(
        Guid Id, string Name, string ProductKey, string? City, string? Province, string? Address,
        string? WhatsappPhone, string? SellerName,
        double Latitude, double Longitude, Core.Domain.Enums.LeadStatus Status, Guid? SellerId);

    [HttpGet("map")]
    public async Task<ActionResult<IEnumerable<MapLeadDto>>> Map(
        [FromQuery] string? productKey, [FromQuery] Guid? sellerId,
        [FromQuery] int limit = 2000, CancellationToken ct = default)
    {
        // Admins see all leads; sellers see only their own.
        var isAdmin = CurrentUser.IsAdmin(User);
        var callerId = CurrentUser.Id(User);

        var q = _db.Leads.AsNoTracking()
            .Where(l => l.Latitude != null && l.Longitude != null);
        if (!isAdmin) q = q.Where(l => l.SellerId == callerId);
        else if (sellerId is not null) q = q.Where(l => l.SellerId == sellerId);
        if (!string.IsNullOrWhiteSpace(productKey)) q = q.Where(l => l.ProductKey == productKey);

        var rows = await q.Include(l => l.Seller).OrderByDescending(l => l.CreatedAt).Take(Math.Min(limit, 5000))
            .Select(l => new MapLeadDto(l.Id, l.Name, l.ProductKey, l.City, l.Province, l.Address,
                l.WhatsappPhone, l.Seller != null ? l.Seller.DisplayName : null,
                l.Latitude!.Value, l.Longitude!.Value, l.Status, l.SellerId))
            .ToListAsync(ct);
        return rows;
    }

    [HttpGet("mine")]
    public async Task<ActionResult<IEnumerable<LeadDto>>> Mine(
        [FromQuery] LeadStatus? status, [FromQuery] string? productKey, [FromQuery] Guid? sellerId,
        [FromQuery] int limit = 200, CancellationToken ct = default)
    {
        var isAdmin = CurrentUser.IsAdmin(User);
        var callerId = CurrentUser.Id(User);
        var q = _db.Leads.AsNoTracking()
            .Include(l => l.Product)
            .Include(l => l.Seller)
            .AsQueryable();
        // Admins see all leads (with optional ?sellerId= filter); sellers see only their own.
        if (!isAdmin) q = q.Where(l => l.SellerId == callerId);
        else if (sellerId is not null) q = q.Where(l => l.SellerId == sellerId);
        if (status is not null) q = q.Where(l => l.Status == status);
        if (!string.IsNullOrWhiteSpace(productKey)) q = q.Where(l => l.ProductKey == productKey);
        q = q.OrderByDescending(l => l.AssignedAt ?? l.CreatedAt).Take(Math.Min(limit, 500));
        return (await q.ToListAsync(ct)).Select(ToDto).ToList();
    }

    [HttpGet("pool")]
    public async Task<ActionResult<IEnumerable<LeadDto>>> Pool(
        [FromQuery] string? productKey, [FromQuery] int limit = 200, CancellationToken ct = default)
    {
        var q = _db.Leads.AsNoTracking()
            .Include(l => l.Product)
            .Where(l => l.SellerId == null && l.Status == LeadStatus.New);
        if (!string.IsNullOrWhiteSpace(productKey)) q = q.Where(l => l.ProductKey == productKey);
        q = q.OrderByDescending(l => l.CreatedAt).Take(Math.Min(limit, 500));
        return (await q.ToListAsync(ct)).Select(ToDto).ToList();
    }

    [HttpPost("{id:guid}/claim")]
    public async Task<ActionResult<LeadDto>> Claim(Guid id, CancellationToken ct)
    {
        var sellerId = CurrentUser.Id(User);
        var lead = await _db.Leads.Include(l => l.Product).FirstOrDefaultAsync(l => l.Id == id, ct);
        if (lead is null) return NotFound();
        if (lead.SellerId is not null && lead.SellerId != sellerId) return Conflict();

        var seller = await _db.Sellers.FirstAsync(s => s.Id == sellerId, ct);
        lead.SellerId = sellerId;
        lead.AssignedAt = DateTimeOffset.UtcNow;
        lead.Status = LeadStatus.Assigned;
        if (lead.Product is not null)
        {
            lead.RenderedMessage = _renderer.Render(lead, lead.Product, seller);
            if (!string.IsNullOrWhiteSpace(lead.WhatsappPhone))
                lead.WhatsappLink = $"https://wa.me/{lead.WhatsappPhone}?text={Uri.EscapeDataString(lead.RenderedMessage ?? "")}";
        }
        await _db.SaveChangesAsync(ct);
        lead.Seller = seller;
        return ToDto(lead);
    }

    [HttpPost("{id:guid}/release")]
    public async Task<ActionResult<LeadDto>> Release(Guid id, CancellationToken ct)
    {
        var sellerId = CurrentUser.Id(User);
        var lead = await _db.Leads.Include(l => l.Product).FirstOrDefaultAsync(l => l.Id == id, ct);
        if (lead is null) return NotFound();
        if (lead.SellerId != sellerId && !CurrentUser.IsAdmin(User)) return Forbid();

        lead.SellerId = null;
        lead.AssignedAt = null;
        lead.Status = LeadStatus.New;
        await _db.SaveChangesAsync(ct);
        return ToDto(lead);
    }

    [HttpPatch("{id:guid}")]
    public async Task<ActionResult<LeadDto>> UpdateStatus(Guid id, [FromBody] UpdateLeadStatusRequest req, CancellationToken ct)
    {
        var sellerId = CurrentUser.Id(User);
        var lead = await _db.Leads.Include(l => l.Product).Include(l => l.Seller).FirstOrDefaultAsync(l => l.Id == id, ct);
        if (lead is null) return NotFound();
        if (lead.SellerId != sellerId && !CurrentUser.IsAdmin(User)) return Forbid();

        lead.Status = req.Status;
        if (req.Notes is not null) lead.Notes = req.Notes;
        if (req.Status == LeadStatus.Replied && lead.FirstReplyAt is null) lead.FirstReplyAt = DateTimeOffset.UtcNow;
        if (req.Status is LeadStatus.Closed or LeadStatus.Lost) lead.ClosedAt = DateTimeOffset.UtcNow;
        lead.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);
        return ToDto(lead);
    }

    [HttpPatch("{id:guid}/info")]
    public async Task<ActionResult<LeadDto>> UpdateInfo(Guid id, [FromBody] UpdateLeadInfoRequest req, CancellationToken ct)
    {
        var sellerId = CurrentUser.Id(User);
        var lead = await _db.Leads.Include(l => l.Product).Include(l => l.Seller).FirstOrDefaultAsync(l => l.Id == id, ct);
        if (lead is null) return NotFound();
        if (lead.SellerId != sellerId && !CurrentUser.IsAdmin(User)) return Forbid();

        var name = req.Name?.Trim();
        if (!string.IsNullOrWhiteSpace(name)) lead.Name = name;

        var phoneChanged = false;
        if (req.WhatsappPhone is not null)
        {
            var phone = req.WhatsappPhone.Trim();
            var newPhone = string.IsNullOrWhiteSpace(phone) ? null : phone;
            if (newPhone != lead.WhatsappPhone)
            {
                lead.WhatsappPhone = newPhone;
                phoneChanged = true;
            }
        }

        if ((phoneChanged || !string.IsNullOrWhiteSpace(name)) && lead.Product is not null && lead.Seller is not null)
        {
            lead.RenderedMessage = _renderer.Render(lead, lead.Product, lead.Seller);
            lead.WhatsappLink = string.IsNullOrWhiteSpace(lead.WhatsappPhone)
                ? null
                : $"https://wa.me/{lead.WhatsappPhone}?text={Uri.EscapeDataString(lead.RenderedMessage ?? "")}";
        }

        lead.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);
        return ToDto(lead);
    }

    [HttpPost("{id:guid}/queue")]
    public async Task<ActionResult<LeadDto>> Queue(Guid id, [FromBody] QueueLeadRequest? req, CancellationToken ct)
    {
        var sellerId = CurrentUser.Id(User);
        var lead = await _db.Leads.Include(l => l.Product).Include(l => l.Seller).ThenInclude(s => s!.EvolutionInstance)
            .FirstOrDefaultAsync(l => l.Id == id, ct);
        if (lead is null) return NotFound();
        if (lead.SellerId != sellerId && !CurrentUser.IsAdmin(User)) return Forbid();
        if (string.IsNullOrWhiteSpace(lead.WhatsappPhone)) return BadRequest(new { error = "Lead sin teléfono WhatsApp" });

        if (lead.SellerId is null || lead.Seller is null)
            return BadRequest(new { error = "Lead sin vendedor asignado. Asignalo primero." });

        var seller = lead.Seller;
        if (seller.EvolutionInstance is null)
            return BadRequest(new { error = "El vendedor no tiene instancia de WhatsApp configurada." });

        // No exigimos Status==Connected acá: el OutboxSender ya filtra al momento de mandar.
        // Si está desconectado, el item se queda Scheduled hasta que reconecte.
        var msg = lead.RenderedMessage ?? (lead.Product is null ? "" : _renderer.Render(lead, lead.Product, seller));
        _db.Outbox.Add(new MessageOutbox
        {
            Id = Guid.NewGuid(),
            LeadId = lead.Id,
            SellerId = seller.Id,
            EvolutionInstance = seller.EvolutionInstance.InstanceName,
            WhatsappPhone = lead.WhatsappPhone,
            Message = msg,
            ScheduledAt = req?.At ?? DateTimeOffset.UtcNow,
            Status = OutboxStatus.Scheduled
        });
        lead.Status = LeadStatus.Queued;
        lead.QueuedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);
        return ToDto(lead);
    }

    public record SimilarLeadDto(Guid Id, string Name, string ProductKey, string? ProductName, LeadStatus Status, Guid? SellerId, string? SellerName, DateTimeOffset CreatedAt);

    [HttpGet("search")]
    public async Task<ActionResult<IEnumerable<SimilarLeadDto>>> SearchSimilar([FromQuery] string q, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(q) || q.Trim().Length < 3) return new List<SimilarLeadDto>();
        var needle = $"%{q.Trim()}%";
        // Cross-user lookup: a seller should see if another seller already loaded the same lead
        // so the team avoids contacting the same business twice. Seller name is included so the
        // duplicate-finder knows who to coordinate with.
        var rows = await _db.Leads.AsNoTracking().Include(l => l.Product).Include(l => l.Seller)
            .Where(l => EF.Functions.ILike(l.Name, needle))
            .OrderByDescending(l => l.CreatedAt).Take(8)
            .Select(l => new SimilarLeadDto(l.Id, l.Name, l.ProductKey, l.Product != null ? l.Product.DisplayName : null,
                l.Status, l.SellerId, l.Seller != null ? l.Seller.DisplayName : null, l.CreatedAt))
            .ToListAsync(ct);
        return rows;
    }

    [HttpPost("bulk-import")]
    public async Task<ActionResult<BulkImportResult>> BulkImport([FromBody] BulkImportRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.RawText))
            return BadRequest(new { error = "Falta el texto a importar" });
        if (string.IsNullOrWhiteSpace(req.ProductKey))
            return BadRequest(new { error = "Falta el producto" });

        var product = await _db.Products.FirstOrDefaultAsync(p => p.ProductKey == req.ProductKey, ct);
        if (product is null)
            return BadRequest(new { error = $"Producto '{req.ProductKey}' no existe" });

        var callerId = CurrentUser.Id(User);
        var isAdmin = CurrentUser.IsAdmin(User);
        Guid? sellerId = req.AssignToCaller
            ? callerId
            : (isAdmin ? req.SellerId : null);

        Seller? seller = null;
        if (sellerId is not null)
        {
            seller = await _db.Sellers.FirstOrDefaultAsync(s => s.Id == sellerId.Value, ct);
            if (seller is null) return BadRequest(new { error = "Vendedor no encontrado" });
        }

        var parsed = MapsTextParser.Parse(req.RawText);
        var now = DateTimeOffset.UtcNow;
        var items = new List<BulkImportItem>();

        // Dedupe set para el mismo batch (varios items con mismo phone en el paste).
        var seenInBatch = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var p in parsed)
        {
            if (string.Equals(p.BusinessStatus, "permanently_closed", StringComparison.OrdinalIgnoreCase))
            {
                items.Add(new BulkImportItem(p.Name, p.Phone, p.Address, p.Rating, p.TotalReviews, "closed", "Cerrado permanentemente"));
                continue;
            }

            var normalized = _phone.Normalize(p.Phone, product.PhonePrefix);

            // Dedupe within the batch — by phone if present, else by name+product.
            var batchKey = normalized ?? $"name:{p.Name.ToLowerInvariant()}";
            if (!seenInBatch.Add(batchKey))
            {
                items.Add(new BulkImportItem(p.Name, normalized, p.Address, p.Rating, p.TotalReviews, "duplicate", "Duplicado en el paste"));
                continue;
            }

            // Dedupe vs DB.
            bool existsInDb;
            if (!string.IsNullOrWhiteSpace(normalized))
            {
                existsInDb = await _db.Leads.AnyAsync(l => l.ProductKey == product.ProductKey && l.WhatsappPhone == normalized, ct);
            }
            else
            {
                var lower = p.Name.Trim().ToLower();
                existsInDb = await _db.Leads.AnyAsync(l => l.ProductKey == product.ProductKey && l.Name.ToLower() == lower, ct);
            }
            if (existsInDb)
            {
                items.Add(new BulkImportItem(p.Name, normalized, p.Address, p.Rating, p.TotalReviews, "duplicate", "Ya existe en la base"));
                continue;
            }

            try
            {
                var lead = new Lead
                {
                    Id = Guid.NewGuid(),
                    ProductKey = product.ProductKey,
                    Source = req.Source,
                    Name = p.Name.Trim(),
                    City = string.IsNullOrWhiteSpace(req.City) ? null : req.City.Trim(),
                    WhatsappPhone = normalized,
                    Address = p.Address,
                    Rating = p.Rating,
                    TotalReviews = p.TotalReviews,
                    BusinessStatus = p.BusinessStatus,
                    SearchQuery = "bulk-import",
                    SearchCategory = p.Type,
                    SellerId = sellerId,
                    AssignedAt = sellerId is not null ? now : null,
                    Status = sellerId is not null && req.Status == LeadStatus.New
                        ? LeadStatus.Assigned
                        : req.Status,
                    CreatedAt = now,
                    UpdatedAt = now
                };

                if (lead.Status >= LeadStatus.Sent) lead.SentAt = now;

                _db.Leads.Add(lead);
                await _db.SaveChangesAsync(ct);

                items.Add(new BulkImportItem(p.Name, normalized, p.Address, p.Rating, p.TotalReviews, "inserted", null, lead.Id));
            }
            catch (Exception ex)
            {
                items.Add(new BulkImportItem(p.Name, normalized, p.Address, p.Rating, p.TotalReviews, "error", ex.Message));
            }
        }

        return new BulkImportResult(
            Parsed: parsed.Count,
            Inserted: items.Count(i => i.Outcome == "inserted"),
            Duplicates: items.Count(i => i.Outcome == "duplicate"),
            Closed: items.Count(i => i.Outcome == "closed"),
            Errors: items.Count(i => i.Outcome == "error"),
            Items: items);
    }

    [HttpPost]
    public async Task<ActionResult<LeadDto>> CreateManual([FromBody] CreateManualLeadRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.Name)) return BadRequest(new { error = "Falta el nombre del lead" });
        if (string.IsNullOrWhiteSpace(req.ProductKey)) return BadRequest(new { error = "Falta el producto" });

        var product = await _db.Products.FirstOrDefaultAsync(p => p.ProductKey == req.ProductKey, ct);
        if (product is null) return BadRequest(new { error = $"Producto '{req.ProductKey}' no existe" });

        var callerId = CurrentUser.Id(User);
        var isAdmin = CurrentUser.IsAdmin(User);
        var sellerId = isAdmin && req.SellerId is not null ? req.SellerId.Value : callerId;
        var seller = await _db.Sellers.FirstOrDefaultAsync(s => s.Id == sellerId, ct);
        if (seller is null) return BadRequest(new { error = "Vendedor no encontrado" });

        var now = DateTimeOffset.UtcNow;
        var status = req.Status ?? LeadStatus.Sent;
        var lead = new Lead
        {
            Id = Guid.NewGuid(),
            ProductKey = req.ProductKey,
            Source = req.Source,
            Name = req.Name.Trim(),
            City = string.IsNullOrWhiteSpace(req.City) ? null : req.City.Trim(),
            WhatsappPhone = string.IsNullOrWhiteSpace(req.WhatsappPhone) ? null : req.WhatsappPhone.Trim(),
            InstagramHandle = string.IsNullOrWhiteSpace(req.InstagramHandle) ? null : req.InstagramHandle.Trim(),
            Website = string.IsNullOrWhiteSpace(req.Website) ? null : req.Website.Trim(),
            Notes = string.IsNullOrWhiteSpace(req.Notes) ? null : req.Notes.Trim(),
            SellerId = sellerId,
            AssignedAt = now,
            Status = status,
            CreatedAt = now,
            UpdatedAt = now
        };

        // Mark timestamps based on status, since the seller is recording past activity.
        if (status >= LeadStatus.Sent) lead.SentAt = now;
        if (status == LeadStatus.Replied) lead.FirstReplyAt = now;
        if (status is LeadStatus.Closed or LeadStatus.Lost) lead.ClosedAt = now;

        _db.Leads.Add(lead);
        await _db.SaveChangesAsync(ct);

        await _db.Entry(lead).Reference(l => l.Product).LoadAsync(ct);
        await _db.Entry(lead).Reference(l => l.Seller).LoadAsync(ct);
        return ToDto(lead);
    }

    private static LeadDto ToDto(Lead l) => new(
        l.Id, l.ProductKey, l.Product?.DisplayName, l.Source, l.Name, l.City, l.Province,
        l.WhatsappPhone, l.Website, l.InstagramHandle, l.FacebookUrl, l.Rating, l.TotalReviews,
        l.Score, l.Status, l.SellerId, l.Seller?.DisplayName, l.RenderedMessage, l.WhatsappLink,
        l.AssignedAt, l.SentAt, l.FirstReplyAt, l.Notes, l.CreatedAt);
}
