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
    private readonly IMessageRenderer _renderer;
    private readonly OnboardingService _onboarding;

    public CrmController(ApplicationDbContext db, IProductStatusNotifier statusNotifier,
        IMessageRenderer renderer, OnboardingService onboarding)
    {
        _db = db; _statusNotifier = statusNotifier; _renderer = renderer; _onboarding = onboarding;
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
    /// Filtros del tablero, compartidos con la cola del modo llamadas (que llama a los mismos
    /// leads que el usuario está mirando). Todos opcionales y combinables.
    /// <c>Q</c> busca por nombre, teléfono o ciudad; <c>StalledDays</c> deja sólo los que no
    /// tienen movimiento hace N días; <c>Due</c> = "today" | "overdue" filtra por la próxima acción.
    /// </summary>
    public class LeadFilter
    {
        public string? Q { get; set; }
        public string? ProductKey { get; set; }
        public Guid? SellerId { get; set; }
        public Guid? DeviceId { get; set; }
        public LeadSource[]? Source { get; set; }
        public bool OnlyMine { get; set; }
        public int? StalledDays { get; set; }
        public string? Due { get; set; }
    }

    /// <summary>La query de leads con los filtros aplicados; null si el filtro no puede matchear nada.</summary>
    private async Task<IQueryable<Lead>?> FilteredLeadsAsync(LeadFilter f, CancellationToken ct)
    {
        var leadQ = _db.Leads.AsNoTracking().Include(l => l.Product).Include(l => l.Seller).AsQueryable();

        if (f.OnlyMine) { var callerId = CurrentUser.Id(User); leadQ = leadQ.Where(l => l.SellerId == callerId); }
        else if (f.SellerId is not null) leadQ = leadQ.Where(l => l.SellerId == f.SellerId);

        if (f.DeviceId is not null)
        {
            // Un celular sin vendedor no tiene leads.
            var devSellerId = await _db.Devices.AsNoTracking().Where(d => d.Id == f.DeviceId)
                .Select(d => d.SellerId).FirstOrDefaultAsync(ct);
            if (devSellerId is null) return null;
            leadQ = leadQ.Where(l => l.SellerId == devSellerId);
        }

        if (!string.IsNullOrWhiteSpace(f.ProductKey)) leadQ = leadQ.Where(l => l.ProductKey == f.ProductKey);
        if (f.Source is { Length: > 0 }) leadQ = leadQ.Where(l => f.Source.Contains(l.Source));

        if (!string.IsNullOrWhiteSpace(f.Q))
        {
            var term = f.Q.Trim().ToLower();
            var digits = new string(term.Where(char.IsDigit).ToArray());
            leadQ = leadQ.Where(l =>
                l.Name.ToLower().Contains(term)
                || (l.City != null && l.City.ToLower().Contains(term))
                || (digits.Length >= 4 && l.WhatsappPhone != null && l.WhatsappPhone.Contains(digits)));
        }

        var now = DateTimeOffset.UtcNow;
        if (f.StalledDays is > 0)
        {
            var cutoff = now.AddDays(-f.StalledDays.Value);
            leadQ = leadQ.Where(l => l.UpdatedAt <= cutoff);
        }
        switch ((f.Due ?? "").ToLowerInvariant())
        {
            case "today":
                leadQ = leadQ.Where(l => l.NextActionAt != null && l.NextActionAt <= now.AddDays(1).Date);
                break;
            case "overdue":
                leadQ = leadQ.Where(l => l.NextActionAt != null && l.NextActionAt < now);
                break;
        }
        return leadQ;
    }

    /// <summary>Tablero completo, con los filtros de <see cref="LeadFilter"/>.</summary>
    [HttpGet("board")]
    public async Task<IActionResult> Board(
        [FromQuery] LeadFilter filter,
        [FromQuery] int perStage = 50,
        CancellationToken ct = default)
    {
        perStage = Math.Clamp(perStage, 5, 200);

        var leadQ = await FilteredLeadsAsync(filter, ct);
        if (leadQ is null)
            return Ok(new { stages = Stages.Select(s => new StageColumn(s.Key, s.Label, 0, Array.Empty<CrmCard>())), total = 0, overdue = 0, perStage });

        var deviceBySeller = await DeviceBySellerAsync(ct);
        var now = DateTimeOffset.UtcNow;

        // Los totales salen de un GROUP BY sobre columnas reales — nada de traer los 12k
        // leads a memoria para después quedarse con 50 por columna.
        var counts = await leadQ
            .GroupBy(l => l.Status)
            .Select(g => new { Status = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Status, x => x.Count, ct);

        var overdue = await leadQ.CountAsync(l => l.NextActionAt != null && l.NextActionAt < now, ct);

        // Una query por etapa, ya ordenada y cortada en SQL: sólo bajan las tarjetas visibles.
        var columns = new List<StageColumn>();
        foreach (var s in Stages)
        {
            var (cards, _) = await StageCardsAsync(leadQ, s, 0, perStage, deviceBySeller, ct);
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

    /// <summary>
    /// Las tarjetas que siguen en una columna (scroll infinito del tablero): mismos filtros y
    /// mismo orden que <see cref="Board"/>, desde <paramref name="skip"/>.
    /// </summary>
    [HttpGet("board/{stageKey}")]
    public async Task<IActionResult> StageCards(
        string stageKey,
        [FromQuery] LeadFilter filter,
        [FromQuery] int skip = 0,
        [FromQuery] int take = 50,
        CancellationToken ct = default)
    {
        var stage = Stages.FirstOrDefault(s => s.Key == stageKey.ToLowerInvariant());
        if (stage is null) return BadRequest(new { error = $"Etapa desconocida: {stageKey}" });
        skip = Math.Max(0, skip);
        take = Math.Clamp(take, 5, 200);

        var leadQ = await FilteredLeadsAsync(filter, ct);
        if (leadQ is null) return Ok(new { key = stage.Key, cards = Array.Empty<CrmCard>(), nextSkip = (int?)null });

        var (cards, hasMore) = await StageCardsAsync(leadQ, stage, skip, take, await DeviceBySellerAsync(ct), ct);
        return Ok(new { key = stage.Key, cards, nextSkip = hasMore ? skip + cards.Count : (int?)null });
    }

    private record DeviceRef(Guid Id, string Name);

    /// <summary>El celular de cada vendedor (el primero por nombre si tiene varios).</summary>
    private async Task<Dictionary<Guid, DeviceRef>> DeviceBySellerAsync(CancellationToken ct) =>
        (await _db.Devices.AsNoTracking().Where(d => d.SellerId != null)
            .Select(d => new { d.Id, d.Name, d.SellerId }).ToListAsync(ct))
        .GroupBy(d => d.SellerId!.Value)
        .ToDictionary(g => g.Key, g => g.OrderBy(x => x.Name).Select(x => new DeviceRef(x.Id, x.Name)).First());

    /// <summary>
    /// Una página de tarjetas de una etapa, ordenada y cortada en SQL. El orden va por columnas
    /// indexables (vencimiento y última actualización) y desempata por Id para que las páginas
    /// no se pisen; el dato del chat se resuelve sólo para las filas de la página.
    /// </summary>
    private async Task<(List<CrmCard> Cards, bool HasMore)> StageCardsAsync(
        IQueryable<Lead> leadQ, Stage s, int skip, int take,
        Dictionary<Guid, DeviceRef> deviceBySeller, CancellationToken ct)
    {
        var rows = await leadQ
            .Where(l => s.Statuses.Contains(l.Status))
            // OrderBy sobre la columna cruda, sin COALESCE: en Postgres un ASC ya manda
            // los NULL al final (los que no tienen recordatorio van después), y así el
            // sort puede apoyarse en el índice (status, next_action_at, updated_at).
            .OrderBy(l => l.NextActionAt)
            .ThenByDescending(l => l.UpdatedAt)
            .ThenBy(l => l.Id)
            .Skip(skip)
            .Take(take + 1)
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

        // Se pide una fila de más sólo para saber si hay otra página.
        var hasMore = rows.Count > take;
        var cards = rows.Take(take).Select(r =>
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
        return (cards, hasMore);
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

        var raw = await _db.ConversationMessages.AsNoTracking()
            .Where(m => m.LeadId == id)
            .OrderByDescending(m => m.Timestamp)
            .Take(10)
            .Select(m => new { m.Direction, m.Text, m.Timestamp, m.EvolutionInstance })
            .ToListAsync(ct);

        // Por qué línea viajó cada mensaje. El nombre de la instancia (seller_ventas,
        // moto-e14) no le dice nada a nadie: lo que importa es el número.
        var lineNames = raw.Where(m => !string.IsNullOrWhiteSpace(m.EvolutionInstance))
            .Select(m => m.EvolutionInstance!).Distinct().ToList();
        var linePhones = await _db.EvolutionInstances.AsNoTracking()
            .Where(i => lineNames.Contains(i.InstanceName) && i.ConnectedPhoneNumber != null)
            .ToDictionaryAsync(i => i.InstanceName, i => i.ConnectedPhoneNumber!, ct);
        // Las que no son instancias de Evolution son celulares del bridge (van por nombre).
        var deviceNames = await _db.Devices.AsNoTracking()
            .Where(d => lineNames.Contains(d.Name))
            .Select(d => d.Name).ToListAsync(ct);

        var messages = raw.Select(m => new
        {
            direction = m.Direction,
            text = m.Text,
            timestamp = m.Timestamp,
            line = m.EvolutionInstance,
            linePhone = m.EvolutionInstance != null ? linePhones.GetValueOrDefault(m.EvolutionInstance) : null,
            isDevice = m.EvolutionInstance != null && deviceNames.Contains(m.EvolutionInstance),
        }).ToList();

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
            manualAssignedAt = lead.ManualAssignedAt,
            createdAt = lead.CreatedAt,
            sentAt = lead.SentAt,
            firstReplyAt = lead.FirstReplyAt,
            demoScheduledAt = lead.DemoScheduledAt,
            closedAt = lead.ClosedAt,
            nextActionAt = lead.NextActionAt,
            nextActionNote = lead.NextActionNote,
            legacyNotes = lead.Notes,
            notes,
            messages = messages.OrderBy(m => m.timestamp).ToList(),
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

        await ApplyStageAsync(lead, stage, ct);
        return Ok(new { id = lead.Id, status = lead.Status.ToString(), stage = stage.Key });
    }

    /// <summary>
    /// Escribe el LeadStatus de la etapa, sella las fechas del embudo, deja el rastro en la
    /// bitácora, guarda y avisa al producto de origen. Lo usan el arrastre y el modo llamadas.
    /// </summary>
    private async Task ApplyStageAsync(Lead lead, Stage stage, CancellationToken ct)
    {
        var id = lead.Id;
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

        await _db.SaveChangesAsync(ct);

        // Mismo status-back que el PATCH clásico: el producto de origen se entera del cierre.
        await _statusNotifier.NotifyAsync(lead.ProductKey, lead.ExternalId, target.ToString(),
            target == LeadStatus.Closed, ct);
    }

    public record CallQueueItem(
        Guid Id, string Name, string Phone, string ProductKey, string? ProductName, string? City,
        string Status, string StageKey, string? SellerName, DateTimeOffset? NextActionAt,
        string? NextActionNote, string? LastNote, int CallCount, DateTimeOffset? LastCallAt,
        DateTimeOffset CreatedAt);

    /// <summary>
    /// Cola del modo llamadas: los leads con teléfono que matchean los filtros del tablero,
    /// en el orden en que conviene llamarlos (recordatorios vencidos primero, después los
    /// más nuevos: un lead recién entrado es el que más chance tiene de atender).
    /// <paramref name="stages"/> limita las etapas, separadas por coma (por defecto todas menos
    /// ganados y perdidos).
    /// <paramref name="skipCalled"/> = "ever" (nunca llamados, por defecto) | "today" | "week" | "no".
    /// </summary>
    [HttpGet("call-queue")]
    public async Task<IActionResult> CallQueue(
        [FromQuery] LeadFilter filter,
        [FromQuery] string? stages,
        [FromQuery] string? skipCalled = "ever",
        [FromQuery] int limit = 300,
        CancellationToken ct = default)
    {
        limit = Math.Clamp(limit, 1, 500);
        var leadQ = await FilteredLeadsAsync(filter, ct);
        if (leadQ is null) return Ok(new { total = 0, items = Array.Empty<CallQueueItem>() });

        var stageKeys = (stages ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var chosen = stageKeys.Length > 0
            ? Stages.Where(s => stageKeys.Contains(s.Key)).ToArray()
            : Stages.Where(s => s.Key is not ("ganado" or "perdido")).ToArray();
        var statuses = chosen.SelectMany(s => s.Statuses).ToArray();

        leadQ = leadQ.Where(l => statuses.Contains(l.Status) && l.WhatsappPhone != null && l.WhatsappPhone != "");

        var now = DateTimeOffset.UtcNow;
        var calls = _db.LeadNotes.Where(n => n.Kind == LeadNoteKind.Call);
        switch ((skipCalled ?? "ever").ToLowerInvariant())
        {
            case "no":
                break;
            case "today":
                var halfDay = now.AddHours(-12);
                leadQ = leadQ.Where(l => !calls.Any(n => n.LeadId == l.Id && n.CreatedAt >= halfDay));
                break;
            case "week":
                var week = now.AddDays(-7);
                leadQ = leadQ.Where(l => !calls.Any(n => n.LeadId == l.Id && n.CreatedAt >= week));
                break;
            default:
                leadQ = leadQ.Where(l => !calls.Any(n => n.LeadId == l.Id));
                break;
        }

        var total = await leadQ.CountAsync(ct);
        var rows = await leadQ
            .OrderBy(l => l.NextActionAt)
            .ThenByDescending(l => l.CreatedAt)
            .Take(limit)
            .Select(l => new
            {
                l.Id, l.Name, l.WhatsappPhone, l.ProductKey,
                ProductName = l.Product != null ? l.Product.DisplayName : null,
                l.City, l.Status,
                SellerName = l.Seller != null ? l.Seller.DisplayName : null,
                l.NextActionAt, l.NextActionNote, l.CreatedAt,
                LastNote = _db.LeadNotes.Where(n => n.LeadId == l.Id && (n.Kind == LeadNoteKind.Note || n.Kind == LeadNoteKind.Call))
                    .OrderByDescending(n => n.CreatedAt).Select(n => n.Text).FirstOrDefault(),
                CallCount = _db.LeadNotes.Count(n => n.LeadId == l.Id && n.Kind == LeadNoteKind.Call),
                LastCallAt = _db.LeadNotes.Where(n => n.LeadId == l.Id && n.Kind == LeadNoteKind.Call)
                    .OrderByDescending(n => n.CreatedAt).Select(n => (DateTimeOffset?)n.CreatedAt).FirstOrDefault(),
            })
            .ToListAsync(ct);

        var items = rows.Select(r => new CallQueueItem(
            r.Id, r.Name, r.WhatsappPhone!, r.ProductKey, r.ProductName, r.City, r.Status.ToString(),
            StageKeyOf(r.Status), r.SellerName, r.NextActionAt, r.NextActionNote, r.LastNote,
            r.CallCount, r.LastCallAt, r.CreatedAt)).ToList();

        return Ok(new { total, items });
    }

    public enum CallOutcome { NoAnswer, Answered, Interested, NotInterested, Callback, WrongNumber }

    public record LogCallRequest(CallOutcome Outcome, string? Note, DateTimeOffset? CallbackAt);

    private static readonly Dictionary<CallOutcome, string> CallOutcomeLabel = new()
    {
        [CallOutcome.NoAnswer] = "No atendió",
        [CallOutcome.Answered] = "Atendió",
        [CallOutcome.Interested] = "Interesado",
        [CallOutcome.NotInterested] = "No le interesa",
        [CallOutcome.Callback] = "Volver a llamar",
        [CallOutcome.WrongNumber] = "Número equivocado",
    };

    /// <summary>
    /// Registra el resultado de una llamada del modo llamadas y aplica lo que implica:
    /// interesado → etapa Interesados; no le interesa / número equivocado → Perdidos;
    /// volver a llamar → recordatorio. "Atendió" y "No atendió" sólo dejan la nota: cambiar
    /// la etapa ahí movería el follow-up automático sin que nadie lo haya decidido.
    /// </summary>
    [HttpPost("leads/{id:guid}/calls")]
    public async Task<IActionResult> LogCall(Guid id, [FromBody] LogCallRequest req, CancellationToken ct)
    {
        if (req.Outcome == CallOutcome.Callback && req.CallbackAt is null)
            return BadRequest(new { error = "Falta cuándo volver a llamar" });

        var lead = await _db.Leads.FirstOrDefaultAsync(l => l.Id == id, ct);
        if (lead is null) return NotFound();

        var now = DateTimeOffset.UtcNow;
        var note = string.IsNullOrWhiteSpace(req.Note) ? null : req.Note!.Trim();
        var text = $"Llamada: {CallOutcomeLabel[req.Outcome]}"
            + (req.Outcome == CallOutcome.Callback ? $" ({req.CallbackAt!.Value.ToOffset(TimeSpan.FromHours(-3)):dd/MM HH:mm})" : "")
            + (note is null ? "" : $" — {note}");
        _db.LeadNotes.Add(new LeadNote
        {
            Id = Guid.NewGuid(),
            LeadId = id,
            SellerId = CurrentUser.Id(User),
            Kind = LeadNoteKind.Call,
            Text = text,
        });

        if (req.Outcome == CallOutcome.Callback)
        {
            lead.NextActionAt = req.CallbackAt;
            lead.NextActionNote = "Volver a llamar" + (note is null ? "" : $": {note}");
        }
        else if (lead.NextActionNote?.StartsWith("Volver a llamar") == true)
        {
            // El recordatorio de llamarlo ya se cumplió.
            lead.NextActionAt = null;
            lead.NextActionNote = null;
        }
        lead.UpdatedAt = now;

        // Sólo avanza: un "interesado" que ya tiene demo o está ganado no retrocede.
        var stageKey = req.Outcome switch
        {
            CallOutcome.Interested when lead.Status is LeadStatus.New or LeadStatus.Assigned or LeadStatus.Queued
                or LeadStatus.Sent or LeadStatus.Replied => "interesado",
            CallOutcome.NotInterested or CallOutcome.WrongNumber when lead.Status != LeadStatus.Closed => "perdido",
            _ => null,
        };

        var stage = stageKey is null ? null : Stages.First(s => s.Key == stageKey);
        if (stage is not null) await ApplyStageAsync(lead, stage, ct);
        else await _db.SaveChangesAsync(ct);

        return Ok(new { id = lead.Id, status = lead.Status.ToString(), stage = StageKeyOf(lead.Status), text });
    }

    public record AssignSellerRequest(Guid SellerId);

    /// <summary>
    /// Cambia el vendedor del lead desde la ficha (sólo admin). Si todavía no se le escribió,
    /// el mensaje se re-renderiza con el vendedor nuevo y se encola por su línea; si ya hay
    /// charla, sólo cambia el dueño y el estado queda como estaba (no vuelve a "Nuevos").
    /// En los dos casos se cancela lo pendiente de la línea vieja, que si no le seguiría
    /// escribiendo. Queda marcado como manual para que el reparto automático no lo mueva.
    /// </summary>
    [HttpPatch("leads/{id:guid}/seller")]
    public async Task<IActionResult> AssignSeller(Guid id, [FromBody] AssignSellerRequest req, CancellationToken ct)
    {
        if (!CurrentUser.IsAdmin(User)) return Forbid();

        var lead = await _db.Leads.Include(l => l.Product).FirstOrDefaultAsync(l => l.Id == id, ct);
        if (lead is null) return NotFound();

        var seller = await _db.Sellers.Include(s => s.EvolutionInstance)
            .FirstOrDefaultAsync(s => s.Id == req.SellerId, ct);
        if (seller is null) return BadRequest(new { error = "Vendedor no encontrado" });
        if (!seller.IsActive) return BadRequest(new { error = "Vendedor inactivo" });

        var now = DateTimeOffset.UtcNow;
        if (lead.SellerId == seller.Id)
        {
            lead.ManualAssignedAt ??= now;
            await _db.SaveChangesAsync(ct);
            return Ok(new { sellerId = seller.Id, sellerName = seller.DisplayName, status = lead.Status.ToString(), queued = false, contacted = false });
        }

        var previous = lead.SellerId is null
            ? null
            : await _db.Sellers.AsNoTracking().Where(s => s.Id == lead.SellerId)
                .Select(s => s.DisplayName).FirstOrDefaultAsync(ct);

        var pending = await _db.Outbox
            .Where(o => o.LeadId == id && (o.Status == OutboxStatus.Scheduled || o.Status == OutboxStatus.Sending))
            .ToListAsync(ct);
        foreach (var o in pending) o.Status = OutboxStatus.Cancelled;

        // Mismo criterio que bulk-reassign: con algo enviado o respondido, la charla ya arrancó.
        var contacted = lead.SentAt != null || lead.FirstReplyAt != null
            || lead.Status is not (LeadStatus.New or LeadStatus.Assigned or LeadStatus.Queued);

        lead.SellerId = seller.Id;
        lead.ManualAssignedAt = now;
        lead.UpdatedAt = now;

        var queued = false;
        if (!contacted)
        {
            lead.AssignedAt = now;
            lead.QueuedAt = null;
            lead.Status = LeadStatus.Assigned;
            if (lead.Product is not null)
            {
                lead.RenderedMessage = _renderer.Render(lead, lead.Product, seller);
                lead.WhatsappLink = string.IsNullOrWhiteSpace(lead.WhatsappPhone)
                    ? null
                    : $"https://wa.me/{lead.WhatsappPhone}?text={Uri.EscapeDataString(lead.RenderedMessage ?? "")}";

                // Encolar aunque la línea esté caída: el sender chequea al momento de mandar.
                if (!string.IsNullOrWhiteSpace(lead.WhatsappPhone) && seller.EvolutionInstance is not null)
                {
                    // Meta Lead Ads con alta self-serve arrancan por el onboarding, no por la
                    // cadencia (mismo camino que el ingest y el rebalanceo).
                    var startedOnboarding = await _onboarding.TryKickoffAsync(
                        lead, lead.Product, seller, lead.WhatsappPhone,
                        seller.EvolutionInstance.InstanceName, _renderer, ct);
                    if (!startedOnboarding)
                    {
                        OutboxEnqueueHelper.EnqueueLeadMessages(
                            _db, _renderer, lead, lead.Product, seller,
                            lead.WhatsappPhone, seller.EvolutionInstance.InstanceName);
                    }
                    lead.Status = LeadStatus.Queued;
                    lead.QueuedAt = now;
                    queued = true;
                }
            }
        }

        var actor = await _db.Sellers.AsNoTracking().Where(s => s.Id == CurrentUser.Id(User))
            .Select(s => s.DisplayName).FirstOrDefaultAsync(ct);
        _db.LeadNotes.Add(new LeadNote
        {
            Id = Guid.NewGuid(),
            LeadId = id,
            SellerId = CurrentUser.Id(User),
            Kind = LeadNoteKind.System,
            Text = $"Asignado a {seller.DisplayName}"
                + (previous is null ? "" : $" (antes: {previous})")
                + (actor is null ? "" : $" por {actor}"),
        });

        await _db.SaveChangesAsync(ct);
        return Ok(new { sellerId = seller.Id, sellerName = seller.DisplayName, status = lead.Status.ToString(), queued, contacted });
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

    private static string StageKeyOf(LeadStatus status) =>
        Stages.FirstOrDefault(s => s.Statuses.Contains(status))?.Key ?? "";
}
