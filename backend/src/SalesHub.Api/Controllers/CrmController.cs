using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SalesHub.Core.Abstractions;
using SalesHub.Core.Domain.Entities;
using SalesHub.Core.Domain.Enums;
using SalesHub.Infrastructure.Persistence;

namespace SalesHub.Api.Controllers;

/// <summary>
/// CRM: el mismo lead que ya vive en el sistema, visto como pipeline comercial.
/// Las etapas son <see cref="LeadStatus"/> agrupado — NO un estado paralelo: si el
/// pipeline tuviera su propia columna, las métricas, los workers y el follow-up
/// quedarían mirando una verdad distinta de la que ve el que vende.
/// </summary>
[ApiController]
[Route("api/crm")]
[Authorize]
public class CrmController : ControllerBase
{
    private readonly ApplicationDbContext _db;
    private readonly IProductStatusNotifier _statusNotifier;

    public CrmController(ApplicationDbContext db, IProductStatusNotifier statusNotifier)
    {
        _db = db; _statusNotifier = statusNotifier;
    }

    /// <summary>
    /// Etapas del tablero. Cada una agrupa los <see cref="LeadStatus"/> que significan lo
    /// mismo para el que vende; <c>Target</c> es el estado que se escribe al soltar una
    /// tarjeta ahí.
    /// </summary>
    public record Stage(string Key, string Label, LeadStatus Target, LeadStatus[] Statuses);

    private static readonly Stage[] Stages =
    {
        new("nuevo", "Nuevos", LeadStatus.Assigned, new[] { LeadStatus.New, LeadStatus.Assigned, LeadStatus.Queued }),
        new("contactado", "Contactados", LeadStatus.Sent, new[] { LeadStatus.Sent }),
        new("respondio", "Respondieron", LeadStatus.Replied, new[] { LeadStatus.Replied }),
        new("interesado", "Interesados", LeadStatus.Interested, new[] { LeadStatus.Interested }),
        new("demo", "Demo agendada", LeadStatus.DemoScheduled, new[] { LeadStatus.DemoScheduled }),
        new("ganado", "Ganados", LeadStatus.Closed, new[] { LeadStatus.Closed }),
        new("perdido", "Perdidos", LeadStatus.Lost, new[] { LeadStatus.Lost, LeadStatus.Blocked, LeadStatus.NoWhatsApp }),
    };

    public record CrmCard(
        Guid Id, string Name, string? City, string ProductKey, string? ProductName,
        string? Phone, string Status, string StageKey, string Source,
        Guid? SellerId, string? SellerName, Guid? DeviceId, string? DeviceName,
        DateTimeOffset? LastActivityAt, DateTimeOffset? NextActionAt, string? NextActionNote,
        int NoteCount, string? LastNote, int UnreadCount, int Score,
        DateTimeOffset CreatedAt);

    public record StageColumn(string Key, string Label, int Total, IReadOnlyList<CrmCard> Cards);

    /// <summary>
    /// Tablero completo. Todos los filtros son opcionales y se combinan.
    /// <paramref name="q"/> busca por nombre, teléfono o ciudad.
    /// <paramref name="stalledDays"/> deja sólo los que no tienen movimiento hace N días.
    /// <paramref name="due"/> = "today" | "overdue": filtra por la próxima acción comprometida.
    /// </summary>
    [HttpGet("board")]
    public async Task<IActionResult> Board(
        [FromQuery] string? q,
        [FromQuery] string? productKey,
        [FromQuery] Guid? sellerId,
        [FromQuery] Guid? deviceId,
        [FromQuery] LeadSource[]? source,
        [FromQuery] bool onlyMine = false,
        [FromQuery] int? stalledDays = null,
        [FromQuery] string? due = null,
        [FromQuery] int perStage = 50,
        CancellationToken ct = default)
    {
        perStage = Math.Clamp(perStage, 5, 200);
        var callerId = CurrentUser.Id(User);

        var devices = await _db.Devices.AsNoTracking()
            .Select(d => new { d.Id, d.Name, d.SellerId }).ToListAsync(ct);

        var leadQ = _db.Leads.AsNoTracking().Include(l => l.Product).Include(l => l.Seller).AsQueryable();

        if (onlyMine) leadQ = leadQ.Where(l => l.SellerId == callerId);
        else if (sellerId is not null) leadQ = leadQ.Where(l => l.SellerId == sellerId);

        if (deviceId is not null)
        {
            var dev = devices.FirstOrDefault(d => d.Id == deviceId);
            if (dev?.SellerId is null) return Ok(new { stages = Stages.Select(s => new StageColumn(s.Key, s.Label, 0, Array.Empty<CrmCard>())), total = 0 });
            leadQ = leadQ.Where(l => l.SellerId == dev.SellerId);
        }

        if (!string.IsNullOrWhiteSpace(productKey)) leadQ = leadQ.Where(l => l.ProductKey == productKey);
        if (source is { Length: > 0 }) leadQ = leadQ.Where(l => source.Contains(l.Source));

        if (!string.IsNullOrWhiteSpace(q))
        {
            var term = q.Trim().ToLower();
            var digits = new string(term.Where(char.IsDigit).ToArray());
            leadQ = leadQ.Where(l =>
                l.Name.ToLower().Contains(term)
                || (l.City != null && l.City.ToLower().Contains(term))
                || (digits.Length >= 4 && l.WhatsappPhone != null && l.WhatsappPhone.Contains(digits)));
        }

        var now = DateTimeOffset.UtcNow;
        if (stalledDays is > 0)
        {
            var cutoff = now.AddDays(-stalledDays.Value);
            leadQ = leadQ.Where(l => l.UpdatedAt <= cutoff);
        }
        switch ((due ?? "").ToLowerInvariant())
        {
            case "today":
                leadQ = leadQ.Where(l => l.NextActionAt != null && l.NextActionAt <= now.AddDays(1).Date);
                break;
            case "overdue":
                leadQ = leadQ.Where(l => l.NextActionAt != null && l.NextActionAt < now);
                break;
        }

        // Los totales salen de un GROUP BY sobre columnas reales — nada de traer los 12k
        // leads a memoria para después quedarse con 50 por columna.
        var counts = await leadQ
            .GroupBy(l => l.Status)
            .Select(g => new { Status = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Status, x => x.Count, ct);

        var overdue = await leadQ.CountAsync(l => l.NextActionAt != null && l.NextActionAt < now, ct);

        var deviceBySeller = devices.Where(d => d.SellerId != null)
            .GroupBy(d => d.SellerId!.Value)
            .ToDictionary(g => g.Key, g => g.OrderBy(x => x.Name).First());

        // Una query por etapa, ya ordenada y cortada en SQL: sólo bajan las tarjetas visibles.
        // El orden va por columnas indexables (vencimiento y última actualización); el dato
        // del chat se resuelve sólo para esas pocas filas.
        var columns = new List<StageColumn>();
        foreach (var s in Stages)
        {
            var top = await leadQ
                .Where(l => s.Statuses.Contains(l.Status))
                // OrderBy sobre la columna cruda, sin COALESCE: en Postgres un ASC ya manda
                // los NULL al final (los que no tienen recordatorio van después), y así el
                // sort puede apoyarse en el índice (status, next_action_at, updated_at).
                .OrderBy(l => l.NextActionAt)
                .ThenByDescending(l => l.UpdatedAt)
                .Take(perStage)
                .Select(l => new
                {
                    l.Id, l.Name, l.City, l.ProductKey,
                    ProductName = l.Product != null ? l.Product.DisplayName : null,
                    l.WhatsappPhone, l.Status, l.Source, l.SellerId,
                    SellerName = l.Seller != null ? l.Seller.DisplayName : null,
                    l.NextActionAt, l.NextActionNote, l.Score, l.CreatedAt, l.UpdatedAt,
                    LastMessageAt = _db.ConversationMessages.Where(m => m.LeadId == l.Id)
                        .OrderByDescending(m => m.Timestamp).Select(m => (DateTimeOffset?)m.Timestamp).FirstOrDefault(),
                    Unread = _db.ConversationMessages.Count(m => m.LeadId == l.Id
                        && m.Direction == MessageDirection.Inbound && !m.IsRead),
                    NoteCount = _db.LeadNotes.Count(n => n.LeadId == l.Id),
                    LastNote = _db.LeadNotes.Where(n => n.LeadId == l.Id && n.Kind == LeadNoteKind.Note)
                        .OrderByDescending(n => n.CreatedAt).Select(n => n.Text).FirstOrDefault(),
                })
                .ToListAsync(ct);

            var cards = top.Select(r =>
            {
                deviceBySeller.TryGetValue(r.SellerId ?? Guid.Empty, out var dev);
                return new CrmCard(
                    r.Id, r.Name, r.City, r.ProductKey, r.ProductName, r.WhatsappPhone,
                    r.Status.ToString(), s.Key, r.Source.ToString(),
                    r.SellerId, r.SellerName, dev?.Id, dev?.Name,
                    r.LastMessageAt ?? r.UpdatedAt,
                    r.NextActionAt, r.NextActionNote,
                    r.NoteCount, r.LastNote, r.Unread, r.Score, r.CreatedAt);
            }).ToList();

            columns.Add(new StageColumn(s.Key, s.Label, s.Statuses.Sum(st => counts.GetValueOrDefault(st)), cards));
        }

        return Ok(new
        {
            stages = columns,
            total = counts.Values.Sum(),
            overdue,
            perStage,
        });
    }

    public record NoteDto(Guid Id, string Text, string Kind, DateTimeOffset CreatedAt, Guid? SellerId, string? SellerName);

    /// <summary>Ficha del lead: sus datos, su bitácora y los últimos mensajes del chat.</summary>
    [HttpGet("leads/{id:guid}")]
    public async Task<IActionResult> Detail(Guid id, CancellationToken ct)
    {
        var lead = await _db.Leads.AsNoTracking()
            .Include(l => l.Product).Include(l => l.Seller)
            .FirstOrDefaultAsync(l => l.Id == id, ct);
        if (lead is null) return NotFound();

        var notes = await _db.LeadNotes.AsNoTracking()
            .Where(n => n.LeadId == id)
            .OrderByDescending(n => n.CreatedAt)
            .Select(n => new NoteDto(n.Id, n.Text, n.Kind.ToString(), n.CreatedAt, n.SellerId,
                n.Seller != null ? n.Seller.DisplayName : null))
            .Take(100)
            .ToListAsync(ct);

        var messages = await _db.ConversationMessages.AsNoTracking()
            .Where(m => m.LeadId == id)
            .OrderByDescending(m => m.Timestamp)
            .Take(10)
            .Select(m => new { m.Direction, m.Text, m.Timestamp })
            .ToListAsync(ct);

        return Ok(new
        {
            id = lead.Id,
            name = lead.Name,
            phone = lead.WhatsappPhone,
            city = lead.City,
            province = lead.Province,
            website = lead.Website,
            instagram = lead.InstagramHandle,
            productKey = lead.ProductKey,
            productName = lead.Product?.DisplayName,
            status = lead.Status.ToString(),
            source = lead.Source.ToString(),
            score = lead.Score,
            sellerId = lead.SellerId,
            sellerName = lead.Seller?.DisplayName,
            createdAt = lead.CreatedAt,
            sentAt = lead.SentAt,
            firstReplyAt = lead.FirstReplyAt,
            demoScheduledAt = lead.DemoScheduledAt,
            closedAt = lead.ClosedAt,
            nextActionAt = lead.NextActionAt,
            nextActionNote = lead.NextActionNote,
            legacyNotes = lead.Notes,
            notes,
            messages = messages.OrderBy(m => m.Timestamp).ToList(),
        });
    }

    public record AddNoteRequest(string Text);

    /// <summary>Agrega una nota libre a la bitácora del lead.</summary>
    [HttpPost("leads/{id:guid}/notes")]
    public async Task<IActionResult> AddNote(Guid id, [FromBody] AddNoteRequest req, CancellationToken ct)
    {
        var text = (req.Text ?? "").Trim();
        if (text.Length == 0) return BadRequest(new { error = "La nota está vacía" });

        var exists = await _db.Leads.AnyAsync(l => l.Id == id, ct);
        if (!exists) return NotFound();

        var note = new LeadNote
        {
            Id = Guid.NewGuid(),
            LeadId = id,
            SellerId = CurrentUser.Id(User),
            Kind = LeadNoteKind.Note,
            Text = text,
        };
        _db.LeadNotes.Add(note);

        // La nota es actividad: mueve el reloj del lead para que no figure como estancado.
        await _db.Leads.Where(l => l.Id == id)
            .ExecuteUpdateAsync(s => s.SetProperty(l => l.UpdatedAt, DateTimeOffset.UtcNow), ct);
        await _db.SaveChangesAsync(ct);

        var author = await _db.Sellers.AsNoTracking().Where(s => s.Id == note.SellerId)
            .Select(s => s.DisplayName).FirstOrDefaultAsync(ct);
        return Ok(new NoteDto(note.Id, note.Text, note.Kind.ToString(), note.CreatedAt, note.SellerId, author));
    }

    [HttpDelete("notes/{noteId:guid}")]
    public async Task<IActionResult> DeleteNote(Guid noteId, CancellationToken ct)
    {
        var note = await _db.LeadNotes.FirstOrDefaultAsync(n => n.Id == noteId, ct);
        if (note is null) return NotFound();
        if (note.Kind != LeadNoteKind.Note) return BadRequest(new { error = "El historial automático no se borra" });
        if (note.SellerId != CurrentUser.Id(User) && !CurrentUser.IsAdmin(User)) return Forbid();

        _db.LeadNotes.Remove(note);
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }

    public record MoveStageRequest(string Stage, string? Note);

    /// <summary>
    /// Mueve el lead de etapa (lo que hace el drag &amp; drop del tablero). Escribe el
    /// LeadStatus real, sella las fechas del embudo y deja rastro en la bitácora.
    /// </summary>
    [HttpPatch("leads/{id:guid}/stage")]
    public async Task<IActionResult> MoveStage(Guid id, [FromBody] MoveStageRequest req, CancellationToken ct)
    {
        var stage = Stages.FirstOrDefault(s => s.Key == (req.Stage ?? "").ToLowerInvariant());
        if (stage is null) return BadRequest(new { error = $"Etapa desconocida: {req.Stage}" });

        var lead = await _db.Leads.FirstOrDefaultAsync(l => l.Id == id, ct);
        if (lead is null) return NotFound();

        var before = lead.Status;
        // Si ya está en un estado que pertenece a la etapa destino, no lo pisamos: mover
        // "Perdidos" no debe convertir un Blocked en Lost y perder el motivo real.
        var target = stage.Statuses.Contains(before) ? before : stage.Target;
        // Un lead sin vendedor no puede quedar en Assigned (mentiría el tablero de reparto).
        if (target == LeadStatus.Assigned && lead.SellerId is null) target = LeadStatus.New;

        var now = DateTimeOffset.UtcNow;
        lead.Status = target;
        if (target == LeadStatus.Replied && lead.FirstReplyAt is null) lead.FirstReplyAt = now;
        if (target == LeadStatus.DemoScheduled && lead.DemoScheduledAt is null) lead.DemoScheduledAt = now;
        if (target is LeadStatus.Closed or LeadStatus.Lost) lead.ClosedAt ??= now;
        // Volver atrás desde un estado terminal reabre el lead.
        if (target is not (LeadStatus.Closed or LeadStatus.Lost)) lead.ClosedAt = null;
        lead.UpdatedAt = now;

        var actor = await _db.Sellers.AsNoTracking().Where(s => s.Id == CurrentUser.Id(User))
            .Select(s => s.DisplayName).FirstOrDefaultAsync(ct);
        var trail = $"{StageLabelOf(before)} → {stage.Label}" + (actor is null ? "" : $" (por {actor})");
        _db.LeadNotes.Add(new LeadNote
        {
            Id = Guid.NewGuid(),
            LeadId = id,
            SellerId = CurrentUser.Id(User),
            Kind = LeadNoteKind.StageChange,
            Text = trail,
        });

        if (!string.IsNullOrWhiteSpace(req.Note))
        {
            _db.LeadNotes.Add(new LeadNote
            {
                Id = Guid.NewGuid(),
                LeadId = id,
                SellerId = CurrentUser.Id(User),
                Kind = LeadNoteKind.Note,
                Text = req.Note!.Trim(),
            });
        }

        await _db.SaveChangesAsync(ct);

        // Mismo status-back que el PATCH clásico: el producto de origen se entera del cierre.
        await _statusNotifier.NotifyAsync(lead.ProductKey, lead.ExternalId, target.ToString(),
            target == LeadStatus.Closed, ct);

        return Ok(new { id = lead.Id, status = lead.Status.ToString(), stage = stage.Key });
    }

    public record NextActionRequest(DateTimeOffset? At, string? Note);

    /// <summary>Fija (o limpia, mandando At=null) el recordatorio de próxima acción.</summary>
    [HttpPatch("leads/{id:guid}/next-action")]
    public async Task<IActionResult> SetNextAction(Guid id, [FromBody] NextActionRequest req, CancellationToken ct)
    {
        var lead = await _db.Leads.FirstOrDefaultAsync(l => l.Id == id, ct);
        if (lead is null) return NotFound();

        lead.NextActionAt = req.At;
        lead.NextActionNote = string.IsNullOrWhiteSpace(req.Note) ? null : req.Note!.Trim();
        lead.UpdatedAt = DateTimeOffset.UtcNow;

        _db.LeadNotes.Add(new LeadNote
        {
            Id = Guid.NewGuid(),
            LeadId = id,
            SellerId = CurrentUser.Id(User),
            Kind = LeadNoteKind.System,
            Text = req.At is null
                ? "Recordatorio quitado"
                : $"Próxima acción: {req.At:dd/MM HH:mm}" + (lead.NextActionNote is null ? "" : $" — {lead.NextActionNote}"),
        });

        await _db.SaveChangesAsync(ct);
        return Ok(new { id = lead.Id, nextActionAt = lead.NextActionAt, nextActionNote = lead.NextActionNote });
    }

    /// <summary>Las etapas y a qué estados corresponde cada una (para pintar el tablero).</summary>
    [HttpGet("stages")]
    public IActionResult GetStages() =>
        Ok(Stages.Select(s => new { key = s.Key, label = s.Label, statuses = s.Statuses.Select(x => x.ToString()) }));

    private static string StageLabelOf(LeadStatus status) =>
        Stages.FirstOrDefault(s => s.Statuses.Contains(status))?.Label ?? status.ToString();
}
