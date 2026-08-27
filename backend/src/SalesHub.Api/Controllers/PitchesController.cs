using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SalesHub.Core.Domain.Entities;
using SalesHub.Core.Domain.Enums;
using SalesHub.Infrastructure.Persistence;
using SalesHub.Infrastructure.Services;

namespace SalesHub.Api.Controllers;

/// <summary>
/// Pitches por anuncio (guiones de pasos estilo Smart Setter / GHL) + estadísticas de
/// enrolamiento por pitch y por anuncio. Solo admin.
/// </summary>
[ApiController]
[Route("api/pitches")]
[Authorize]
public class PitchesController : ControllerBase
{
    private readonly ApplicationDbContext _db;
    private readonly PitchEngine _engine;
    public PitchesController(ApplicationDbContext db, PitchEngine engine) { _db = db; _engine = engine; }

    public record PitchMessageDto(string Text, Guid? MediaAssetId, string? VoiceText, int DelaySeconds);
    public record PitchFollowUpDto(double AfterHours, string Text, Guid? MediaAssetId);
    public record PitchStepDto(string? Title, List<PitchMessageDto> Messages, List<PitchFollowUpDto> FollowUps);
    public record PitchStats(int Enrolled, int Active, int Replied, int Completed, int GaveUp, int Converted);
    public record PitchDto(
        Guid Id, string ProductKey, string Name, bool Active, int SortOrder,
        List<string> AdIds, string? TriggerText, bool IsDefault,
        List<PitchStepDto> Steps, string? AutoTagOnReply, string? StatusOnReply, bool AiAfterPitch,
        int ReplyDelayMinSec, int ReplyDelayMaxSec, DateTimeOffset UpdatedAt, PitchStats Stats,
        string Channel, bool AutoEnroll, int DailyEnrollCap);
    public record UpsertPitchRequest(
        string ProductKey, string Name, bool Active, int SortOrder,
        List<string>? AdIds, string? TriggerText, bool IsDefault,
        List<PitchStepDto>? Steps, string? AutoTagOnReply, string? StatusOnReply, bool AiAfterPitch,
        int ReplyDelayMinSec = 8, int ReplyDelayMaxSec = 40,
        string? Channel = "WhatsApp", bool AutoEnroll = false, int DailyEnrollCap = 30);
    public record BulkEnrollRequest(int Limit = 50, List<string>? Statuses = null, string? City = null);

    [HttpGet]
    public async Task<IActionResult> List([FromQuery] string? productKey, CancellationToken ct)
    {
        if (!CurrentUser.IsAdmin(User)) return Forbid();
        var q = _db.Pitches.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(productKey)) q = q.Where(p => p.ProductKey == productKey);
        var pitches = await q.OrderBy(p => p.ProductKey).ThenBy(p => p.SortOrder).ThenBy(p => p.CreatedAt).ToListAsync(ct);
        var stats = await StatsByPitchAsync(pitches.Select(p => p.Id).ToList(), ct);
        return Ok(pitches.Select(p => ToDto(p, stats.GetValueOrDefault(p.Id) ?? new PitchStats(0, 0, 0, 0, 0, 0))));
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct)
    {
        if (!CurrentUser.IsAdmin(User)) return Forbid();
        var p = await _db.Pitches.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (p is null) return NotFound();
        var stats = await StatsByPitchAsync(new List<Guid> { id }, ct);
        return Ok(ToDto(p, stats.GetValueOrDefault(id) ?? new PitchStats(0, 0, 0, 0, 0, 0)));
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] UpsertPitchRequest r, CancellationToken ct)
    {
        if (!CurrentUser.IsAdmin(User)) return Forbid();
        var err = Validate(r); if (err is not null) return BadRequest(new { error = err });
        if (!await _db.Products.AnyAsync(x => x.ProductKey == r.ProductKey, ct)) return BadRequest(new { error = "Producto inexistente" });
        var p = new Pitch();
        Apply(p, r);
        if (p.IsDefault) await ClearOtherDefaultsAsync(p, ct);
        _db.Pitches.Add(p);
        await _db.SaveChangesAsync(ct);
        return Ok(ToDto(p, new PitchStats(0, 0, 0, 0, 0, 0)));
    }

    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpsertPitchRequest r, CancellationToken ct)
    {
        if (!CurrentUser.IsAdmin(User)) return Forbid();
        var err = Validate(r); if (err is not null) return BadRequest(new { error = err });
        var p = await _db.Pitches.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (p is null) return NotFound();
        Apply(p, r);
        p.UpdatedAt = DateTimeOffset.UtcNow;
        if (p.IsDefault) await ClearOtherDefaultsAsync(p, ct);
        await _db.SaveChangesAsync(ct);
        var stats = await StatsByPitchAsync(new List<Guid> { id }, ct);
        return Ok(ToDto(p, stats.GetValueOrDefault(id) ?? new PitchStats(0, 0, 0, 0, 0, 0)));
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        if (!CurrentUser.IsAdmin(User)) return Forbid();
        var p = await _db.Pitches.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (p is null) return NotFound();
        _db.Pitches.Remove(p);
        await _db.SaveChangesAsync(ct);
        return Ok(new { deleted = id });
    }

    /// <summary>Copia un pitch (para armar la variante B de un A/B).</summary>
    [HttpPost("{id:guid}/duplicate")]
    public async Task<IActionResult> Duplicate(Guid id, CancellationToken ct)
    {
        if (!CurrentUser.IsAdmin(User)) return Forbid();
        var p = await _db.Pitches.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (p is null) return NotFound();
        var copy = new Pitch
        {
            ProductKey = p.ProductKey, Name = p.Name + " (copia)", Active = false, SortOrder = p.SortOrder + 1,
            AdIds = new(), TriggerText = null, IsDefault = false,
            Steps = System.Text.Json.JsonSerializer.Deserialize<List<PitchStep>>(System.Text.Json.JsonSerializer.Serialize(p.Steps)) ?? new(),
            AutoTagOnReply = p.AutoTagOnReply, StatusOnReply = p.StatusOnReply, AiAfterPitch = p.AiAfterPitch,
            ReplyDelayMinSec = p.ReplyDelayMinSec, ReplyDelayMaxSec = p.ReplyDelayMaxSec,
            Channel = p.Channel, AutoEnroll = false, DailyEnrollCap = p.DailyEnrollCap,
        };
        _db.Pitches.Add(copy);
        await _db.SaveChangesAsync(ct);
        return Ok(ToDto(copy, new PitchStats(0, 0, 0, 0, 0, 0)));
    }

    /// <summary>
    /// Anuncios VISTOS en los leads (externalAdReply): id, título, cuántos leads trajo cada uno y a
    /// qué pitch está asignado. Para asignar ads a pitches desde la UI sin tipear ids.
    /// </summary>
    [HttpGet("ads")]
    public async Task<IActionResult> AdsSeen([FromQuery] string? productKey, [FromQuery] int days = 90, CancellationToken ct = default)
    {
        if (!CurrentUser.IsAdmin(User)) return Forbid();
        var since = DateTimeOffset.UtcNow.AddDays(-Math.Clamp(days, 1, 365));
        var q = _db.Leads.AsNoTracking().Where(l => l.AdId != null && l.CreatedAt >= since);
        if (!string.IsNullOrWhiteSpace(productKey)) q = q.Where(l => l.ProductKey == productKey);
        var ads = await q.GroupBy(l => new { l.ProductKey, l.AdId })
            .Select(g => new
            {
                g.Key.ProductKey,
                AdId = g.Key.AdId!,
                Title = g.Max(x => x.AdTitle),
                Leads = g.Count(),
                Replied = g.Count(x => x.FirstReplyAt != null),
                Closed = g.Count(x => x.Status == LeadStatus.Closed),
                LastSeen = g.Max(x => x.CreatedAt),
            })
            .OrderByDescending(x => x.Leads)
            .ToListAsync(ct);
        var pitches = await _db.Pitches.AsNoTracking().Select(p => new { p.Id, p.Name, p.AdIds }).ToListAsync(ct);
        return Ok(ads.Select(a => new
        {
            a.ProductKey, a.AdId, a.Title, a.Leads, a.Replied, a.Closed, a.LastSeen,
            Pitch = pitches.FirstOrDefault(p => p.AdIds.Contains(a.AdId)) is { } hit ? new { hit.Id, hit.Name } : null,
        }));
    }

    /// <summary>Enrolados de un pitch (lista tipo "Enrollment History").</summary>
    [HttpGet("{id:guid}/enrollments")]
    public async Task<IActionResult> Enrollments(Guid id, [FromQuery] int limit = 100, CancellationToken ct = default)
    {
        if (!CurrentUser.IsAdmin(User)) return Forbid();
        var rows = await _db.LeadPitchStates.AsNoTracking()
            .Where(s => s.PitchId == id)
            .OrderByDescending(s => s.EnrolledAt)
            .Take(Math.Clamp(limit, 1, 500))
            .Select(s => new
            {
                s.LeadId,
                LeadName = s.Lead!.Name,
                Phone = s.Lead.WhatsappPhone,
                Status = s.Lead.Status.ToString(),
                s.Lead.AdTitle,
                s.StepIndex, s.StepSentAt, s.NextStepDueAt, s.FollowupsSent, s.Replies,
                s.FirstReplyAfterPitchAt, s.CompletedAt, s.GaveUpAt, s.EnrolledAt,
            })
            .ToListAsync(ct);
        return Ok(rows);
    }

    /// <summary>Saca a un lead del pitch (sin borrar el historial): el bot/IA sigue normal.</summary>
    [HttpPost("enrollments/{leadId:guid}/stop")]
    public async Task<IActionResult> StopEnrollment(Guid leadId, CancellationToken ct)
    {
        if (!CurrentUser.IsAdmin(User)) return Forbid();
        var s = await _db.LeadPitchStates.FirstOrDefaultAsync(x => x.LeadId == leadId, ct);
        if (s is null) return NotFound();
        s.CompletedAt ??= DateTimeOffset.UtcNow;
        s.NextStepDueAt = null;
        s.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);
        return Ok(new { leadId, stopped = true });
    }

    /// <summary>Enrola a mano un lead existente (para probar un pitch con tu propio número, por ejemplo).</summary>
    [HttpPost("{id:guid}/enroll/{leadId:guid}")]
    public async Task<IActionResult> Enroll(Guid id, Guid leadId, CancellationToken ct)
    {
        if (!CurrentUser.IsAdmin(User)) return Forbid();
        var p = await _db.Pitches.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (p is null) return NotFound(new { error = "Pitch inexistente" });
        var lead = await _db.Leads.FirstOrDefaultAsync(l => l.Id == leadId, ct);
        if (lead is null) return NotFound(new { error = "Lead inexistente" });
        var existing = await _db.LeadPitchStates.FirstOrDefaultAsync(s => s.LeadId == leadId, ct);
        if (existing is not null) _db.LeadPitchStates.Remove(existing);
        _db.LeadPitchStates.Add(new LeadPitchState
        {
            LeadId = leadId, PitchId = id, StepIndex = -1,
            NextStepDueAt = DateTimeOffset.UtcNow.AddSeconds(5),
        });
        lead.BotMutedAt = null;
        lead.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);
        return Ok(new { leadId, pitchId = id, enrolled = true });
    }

    /// <summary>Cuántos leads del producto podrían enrolarse hoy en este pitch de Instagram (con handle, nunca contactados).</summary>
    [HttpGet("{id:guid}/enroll-preview")]
    public async Task<IActionResult> EnrollPreview(Guid id, [FromQuery] string? statuses, [FromQuery] string? city, CancellationToken ct)
    {
        if (!CurrentUser.IsAdmin(User)) return Forbid();
        var p = await _db.Pitches.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (p is null) return NotFound();
        var sts = ParseStatuses(statuses?.Split(',').ToList());
        var n = await _engine.CountEligibleForInstagramAsync(p, sts, city, ct);
        var accounts = await _db.InstagramAccounts.CountAsync(a => a.IsActive && a.IsLoggedIn && !a.IsActionBlocked, ct);
        return Ok(new { eligible = n, activeAccounts = accounts });
    }

    /// <summary>Enrola en masa (estilo "Bulk"): hasta Limit leads del producto con handle de IG y sin contacto previo.</summary>
    [HttpPost("{id:guid}/enroll-bulk")]
    public async Task<IActionResult> EnrollBulk(Guid id, [FromBody] BulkEnrollRequest req, CancellationToken ct)
    {
        if (!CurrentUser.IsAdmin(User)) return Forbid();
        var p = await _db.Pitches.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (p is null) return NotFound();
        if (p.Channel != MessageChannel.Instagram) return BadRequest(new { error = "El enrolamiento masivo es para pitches de Instagram (los de WhatsApp se disparan cuando el lead escribe desde el anuncio)" });
        if (!p.Active) return BadRequest(new { error = "Activá el pitch antes de enrolar" });
        var accounts = await _db.InstagramAccounts.CountAsync(a => a.IsActive && !a.IsActionBlocked, ct);
        if (accounts == 0) return BadRequest(new { error = "No hay cuentas de Instagram activas en el hub (Cuentas IG)" });
        var n = await _engine.EnrollBulkAsync(p, req.Limit, ParseStatuses(req.Statuses), req.City, ct);
        return Ok(new { enrolled = n });
    }

    private static List<LeadStatus> ParseStatuses(List<string>? raw)
    {
        var list = (raw ?? new()).Select(x => Enum.TryParse<LeadStatus>(x.Trim(), true, out var st) ? (LeadStatus?)st : null)
            .Where(x => x != null).Select(x => x!.Value).Distinct().ToList();
        return list.Count > 0 ? list : new List<LeadStatus> { LeadStatus.New, LeadStatus.Assigned };
    }

    // ───────────────────────────── helpers ─────────────────────────────

    private static string? Validate(UpsertPitchRequest r)
    {
        if (string.IsNullOrWhiteSpace(r.ProductKey)) return "Falta el producto";
        if (string.IsNullOrWhiteSpace(r.Name)) return "Falta el nombre";
        if (r.Steps is null || r.Steps.Count == 0) return "El pitch necesita al menos un paso";
        for (var i = 0; i < r.Steps.Count; i++)
        {
            var st = r.Steps[i];
            var msgs = (st.Messages ?? new()).Where(m => !string.IsNullOrWhiteSpace(m.Text) || m.MediaAssetId != null || !string.IsNullOrWhiteSpace(m.VoiceText)).ToList();
            if (msgs.Count == 0) return $"El paso {i + 1} no tiene mensajes";
            foreach (var f in st.FollowUps ?? new())
                if (string.IsNullOrWhiteSpace(f.Text) && f.MediaAssetId == null) return $"Un follow-up del paso {i + 1} está vacío";
        }
        if (!string.IsNullOrWhiteSpace(r.StatusOnReply) && !Enum.TryParse<LeadStatus>(r.StatusOnReply, true, out _))
            return "StatusOnReply inválido";
        return null;
    }

    private static void Apply(Pitch p, UpsertPitchRequest r)
    {
        p.ProductKey = r.ProductKey.Trim();
        p.Name = r.Name.Trim();
        p.Active = r.Active;
        p.SortOrder = r.SortOrder;
        p.AdIds = (r.AdIds ?? new()).Select(a => a?.Trim() ?? "").Where(a => a.Length > 0).Distinct().ToList();
        p.TriggerText = string.IsNullOrWhiteSpace(r.TriggerText) ? null : r.TriggerText.Trim();
        p.IsDefault = r.IsDefault;
        p.Steps = (r.Steps ?? new()).Select(s => new PitchStep
        {
            Title = string.IsNullOrWhiteSpace(s.Title) ? null : s.Title.Trim(),
            Messages = (s.Messages ?? new())
                .Where(m => !string.IsNullOrWhiteSpace(m.Text) || m.MediaAssetId != null || !string.IsNullOrWhiteSpace(m.VoiceText))
                .Select(m => new PitchMessage
                {
                    Text = m.Text ?? string.Empty,
                    MediaAssetId = m.MediaAssetId,
                    VoiceText = string.IsNullOrWhiteSpace(m.VoiceText) ? null : m.VoiceText.Trim(),
                    DelaySeconds = Math.Clamp(m.DelaySeconds, 0, 600),
                }).ToList(),
            FollowUps = (s.FollowUps ?? new()).Select(f => new PitchFollowUp
            {
                AfterHours = Math.Clamp(f.AfterHours, 0.05, 24 * 14),
                Text = f.Text ?? string.Empty,
                MediaAssetId = f.MediaAssetId,
            }).ToList(),
        }).ToList();
        p.AutoTagOnReply = string.IsNullOrWhiteSpace(r.AutoTagOnReply) ? null : r.AutoTagOnReply.Trim();
        p.StatusOnReply = string.IsNullOrWhiteSpace(r.StatusOnReply) ? null : r.StatusOnReply.Trim();
        p.AiAfterPitch = r.AiAfterPitch;
        p.ReplyDelayMinSec = Math.Clamp(r.ReplyDelayMinSec, 0, 3600);
        p.ReplyDelayMaxSec = Math.Clamp(r.ReplyDelayMaxSec, p.ReplyDelayMinSec, 3600);
        p.Channel = string.Equals(r.Channel, "Instagram", StringComparison.OrdinalIgnoreCase) ? MessageChannel.Instagram : MessageChannel.WhatsApp;
        p.AutoEnroll = p.Channel == MessageChannel.Instagram && r.AutoEnroll;
        p.DailyEnrollCap = Math.Clamp(r.DailyEnrollCap, 1, 500);
    }

    private async Task ClearOtherDefaultsAsync(Pitch p, CancellationToken ct)
    {
        var others = await _db.Pitches.Where(x => x.ProductKey == p.ProductKey && x.Id != p.Id && x.IsDefault).ToListAsync(ct);
        foreach (var o in others) { o.IsDefault = false; o.UpdatedAt = DateTimeOffset.UtcNow; }
    }

    private static PitchDto ToDto(Pitch p, PitchStats stats) => new(
        p.Id, p.ProductKey, p.Name, p.Active, p.SortOrder, p.AdIds, p.TriggerText, p.IsDefault,
        p.Steps.Select(s => new PitchStepDto(s.Title,
            s.Messages.Select(m => new PitchMessageDto(m.Text, m.MediaAssetId, m.VoiceText, m.DelaySeconds)).ToList(),
            s.FollowUps.Select(f => new PitchFollowUpDto(f.AfterHours, f.Text, f.MediaAssetId)).ToList())).ToList(),
        p.AutoTagOnReply, p.StatusOnReply, p.AiAfterPitch, p.ReplyDelayMinSec, p.ReplyDelayMaxSec, p.UpdatedAt, stats,
        p.Channel.ToString(), p.AutoEnroll, p.DailyEnrollCap);

    /// <summary>
    /// Enrolados / activos / respondieron / completados / abandonados / convertidos por pitch.
    /// Convertido = lead ganado (Closed) o con cuenta provisionada por el onboarding.
    /// </summary>
    private async Task<Dictionary<Guid, PitchStats?>> StatsByPitchAsync(List<Guid> ids, CancellationToken ct)
    {
        if (ids.Count == 0) return new();
        var rows = await _db.LeadPitchStates.AsNoTracking()
            .Where(s => ids.Contains(s.PitchId))
            .Select(s => new
            {
                s.PitchId,
                Active = s.CompletedAt == null && s.GaveUpAt == null,
                Replied = s.FirstReplyAfterPitchAt != null,
                Completed = s.CompletedAt != null,
                GaveUp = s.GaveUpAt != null,
                Converted = s.Lead!.Status == LeadStatus.Closed
                    || _db.Set<LeadOnboarding>().Any(o => o.LeadId == s.LeadId && o.ProvisionedAt != null),
            })
            .ToListAsync(ct);
        return rows.GroupBy(r => r.PitchId).ToDictionary(g => g.Key, g => (PitchStats?)new PitchStats(
            g.Count(), g.Count(x => x.Active), g.Count(x => x.Replied), g.Count(x => x.Completed), g.Count(x => x.GaveUp), g.Count(x => x.Converted)));
    }
}
