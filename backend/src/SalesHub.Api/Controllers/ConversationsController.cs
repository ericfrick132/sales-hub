using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SalesHub.Core.Domain.Entities;
using SalesHub.Core.Domain.Enums;
using SalesHub.Infrastructure.Persistence;
using SalesHub.Infrastructure.Services;

namespace SalesHub.Api.Controllers;

[ApiController]
[Route("api/conversations")]
[Authorize]
public class ConversationsController : ControllerBase
{
    private readonly ApplicationDbContext _db;
    private readonly ConversationService _conv;
    private readonly ConversationAgentService _agent;

    public ConversationsController(ApplicationDbContext db, ConversationService conv, ConversationAgentService agent)
    {
        _db = db; _conv = conv; _agent = agent;
    }

    public record ReclassifyRequest(int Max = 50);

    /// <summary>
    /// Backfill: la IA analiza los chats anteriores de los leads en Sent/Replied y
    /// les actualiza el estado (interesado / no interesado / agendó / compró). Procesa
    /// hasta Max por llamada; devuelve cuántos quedan para llamarlo de nuevo. Solo admin.
    /// </summary>
    [HttpPost("reclassify")]
    public async Task<IActionResult> Reclassify([FromBody] ReclassifyRequest? req, CancellationToken ct)
    {
        if (!CurrentUser.IsAdmin(User)) return Forbid();
        var (processed, remaining) = await _agent.ReclassifyExistingAsync(req?.Max ?? 50, ct);
        return Ok(new { processed, remaining });
    }

    public record ConversationListItem(
        Guid LeadId, string LeadName, string? City, string ProductKey, string? ProductName,
        Guid? SellerId, string? SellerName, string Status,
        string? LastMessageText, MessageDirection? LastDirection,
        DateTimeOffset? LastTimestamp, int UnreadCount,
        DateTimeOffset? FirstReplyAt, DateTimeOffset? SentAt,
        string Source, List<string> Tags, string? AdTitle,
        DateTimeOffset? LastInboundAt, DateTimeOffset? ClosedAt, DateTimeOffset? BotMutedAt,
        string? PitchName, int? PitchStep, int? PitchSteps, bool PitchActive)
    {
        /// <summary>Cuándo vence la ventana de 24 h para responder (desde el último inbound).</summary>
        public DateTimeOffset? WindowExpiresAt => LastInboundAt?.AddHours(24);
        /// <summary>
        /// Celular que atiende esta conversación (el device del bridge asignado a la línea).
        /// Se llena después de la query: los devices son pocos y se resuelven en memoria.
        /// </summary>
        public string? DeviceName { get; set; }
        public Guid? DeviceId { get; set; }
    }

    public record ConversationMessageDto(
        Guid Id, MessageDirection Direction, string Text, DateTimeOffset Timestamp,
        MessageDeliveryStatus Status, bool IsRead);

    public record ConversationThreadDto(
        Guid LeadId, string LeadName, string? WhatsappPhone, string? RenderedInitialMessage,
        string ProductKey, string Status,
        Guid? SellerId, string? SellerName,
        string? AiSuggestedReply,
        DateTimeOffset? BotMutedAt,
        IReadOnlyList<ConversationMessageDto> Messages,
        // Panel "Info del lead"
        string Source, List<string> Tags, string? AdId, string? AdTitle, string? AdSourceUrl,
        DateTimeOffset? LastInboundAt, DateTimeOffset? WindowExpiresAt, DateTimeOffset? ClosedAt,
        DateTimeOffset CreatedAt, DateTimeOffset? FirstMessageAt, int MessagesCount, DateTimeOffset? LastActiveAt,
        PitchInfoDto? Pitch, IReadOnlyList<FeedbackDto> Feedback, string? City, int Score);
    public record PitchInfoDto(Guid PitchId, string Name, int StepIndex, int StepsTotal, int FollowupsSent, bool Completed, bool GaveUp, DateTimeOffset? NextStepDueAt);
    public record FeedbackDto(Guid Id, int Rating, string? Note, string? RatedMessage, string? SellerName, DateTimeOffset CreatedAt);
    public record SetTagsRequest(List<string> Tags);
    public record FeedbackRequest(int Rating, string? Note);

    public record SendReplyRequest(string Text);

    [HttpGet]
    public async Task<ActionResult<IEnumerable<ConversationListItem>>> List(
        [FromQuery] string? productKey,
        [FromQuery] Guid? sellerId,
        [FromQuery] Guid? deviceId,
        [FromQuery] string? status,
        [FromQuery(Name = "from")] DateTimeOffset? fromTs,
        [FromQuery(Name = "to")] DateTimeOffset? toTs,
        // bucket: "unread" (con entrantes sin leer), "replied" (lead respondió alguna vez),
        //         "waiting" (mandamos último, esperando),
        //         "cold" (sin respuesta + > coldDays sin actividad), "all" (default).
        [FromQuery] string? bucket,
        // window: "12h+" | "6-12h" | "<6h" | "expired" | "new" (ventana de 24 h desde el último inbound)
        [FromQuery] string? window,
        [FromQuery] string? tag,
        [FromQuery] string? source,
        [FromQuery] bool includeClosed = false,
        [FromQuery] int coldDays = 3,
        [FromQuery] int limit = 200,
        CancellationToken ct = default)
    {
        // TODOS ven TODOS los chats. La atención dejó de ser "cada vendedor con sus leads":
        // hay una línea compartida y quien atiende necesita la bandeja completa.
        var leadQ = _db.Leads.AsNoTracking().Include(l => l.Product).Include(l => l.Seller).AsQueryable();
        if (sellerId is not null) leadQ = leadQ.Where(l => l.SellerId == sellerId);

        // Filtrar por celular = filtrar por la línea a la que ese celular está asignado.
        var devices = await _db.Devices.AsNoTracking()
            .Select(d => new { d.Id, d.Name, d.SellerId })
            .ToListAsync(ct);
        if (deviceId is not null)
        {
            var dev = devices.FirstOrDefault(d => d.Id == deviceId);
            if (dev?.SellerId is null) return new List<ConversationListItem>();
            leadQ = leadQ.Where(l => l.SellerId == dev.SellerId);
        }
        if (!string.IsNullOrWhiteSpace(productKey)) leadQ = leadQ.Where(l => l.ProductKey == productKey);
        if (!string.IsNullOrWhiteSpace(status) && Enum.TryParse<LeadStatus>(status, ignoreCase: true, out var st))
            leadQ = leadQ.Where(l => l.Status == st);
        if (!includeClosed) leadQ = leadQ.Where(l => l.ConversationClosedAt == null);
        if (!string.IsNullOrWhiteSpace(tag)) leadQ = leadQ.Where(l => l.Tags.Contains(tag));
        if (!string.IsNullOrWhiteSpace(source) && Enum.TryParse<LeadSource>(source, ignoreCase: true, out var src))
            leadQ = leadQ.Where(l => l.Source == src);
        // Ventana de respuesta (estilo Meta: 24 h desde el último mensaje del lead).
        var nowW = DateTimeOffset.UtcNow;
        switch ((window ?? string.Empty).Trim().ToLowerInvariant())
        {
            case "12h+": leadQ = leadQ.Where(l => l.LastInboundAt != null && l.LastInboundAt > nowW.AddHours(-12)); break;
            case "6-12h": leadQ = leadQ.Where(l => l.LastInboundAt != null && l.LastInboundAt <= nowW.AddHours(-12) && l.LastInboundAt > nowW.AddHours(-18)); break;
            case "<6h": leadQ = leadQ.Where(l => l.LastInboundAt != null && l.LastInboundAt <= nowW.AddHours(-18) && l.LastInboundAt > nowW.AddHours(-24)); break;
            case "expired": leadQ = leadQ.Where(l => l.LastInboundAt == null || l.LastInboundAt <= nowW.AddHours(-24)); break;
            case "new": leadQ = leadQ.Where(l => l.CreatedAt > nowW.AddHours(-24)); break;
        }

        // Los buckets filtran EN SQL, antes del Take: si fueran post-Take, "sin leer"
        // solo vería los N hilos más recientes y se perdería el backlog viejo.
        bucket = string.IsNullOrWhiteSpace(bucket) ? "all" : bucket;
        var coldCutoff = DateTimeOffset.UtcNow.AddDays(-coldDays);

        var items = await (from l in leadQ
                           let latest = _db.ConversationMessages.Where(m => m.LeadId == l.Id)
                                          .OrderByDescending(m => m.Timestamp).FirstOrDefault()
                           let unread = _db.ConversationMessages.Count(m => m.LeadId == l.Id
                                          && m.Direction == MessageDirection.Inbound && !m.IsRead)
                           where latest != null
                           where fromTs == null || latest.Timestamp >= fromTs
                           where toTs == null || latest.Timestamp <= toTs
                           // Con mensajes entrantes sin leer.
                           where bucket != "unread" || unread > 0
                           // El lead nos contestó al menos una vez.
                           where bucket != "replied" || l.FirstReplyAt != null
                           // Nosotros mandamos último → estamos esperando respuesta.
                           where bucket != "waiting" || (l.FirstReplyAt == null
                                    && latest.Direction == MessageDirection.Outbound)
                           // Sin respuesta y sin actividad reciente → follow-up.
                           where bucket != "cold" || (l.FirstReplyAt == null
                                    && latest.Timestamp <= coldCutoff)
                           // Orden por RECENCIA (estilo WhatsApp). Antes era unread-first y una
                           // conversación de HOY quedaba enterrada bajo cientos de hilos viejos
                           // con no-leídos acumulados (el caso "escribió y no lo veo en la lista").
                           orderby latest.Timestamp descending
                           let ps = _db.LeadPitchStates.Where(x => x.LeadId == l.Id).Select(x => new { x.StepIndex, x.CompletedAt, x.GaveUpAt, Name = x.Pitch!.Name, Total = x.Pitch.Steps.Count }).FirstOrDefault()
                           select new ConversationListItem(
                               l.Id, l.Name, l.City, l.ProductKey,
                               l.Product != null ? l.Product.DisplayName : null,
                               l.SellerId,
                               l.Seller != null ? l.Seller.DisplayName : null,
                               l.Status.ToString(),
                               latest.Text, latest.Direction, latest.Timestamp, unread,
                               l.FirstReplyAt, l.SentAt,
                               l.Source.ToString(), l.Tags, l.AdTitle,
                               l.LastInboundAt, l.ConversationClosedAt, l.BotMutedAt,
                               ps != null ? ps.Name : null,
                               ps != null ? ps.StepIndex + 1 : (int?)null,
                               ps != null ? ps.Total : (int?)null,
                               ps != null && ps.CompletedAt == null && ps.GaveUpAt == null))
                       .Take(Math.Min(limit, 500)).ToListAsync(ct);

        // Un seller puede tener más de un celu; se muestra el primero por nombre.
        var deviceBySeller = devices
            .Where(d => d.SellerId != null)
            .GroupBy(d => d.SellerId!.Value)
            .ToDictionary(g => g.Key, g => g.OrderBy(x => x.Name).First());
        foreach (var it in items)
        {
            if (it.SellerId is { } sid && deviceBySeller.TryGetValue(sid, out var dev))
            {
                it.DeviceName = dev.Name;
                it.DeviceId = dev.Id;
            }
        }

        return items;
    }

    [HttpGet("{leadId:guid}")]
    public async Task<ActionResult<ConversationThreadDto>> Thread(Guid leadId, CancellationToken ct)
    {
        var sellerId = CurrentUser.Id(User);
        var isAdmin = CurrentUser.IsAdmin(User);
        var lead = await _db.Leads.AsNoTracking()
            .Include(l => l.Seller)
            .FirstOrDefaultAsync(l => l.Id == leadId, ct);
        if (lead is null) return NotFound();

        var messages = await _db.ConversationMessages.AsNoTracking()
            .Where(m => m.LeadId == leadId)
            .OrderBy(m => m.Timestamp)
            .Select(m => new ConversationMessageDto(m.Id, m.Direction, m.Text, m.Timestamp, m.Status, m.IsRead))
            .ToListAsync(ct);

        // Bandeja compartida: abrir el chat lo marca leído para todos, mire quien mire.
        await _conv.MarkReadAsync(sellerId, leadId, ct);

        var ps = await _db.LeadPitchStates.AsNoTracking().Include(x => x.Pitch)
            .FirstOrDefaultAsync(x => x.LeadId == leadId, ct);
        var feedback = await _db.ConversationFeedbacks.AsNoTracking()
            .Where(f => f.LeadId == leadId)
            .OrderByDescending(f => f.CreatedAt)
            .Take(5)
            .Select(f => new FeedbackDto(f.Id, f.Rating, f.Note, f.RatedMessage, f.Seller != null ? f.Seller.DisplayName : null, f.CreatedAt))
            .ToListAsync(ct);
        var pitchInfo = ps is null ? null : new PitchInfoDto(ps.PitchId, ps.Pitch!.Name, ps.StepIndex + 1, ps.Pitch.Steps.Count,
            ps.FollowupsSent, ps.CompletedAt != null, ps.GaveUpAt != null, ps.NextStepDueAt);
        return new ConversationThreadDto(lead.Id, lead.Name, lead.WhatsappPhone, lead.RenderedMessage,
            lead.ProductKey, lead.Status.ToString(),
            lead.SellerId, lead.Seller?.DisplayName,
            lead.AiSuggestedReply,
            lead.BotMutedAt,
            messages,
            lead.Source.ToString(), lead.Tags, lead.AdId, lead.AdTitle, lead.AdSourceUrl,
            lead.LastInboundAt, lead.LastInboundAt?.AddHours(24), lead.ConversationClosedAt,
            lead.CreatedAt, messages.Count > 0 ? messages[0].Timestamp : null, messages.Count,
            messages.Count > 0 ? messages[^1].Timestamp : null,
            pitchInfo, feedback, lead.City, lead.Score);
    }

    /// <summary>Takeover humano desde la UI: bot ON/OFF para esta conversación
    /// (equivale a mandar "-"/"+" desde el celu).</summary>
    [HttpPost("{leadId:guid}/bot")]
    public async Task<IActionResult> ToggleBot(Guid leadId, [FromBody] ToggleBotRequest req, CancellationToken ct)
    {
        var sellerId = CurrentUser.Id(User);
        var isAdmin = CurrentUser.IsAdmin(User);
        var lead = await _db.Leads.FirstOrDefaultAsync(l => l.Id == leadId, ct);
        if (lead is null) return NotFound();

        lead.BotMutedAt = req.Enabled ? null : DateTimeOffset.UtcNow;
        if (!req.Enabled) { lead.AiSuggestedReply = null; lead.AiSuggestedReplyAt = null; }
        lead.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);
        return Ok(new { leadId, botEnabled = req.Enabled });
    }

    public record ToggleBotRequest(bool Enabled);

    [HttpPost("{leadId:guid}/reply")]
    public async Task<IActionResult> Reply(Guid leadId, [FromBody] SendReplyRequest req, CancellationToken ct)
    {
        var sellerId = CurrentUser.Id(User);
        if (string.IsNullOrWhiteSpace(req.Text)) return BadRequest(new { error = "Texto vacío" });
        try
        {
            var msg = await _conv.SendReplyAsync(sellerId, leadId, req.Text, ct);
            if (msg is null) return NotFound();
            return Ok(new ConversationMessageDto(msg.Id, msg.Direction, msg.Text, msg.Timestamp, msg.Status, msg.IsRead));
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>Tags del inbox (reemplaza la lista completa).</summary>
    [HttpPost("{leadId:guid}/tags")]
    public async Task<IActionResult> SetTags(Guid leadId, [FromBody] SetTagsRequest req, CancellationToken ct)
    {
        var lead = await _db.Leads.FirstOrDefaultAsync(l => l.Id == leadId, ct);
        if (lead is null) return NotFound();
        lead.Tags = (req.Tags ?? new()).Select(t => t.Trim().ToLowerInvariant()).Where(t => t.Length > 0 && t.Length <= 40).Distinct().ToList();
        lead.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);
        return Ok(new { leadId, tags = lead.Tags });
    }

    /// <summary>Cierra la conversación (se oculta del inbox hasta que el lead vuelva a escribir).</summary>
    [HttpPost("{leadId:guid}/close")]
    public async Task<IActionResult> Close(Guid leadId, CancellationToken ct)
    {
        var lead = await _db.Leads.FirstOrDefaultAsync(l => l.Id == leadId, ct);
        if (lead is null) return NotFound();
        lead.ConversationClosedAt = DateTimeOffset.UtcNow;
        lead.AiSuggestedReply = null; lead.AiSuggestedReplyAt = null;
        lead.UpdatedAt = DateTimeOffset.UtcNow;
        // Cortar el pitch si estaba en curso.
        var ps = await _db.LeadPitchStates.FirstOrDefaultAsync(x => x.LeadId == leadId, ct);
        if (ps is not null && ps.CompletedAt is null && ps.GaveUpAt is null) { ps.CompletedAt = DateTimeOffset.UtcNow; ps.NextStepDueAt = null; }
        await _db.SaveChangesAsync(ct);
        return Ok(new { leadId, closedAt = lead.ConversationClosedAt });
    }

    [HttpPost("{leadId:guid}/reopen")]
    public async Task<IActionResult> Reopen(Guid leadId, CancellationToken ct)
    {
        var lead = await _db.Leads.FirstOrDefaultAsync(l => l.Id == leadId, ct);
        if (lead is null) return NotFound();
        lead.ConversationClosedAt = null;
        lead.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);
        return Ok(new { leadId, closedAt = (DateTimeOffset?)null });
    }

    /// <summary>
    /// Califica la conversación (👍 = 1, 👎 = -1, 0 = solo nota). Guarda como contexto el último
    /// mensaje nuestro; las notas alimentan el prompt del agente para ese producto.
    /// </summary>
    [HttpPost("{leadId:guid}/feedback")]
    public async Task<IActionResult> Feedback(Guid leadId, [FromBody] FeedbackRequest req, CancellationToken ct)
    {
        var lead = await _db.Leads.AsNoTracking().FirstOrDefaultAsync(l => l.Id == leadId, ct);
        if (lead is null) return NotFound();
        if (req.Rating is < -1 or > 1) return BadRequest(new { error = "Rating debe ser -1, 0 o 1" });
        if (req.Rating == 0 && string.IsNullOrWhiteSpace(req.Note)) return BadRequest(new { error = "Poné una nota o un pulgar" });
        var lastOurs = await _db.ConversationMessages.AsNoTracking()
            .Where(m => m.LeadId == leadId && m.Direction == MessageDirection.Outbound)
            .OrderByDescending(m => m.Timestamp).Select(m => m.Text).FirstOrDefaultAsync(ct);
        var fb = new ConversationFeedback
        {
            LeadId = leadId, ProductKey = lead.ProductKey, SellerId = CurrentUser.Id(User),
            Rating = req.Rating, Note = string.IsNullOrWhiteSpace(req.Note) ? null : req.Note.Trim(),
            RatedMessage = lastOurs,
        };
        _db.ConversationFeedbacks.Add(fb);
        await _db.SaveChangesAsync(ct);
        ConversationFeedbackProvider.Invalidate(lead.ProductKey);
        return Ok(new FeedbackDto(fb.Id, fb.Rating, fb.Note, fb.RatedMessage, null, fb.CreatedAt));
    }

    /// <summary>Tags en uso (para autocompletar en el inbox).</summary>
    [HttpGet("tags")]
    public async Task<IActionResult> Tags(CancellationToken ct)
    {
        var tags = await _db.Leads.AsNoTracking()
            .Where(l => l.Tags.Count > 0)
            .Select(l => l.Tags)
            .Take(2000)
            .ToListAsync(ct);
        return Ok(tags.SelectMany(t => t).GroupBy(t => t).OrderByDescending(g => g.Count()).Select(g => new { tag = g.Key, count = g.Count() }).Take(60));
    }

    [HttpGet("unread-count")]
    public async Task<IActionResult> UnreadCount(CancellationToken ct)
    {
        // Sin filtrar por vendedor: el badge cuenta la bandeja completa, igual que la lista.
        var count = await _db.ConversationMessages
            .CountAsync(m => m.Direction == MessageDirection.Inbound && !m.IsRead, ct);
        return Ok(new { count });
    }
}
