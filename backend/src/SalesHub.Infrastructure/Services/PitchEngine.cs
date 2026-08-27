using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SalesHub.Core.Abstractions;
using SalesHub.Core.Domain.Entities;
using SalesHub.Core.Domain.Enums;
using SalesHub.Infrastructure.Options;
using SalesHub.Infrastructure.Persistence;
using SalesHub.Infrastructure.Services.Social;

namespace SalesHub.Infrastructure.Services;

/// <summary>
/// Motor de PITCH por anuncio (modelo Smart Setter / GHL):
///  - Enrola al lead de anuncio en el pitch que matchea (ad id → texto prellenado → default).
///  - Manda cada paso (grupo de mensajes: texto / media / audio con delays) por la línea donde
///    VIVE la conversación (la que recibió el ad), registrando cada envío ANTES de mandarlo.
///  - La respuesta del lead avanza al paso siguiente (auto-tag + etapa del CRM).
///  - Si no responde, salen los follow-ups del paso (en horas, dentro del horario activo).
///  - Terminado el guion: IA libre (AiAfterPitch) o handoff humano (bot muteado).
/// Mientras el pitch está activo, el agente de IA NO toca la conversación.
/// </summary>
public class PitchEngine
{
    private const int MaxStepsPerTick = 15;
    private const int MaxFollowupsPerTick = 8;
    // Sin follow-ups configurados: cuánto esperamos sin respuesta antes de dar por perdido el paso.
    private static readonly TimeSpan GiveUpAfterNoFollowups = TimeSpan.FromHours(72);
    // Con follow-ups agotados: cuánto esperamos después del último antes de dar por perdido.
    private static readonly TimeSpan GiveUpAfterLastFollowup = TimeSpan.FromHours(48);

    private readonly ApplicationDbContext _db;
    private readonly IEvolutionClient _evo;
    private readonly SellerLineSender _lineSender;
    private readonly IMessageRenderer _renderer;
    private readonly ElevenLabsClient _tts;
    private readonly VoiceNoteOptions _voice;
    private readonly ILeadAssigner _assigner;
    private readonly ILogger<PitchEngine> _log;

    public PitchEngine(ApplicationDbContext db, IEvolutionClient evo, SellerLineSender lineSender,
        IMessageRenderer renderer, ElevenLabsClient tts, IOptions<VoiceNoteOptions> voice, ILeadAssigner assigner, ILogger<PitchEngine> log)
    {
        _db = db; _evo = evo; _lineSender = lineSender; _renderer = renderer; _tts = tts; _voice = voice.Value; _assigner = assigner; _log = log;
    }

    /// <summary>Marca de los outbox rows que pertenecen a un pitch (CadenceCategory).</summary>
    public static string OutboxTag(Guid pitchId) => $"pitch:{pitchId:N}";

    // ───────────────────────────── Enrolamiento / avance por respuesta ─────────────────────────────

    /// <summary>
    /// Hook del inbound (ya persistido). <paramref name="isNewLead"/> = el lead se creó con este
    /// mensaje. Enrola leads de anuncio sin historia y avanza el paso de los ya enrolados.
    /// </summary>
    public async Task OnInboundAsync(Lead lead, string text, ConversationService.AdReferral? ad, bool isNewLead, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var state = await _db.LeadPitchStates.Include(s => s.Pitch).FirstOrDefaultAsync(s => s.LeadId == lead.Id, ct);
        if (state is null)
        {
            var isAdLead = lead.Source == LeadSource.WhatsAppAd || ad is not null || !string.IsNullOrWhiteSpace(lead.AdId);
            if (!isAdLead) return;
            if (!isNewLead)
            {
                // Lead que ya existía: solo si nunca le escribimos (si no, pisaríamos una charla en curso).
                var hasOutbound = await _db.ConversationMessages
                    .AnyAsync(m => m.LeadId == lead.Id && m.Direction == MessageDirection.Outbound, ct);
                if (hasOutbound) return;
                if (lead.BotMutedAt is not null) return; // takeover humano explícito
            }
            var pitch = await ResolvePitchAsync(lead.ProductKey, lead.AdId ?? ad?.SourceId, text, ct);
            if (pitch is null) return;
            _db.LeadPitchStates.Add(new LeadPitchState
            {
                LeadId = lead.Id,
                PitchId = pitch.Id,
                StepIndex = -1,
                NextStepDueAt = now.Add(HumanDelay(pitch)),
                EnrolledAt = now,
                UpdatedAt = now,
            });
            // El pitch es dueño de la charla y manda por la línea donde entró el ad: el mute
            // automático "línea de app" ya no aplica.
            if (isNewLead) lead.BotMutedAt = null;
            await _db.SaveChangesAsync(ct);
            _log.LogInformation("Pitch: lead {Lead} enrolado en \"{Pitch}\" (ad={Ad})", lead.Id, pitch.Name, lead.AdId ?? ad?.SourceId ?? "-");
            return;
        }

        if (state.CompletedAt is not null || state.GaveUpAt is not null) return;
        if (state.StepIndex < 0) return; // todavía no salió el paso 0: el mensaje suma contexto, nada más
        var p = state.Pitch!;

        state.Replies++;
        state.UpdatedAt = now;
        var firstReply = state.FirstReplyAfterPitchAt is null;
        state.FirstReplyAfterPitchAt ??= now;
        if (firstReply)
        {
            if (!string.IsNullOrWhiteSpace(p.AutoTagOnReply))
            {
                var tag = p.AutoTagOnReply!.Trim();
                if (!lead.Tags.Contains(tag, StringComparer.OrdinalIgnoreCase)) lead.Tags = lead.Tags.Append(tag).ToList();
            }
            if (!string.IsNullOrWhiteSpace(p.StatusOnReply)
                && Enum.TryParse<LeadStatus>(p.StatusOnReply, true, out var st)
                && lead.Status is not (LeadStatus.Closed or LeadStatus.Lost or LeadStatus.Blocked))
                lead.Status = st;
            lead.UpdatedAt = now;
        }

        if (state.StepIndex + 1 < p.Steps.Count)
        {
            // Respondió → siguiente paso (con espera humana).
            state.NextStepDueAt = now.Add(HumanDelay(p));
            state.FollowupsSent = 0;
            state.LastFollowupAt = null;
        }
        else
        {
            await CompleteAsync(lead, state, p, "respondió al último paso", ct);
        }
        await _db.SaveChangesAsync(ct);
    }

    private async Task CompleteAsync(Lead lead, LeadPitchState state, Pitch p, string why, CancellationToken ct)
    {
        state.CompletedAt = DateTimeOffset.UtcNow;
        state.NextStepDueAt = null;
        state.UpdatedAt = state.CompletedAt.Value;
        // IA libre solo si la charla vive en la línea del vendedor (la IA responde por esa línea);
        // si entró por una línea de app sin vendedor, el número sería otro → handoff.
        var sender = await ResolveSenderAsync(lead, ct);
        var aiOk = p.AiAfterPitch && sender is { IsSellerLine: true };
        if (!aiOk) lead.BotMutedAt ??= DateTimeOffset.UtcNow;
        lead.UpdatedAt = DateTimeOffset.UtcNow;
        _log.LogInformation("Pitch \"{Pitch}\" completado para lead {Lead} ({Why}) → {Next}", p.Name, lead.Id, why, aiOk ? "IA libre" : "handoff humano");
    }

    /// <summary>Pitch que corresponde a un lead de anuncio: ad id → texto prellenado → default del producto.</summary>
    public async Task<Pitch?> ResolvePitchAsync(string productKey, string? adId, string? text, CancellationToken ct)
    {
        var pitches = await _db.Pitches.AsNoTracking()
            .Where(p => p.ProductKey == productKey && p.Active && p.Channel == MessageChannel.WhatsApp)
            .OrderBy(p => p.SortOrder).ThenBy(p => p.CreatedAt)
            .ToListAsync(ct);
        if (pitches.Count == 0) return null;
        if (!string.IsNullOrWhiteSpace(adId))
        {
            var byAd = pitches.FirstOrDefault(p => p.AdIds.Any(a => string.Equals(a?.Trim(), adId.Trim(), StringComparison.OrdinalIgnoreCase)));
            if (byAd is not null) return byAd;
        }
        if (!string.IsNullOrWhiteSpace(text))
        {
            var lower = text.ToLowerInvariant();
            var byText = pitches
                .Where(p => !string.IsNullOrWhiteSpace(p.TriggerText))
                .OrderByDescending(p => p.TriggerText!.Length) // el match más específico gana
                .FirstOrDefault(p => lower.Contains(p.TriggerText!.Trim().ToLowerInvariant()));
            if (byText is not null) return byText;
        }
        return pitches.FirstOrDefault(p => p.IsDefault);
    }

    private static TimeSpan HumanDelay(Pitch p)
    {
        var min = Math.Max(0, p.ReplyDelayMinSec);
        var max = Math.Max(min, p.ReplyDelayMaxSec);
        return TimeSpan.FromSeconds(Random.Shared.Next(min, max + 1));
    }

    // ───────────────────────────── Tick: pasos pendientes + follow-ups ─────────────────────────────

    public async Task<int> TickAsync(CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var done = 0;

        // 0) Instagram: auto-enrolar leads con handle (outbound) y confirmar pasos ya despachados
        //    por la cola de DMs (el sender de IG es asíncrono: encolamos y después miramos SentAt).
        try { done += await AutoEnrollInstagramAsync(now, ct); }
        catch (Exception ex) { _log.LogError(ex, "Pitch IG: auto-enroll falló"); }
        try { await ConfirmInstagramStepsAsync(now, ct); }
        catch (Exception ex) { _log.LogError(ex, "Pitch IG: confirmación de pasos falló"); }

        // 1) Pasos pendientes (paso 0 al enrolar, o el siguiente tras una respuesta).
        var dueStates = await _db.LeadPitchStates
            .Include(s => s.Pitch)
            .Include(s => s.Lead)!.ThenInclude(l => l!.Product)
            .Include(s => s.Lead)!.ThenInclude(l => l!.Seller)!.ThenInclude(se => se!.EvolutionInstance)
            .Where(s => s.CompletedAt == null && s.GaveUpAt == null && s.NextStepDueAt != null && s.NextStepDueAt <= now)
            .OrderBy(s => s.NextStepDueAt)
            .Take(MaxStepsPerTick)
            .ToListAsync(ct);
        foreach (var s in dueStates)
        {
            try { if (await SendNextStepAsync(s, ct)) done++; }
            catch (Exception ex) { _log.LogError(ex, "Pitch: falló el paso para lead {Lead}", s.LeadId); }
        }

        // 2) Follow-ups del paso actual (sin respuesta del lead desde que salió el paso).
        var waiting = await _db.LeadPitchStates
            .Include(s => s.Pitch)
            .Include(s => s.Lead)!.ThenInclude(l => l!.Product)
            .Include(s => s.Lead)!.ThenInclude(l => l!.Seller)!.ThenInclude(se => se!.EvolutionInstance)
            .Where(s => s.CompletedAt == null && s.GaveUpAt == null && s.NextStepDueAt == null && s.StepIndex >= 0
                && s.Lead != null && (s.Lead.LastInboundAt == null || s.Lead.LastInboundAt < s.StepSentAt))
            .OrderBy(s => s.StepSentAt)
            .Take(200)
            .ToListAsync(ct);
        var fuSent = 0;
        foreach (var s in waiting)
        {
            if (fuSent >= MaxFollowupsPerTick) break;
            try { if (await MaybeFollowupAsync(s, now, ct)) { fuSent++; done++; } }
            catch (Exception ex) { _log.LogError(ex, "Pitch: falló el follow-up para lead {Lead}", s.LeadId); }
        }
        return done;
    }

    private async Task<bool> SendNextStepAsync(LeadPitchState s, CancellationToken ct)
    {
        var lead = s.Lead!; var p = s.Pitch!;
        var now = DateTimeOffset.UtcNow;
        if (!p.Active) { s.NextStepDueAt = null; s.UpdatedAt = now; await _db.SaveChangesAsync(ct); return false; }
        // Takeover humano mientras esperaba el paso: el humano manda, el pitch se retira.
        if (lead.BotMutedAt is not null && s.StepIndex >= 0)
        {
            s.CompletedAt = now; s.NextStepDueAt = null; s.UpdatedAt = now;
            await _db.SaveChangesAsync(ct);
            _log.LogInformation("Pitch: lead {Lead} tomado por humano — guion cortado", lead.Id);
            return false;
        }
        if (lead.Status is LeadStatus.Lost or LeadStatus.Blocked or LeadStatus.NoWhatsApp)
        {
            s.GaveUpAt = now; s.NextStepDueAt = null; s.UpdatedAt = now;
            await _db.SaveChangesAsync(ct);
            return false;
        }
        var idx = s.StepIndex + 1;
        if (idx >= p.Steps.Count)
        {
            await CompleteAsync(lead, s, p, "sin más pasos", ct);
            await _db.SaveChangesAsync(ct);
            return false;
        }
        if (p.Channel == MessageChannel.Instagram)
            return await EnqueueInstagramStepAsync(s, idx, now, ct);

        var sender = await ResolveSenderAsync(lead, ct);
        if (sender is null)
        {
            // Sin línea para mandar (todo desconectado o solo-escucha): reintentar más tarde sin
            // martillar el log.
            s.NextStepDueAt = now.AddMinutes(5); s.UpdatedAt = now;
            await _db.SaveChangesAsync(ct);
            _log.LogWarning("Pitch: lead {Lead} sin línea conectada para mandar el paso {Step} — reintento en 5 min", lead.Id, idx + 1);
            return false;
        }
        var step = p.Steps[idx];
        var sentAny = false;
        for (var i = 0; i < step.Messages.Count; i++)
        {
            var m = step.Messages[i];
            if (i > 0)
            {
                var prev = step.Messages[i - 1];
                var pause = Math.Clamp(prev.DelaySeconds, 1, 180);
                await TypingAsync(sender, lead, pause, ct);
                await Task.Delay(TimeSpan.FromSeconds(pause), ct);
            }
            else
            {
                await TypingAsync(sender, lead, 3, ct);
                await Task.Delay(TimeSpan.FromSeconds(3), ct);
            }
            if (await SendMessageAsync(sender, lead, m.Text, m.MediaAssetId, m.VoiceText, ct)) sentAny = true;
        }
        s.StepIndex = idx;
        s.StepSentAt = now;
        s.NextStepDueAt = null;
        s.FollowupsSent = 0;
        s.LastFollowupAt = null;
        s.UpdatedAt = now;
        lead.SentAt ??= now;
        if (lead.Status is LeadStatus.New or LeadStatus.Assigned or LeadStatus.Queued) lead.Status = LeadStatus.Sent;
        lead.UpdatedAt = now;
        await _db.SaveChangesAsync(ct);
        _log.LogInformation("Pitch \"{Pitch}\": paso {Step}/{Total} enviado a lead {Lead} ({N} msgs)", p.Name, idx + 1, p.Steps.Count, lead.Id, step.Messages.Count);
        return sentAny;
    }

    private async Task<bool> MaybeFollowupAsync(LeadPitchState s, DateTimeOffset now, CancellationToken ct)
    {
        var lead = s.Lead!; var p = s.Pitch!;
        if (s.StepIndex < 0 || s.StepIndex >= p.Steps.Count || s.StepSentAt is null) return false;
        if (!p.Active) return false;
        if (lead.BotMutedAt is not null)
        {
            s.CompletedAt = now; s.UpdatedAt = now; await _db.SaveChangesAsync(ct); return false;
        }
        var followUps = p.Steps[s.StepIndex].FollowUps;
        if (s.FollowupsSent >= followUps.Count)
        {
            // Agotados (o no había): dar por perdido después de la espera de gracia.
            var anchor = s.LastFollowupAt ?? s.StepSentAt.Value;
            var grace = followUps.Count == 0 ? GiveUpAfterNoFollowups : GiveUpAfterLastFollowup;
            if (anchor + grace <= now)
            {
                s.GaveUpAt = now; s.UpdatedAt = now;
                await _db.SaveChangesAsync(ct);
                _log.LogInformation("Pitch \"{Pitch}\": lead {Lead} sin respuesta tras {N} follow-ups → gave up", p.Name, lead.Id, s.FollowupsSent);
            }
            return false;
        }
        var fu = followUps[s.FollowupsSent];
        var since = s.LastFollowupAt ?? s.StepSentAt.Value;
        if (since.AddHours(Math.Max(0.05, fu.AfterHours)) > now) return false;
        if (!WithinActiveHours(lead.Seller, now)) return false; // proactivo: solo en horario
        bool ok;
        if (p.Channel == MessageChannel.Instagram)
        {
            if (string.IsNullOrWhiteSpace(fu.Text)) { s.FollowupsSent++; s.LastFollowupAt = now; s.UpdatedAt = now; await _db.SaveChangesAsync(ct); return false; }
            ok = await EnqueueInstagramDmAsync(lead, p, s.StepIndex, fu.Text, now, ct);
        }
        else
        {
            var sender = await ResolveSenderAsync(lead, ct);
            if (sender is null) return false;
            await TypingAsync(sender, lead, 3, ct);
            await Task.Delay(TimeSpan.FromSeconds(3), ct);
            ok = await SendMessageAsync(sender, lead, fu.Text, fu.MediaAssetId, null, ct);
        }
        s.FollowupsSent++;
        s.LastFollowupAt = now;
        s.UpdatedAt = now;
        await _db.SaveChangesAsync(ct);
        _log.LogInformation("Pitch \"{Pitch}\": follow-up {N} del paso {Step} a lead {Lead} ({Ok})", p.Name, s.FollowupsSent, s.StepIndex + 1, lead.Id, ok ? "ok" : "falló");
        return ok;
    }

    // ───────────────────────────── Instagram (outbound por la cola de DMs) ─────────────────────────────

    /// <summary>
    /// Enrola leads con handle de IG en los pitches de Instagram con AutoEnroll, respetando el
    /// cap diario del pitch. Candidatos: del producto, con InstagramHandle, sin estado de pitch,
    /// sin outbound previo, en New/Assigned (nunca contactados), mejor score primero.
    /// </summary>
    private async Task<int> AutoEnrollInstagramAsync(DateTimeOffset now, CancellationToken ct)
    {
        var pitches = await _db.Pitches.AsNoTracking()
            .Where(p => p.Active && p.Channel == MessageChannel.Instagram && p.AutoEnroll)
            .ToListAsync(ct);
        if (pitches.Count == 0) return 0;
        var total = 0;
        var dayStart = now.Date;
        foreach (var p in pitches)
        {
            var today = await _db.LeadPitchStates.CountAsync(s => s.PitchId == p.Id && s.EnrolledAt >= dayStart, ct);
            var room = Math.Max(0, p.DailyEnrollCap - today);
            if (room == 0) continue;
            var n = await EnrollBulkAsync(p, Math.Min(room, 10), new[] { LeadStatus.New, LeadStatus.Assigned }, null, ct);
            total += n;
        }
        return total;
    }

    /// <summary>Enrola hasta <paramref name="limit"/> leads del producto en el pitch (Instagram). Devuelve cuántos.</summary>
    public async Task<int> EnrollBulkAsync(Pitch p, int limit, IReadOnlyCollection<LeadStatus> statuses, string? city, CancellationToken ct)
    {
        var q = EligibleForInstagram(p, statuses, city);
        var leads = await q.OrderByDescending(l => l.Score).ThenBy(l => l.CreatedAt).Take(Math.Clamp(limit, 1, 500)).ToListAsync(ct);
        if (leads.Count == 0) return 0;
        var now = DateTimeOffset.UtcNow;
        var i = 0;
        foreach (var lead in leads)
        {
            if (lead.SellerId is null)
            {
                var owner = await _assigner.PickOwnerAsync(p.ProductKey, ct);
                if (owner is null) continue;
                lead.SellerId = owner; lead.AssignedAt = now;
                if (lead.Status == LeadStatus.New) lead.Status = LeadStatus.Assigned;
            }
            _db.LeadPitchStates.Add(new LeadPitchState
            {
                LeadId = lead.Id, PitchId = p.Id, StepIndex = -1,
                // Escalonados: el sender de IG manda de a uno igual, esto solo ordena la cola.
                NextStepDueAt = now.AddSeconds(i * 20),
                EnrolledAt = now, UpdatedAt = now,
            });
            i++;
        }
        await _db.SaveChangesAsync(ct);
        _log.LogInformation("Pitch IG \"{Pitch}\": {N} leads enrolados", p.Name, i);
        return i;
    }

    public async Task<int> CountEligibleForInstagramAsync(Pitch p, IReadOnlyCollection<LeadStatus> statuses, string? city, CancellationToken ct)
        => await EligibleForInstagram(p, statuses, city).CountAsync(ct);

    private IQueryable<Lead> EligibleForInstagram(Pitch p, IReadOnlyCollection<LeadStatus> statuses, string? city)
    {
        var sts = statuses.ToList();
        var q = _db.Leads.Where(l => l.ProductKey == p.ProductKey
            && l.InstagramHandle != null && l.InstagramHandle != ""
            && sts.Contains(l.Status)
            && l.BotMutedAt == null
            && !_db.LeadPitchStates.Any(s => s.LeadId == l.Id)
            && !_db.ConversationMessages.Any(m => m.LeadId == l.Id && m.Direction == MessageDirection.Outbound)
            && !_db.Outbox.Any(o => o.LeadId == l.Id && (o.Status == OutboxStatus.Scheduled || o.Status == OutboxStatus.Sending || o.Status == OutboxStatus.Sent)));
        if (!string.IsNullOrWhiteSpace(city)) q = q.Where(l => l.City != null && l.City.ToLower().Contains(city.ToLower()));
        return q;
    }

    /// <summary>
    /// Paso de un pitch de Instagram: encola cada mensaje (solo texto) en el outbox con Channel=Instagram,
    /// espaciados por el delay del mensaje. StepSentAt queda null hasta que el sender los despache
    /// (ver <see cref="ConfirmInstagramStepsAsync"/>).
    /// </summary>
    private async Task<bool> EnqueueInstagramStepAsync(LeadPitchState s, int idx, DateTimeOffset now, CancellationToken ct)
    {
        var lead = s.Lead!; var p = s.Pitch!;
        if (string.IsNullOrWhiteSpace(lead.InstagramHandle))
        {
            s.GaveUpAt = now; s.NextStepDueAt = null; s.UpdatedAt = now;
            await _db.SaveChangesAsync(ct);
            return false;
        }
        if (lead.SellerId is null)
        {
            var owner = await _assigner.PickOwnerAsync(p.ProductKey, ct);
            if (owner is null) { s.NextStepDueAt = now.AddHours(1); s.UpdatedAt = now; await _db.SaveChangesAsync(ct); return false; }
            lead.SellerId = owner; lead.AssignedAt = now;
        }
        var step = p.Steps[idx];
        var when = now;
        var queued = 0;
        for (var i = 0; i < step.Messages.Count; i++)
        {
            var m = step.Messages[i];
            var text = string.IsNullOrWhiteSpace(m.Text) ? (m.VoiceText ?? string.Empty) : m.Text; // IG: texto o el guion del audio
            if (string.IsNullOrWhiteSpace(text)) continue;
            if (queued > 0) when = when.AddSeconds(Math.Clamp(step.Messages[i - 1].DelaySeconds, 1, 3600));
            var rendered = _renderer.RenderTemplate(text, lead, lead.Product!, lead.Seller);
            _db.Outbox.Add(new MessageOutbox
            {
                Id = Guid.NewGuid(), LeadId = lead.Id, SellerId = lead.SellerId!.Value,
                Channel = MessageChannel.Instagram, EvolutionInstance = string.Empty, WhatsappPhone = lead.WhatsappPhone ?? string.Empty,
                Message = rendered, StepIndex = idx, CadenceCategory = OutboxTag(p.Id),
                ScheduledAt = when, Status = OutboxStatus.Scheduled, Priority = 60,
            });
            queued++;
        }
        s.StepIndex = idx;
        s.StepSentAt = null;   // se confirma cuando el sender despacha
        s.NextStepDueAt = null;
        s.FollowupsSent = 0;
        s.LastFollowupAt = null;
        s.UpdatedAt = now;
        if (queued == 0)
        {
            // Paso sin texto (solo media): en IG no hay nada que mandar → lo damos por enviado.
            s.StepSentAt = now;
        }
        await _db.SaveChangesAsync(ct);
        _log.LogInformation("Pitch IG \"{Pitch}\": paso {Step}/{Total} encolado a @{Handle} ({N} DMs)", p.Name, idx + 1, p.Steps.Count, lead.InstagramHandle, queued);
        return queued > 0;
    }

    private async Task<bool> EnqueueInstagramDmAsync(Lead lead, Pitch p, int stepIdx, string text, DateTimeOffset now, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(lead.InstagramHandle) || lead.SellerId is null) return false;
        var rendered = _renderer.RenderTemplate(text, lead, lead.Product!, lead.Seller);
        _db.Outbox.Add(new MessageOutbox
        {
            Id = Guid.NewGuid(), LeadId = lead.Id, SellerId = lead.SellerId.Value,
            Channel = MessageChannel.Instagram, EvolutionInstance = string.Empty, WhatsappPhone = lead.WhatsappPhone ?? string.Empty,
            Message = rendered, StepIndex = stepIdx, CadenceCategory = OutboxTag(p.Id) + ":fu",
            ScheduledAt = now, Status = OutboxStatus.Scheduled, Priority = 60,
        });
        return true;
    }

    /// <summary>
    /// Estados de IG con paso encolado pero no confirmado: cuando todos los DMs del paso salieron
    /// (o fallaron), fija StepSentAt (arranca el reloj de los follow-ups) o da por perdido.
    /// </summary>
    private async Task ConfirmInstagramStepsAsync(DateTimeOffset now, CancellationToken ct)
    {
        var pending = await _db.LeadPitchStates
            .Include(s => s.Pitch)
            .Where(s => s.CompletedAt == null && s.GaveUpAt == null && s.StepIndex >= 0 && s.StepSentAt == null
                && s.Pitch!.Channel == MessageChannel.Instagram)
            .Take(300)
            .ToListAsync(ct);
        foreach (var s in pending)
        {
            var tag = OutboxTag(s.PitchId);
            var rows = await _db.Outbox.AsNoTracking()
                .Where(o => o.LeadId == s.LeadId && o.CadenceCategory == tag && o.StepIndex == s.StepIndex)
                .Select(o => new { o.Status, o.SentAt })
                .ToListAsync(ct);
            if (rows.Count == 0) { s.StepSentAt = now; s.UpdatedAt = now; continue; }
            if (rows.Any(r => r.Status == OutboxStatus.Scheduled || r.Status == OutboxStatus.Sending)) continue;
            var sent = rows.Where(r => r.Status == OutboxStatus.Sent).Select(r => r.SentAt).Where(x => x != null).ToList();
            if (sent.Count > 0) { s.StepSentAt = sent.Max(); s.UpdatedAt = now; continue; }
            // Nada salió (cancelado por respuesta → OnInbound ya avanzó; fallido → perdido).
            if (rows.All(r => r.Status == OutboxStatus.Failed)) { s.GaveUpAt = now; s.UpdatedAt = now; }
            else { s.StepSentAt = now; s.UpdatedAt = now; }
        }
        await _db.SaveChangesAsync(ct);
    }

    // ───────────────────────────── Transporte ─────────────────────────────

    /// <summary>Por dónde sale el pitch para este lead.</summary>
    public sealed record Sender(string? InstanceName, Guid? BridgeSellerId, bool IsSellerLine)
    {
        public bool IsBridge => BridgeSellerId is not null && InstanceName is null;
    }

    /// <summary>
    /// La línea donde VIVE la conversación (la que recibió el último inbound) si está conectada y
    /// no es solo-escucha; si no, la línea del vendedor; si no, el celu (bridge) del vendedor.
    /// </summary>
    public async Task<Sender?> ResolveSenderAsync(Lead lead, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(lead.WhatsappPhone)) return null;
        var lastInstance = await _db.ConversationMessages.AsNoTracking()
            .Where(m => m.LeadId == lead.Id && m.Direction == MessageDirection.Inbound && m.EvolutionInstance != null)
            .OrderByDescending(m => m.Timestamp)
            .Select(m => m.EvolutionInstance)
            .FirstOrDefaultAsync(ct);
        if (!string.IsNullOrWhiteSpace(lastInstance))
        {
            var line = await _db.EvolutionInstances.AsNoTracking().FirstOrDefaultAsync(i => i.InstanceName == lastInstance, ct);
            if (line is not null && line.Status == InstanceStatus.Connected && !line.ListenOnly)
                return new Sender(line.InstanceName, null, line.SellerId != null && line.SellerId == lead.SellerId);
        }
        var sellerInst = lead.Seller?.EvolutionInstance;
        if (sellerInst is not null && sellerInst.Status == InstanceStatus.Connected && !sellerInst.ListenOnly)
            return new Sender(sellerInst.InstanceName, null, true);
        if (lead.SellerId is not null && await _db.Devices.AnyAsync(d => d.SellerId == lead.SellerId, ct))
            return new Sender(null, lead.SellerId, true);
        return null;
    }

    private async Task TypingAsync(Sender sender, Lead lead, int seconds, CancellationToken ct)
    {
        if (sender.InstanceName is null) return;
        try { await _evo.SetPresenceTypingAsync(sender.InstanceName, lead.WhatsappPhone!, seconds, ct); } catch { /* best-effort */ }
    }

    /// <summary>Manda UN mensaje del pitch (texto y/o media/audio) y lo registra en la conversación.</summary>
    private async Task<bool> SendMessageAsync(Sender sender, Lead lead, string? text, Guid? mediaAssetId, string? voiceText, CancellationToken ct)
    {
        var product = lead.Product!;
        var rendered = string.IsNullOrWhiteSpace(text) ? string.Empty : _renderer.RenderTemplate(text, lead, product, lead.Seller);
        var any = false;

        // 1) Nota de voz generada (voz clonada) — gana sobre el asset.
        if (!string.IsNullOrWhiteSpace(voiceText) && sender.InstanceName is not null)
        {
            var script = _renderer.RenderTemplate(voiceText, lead, product, lead.Seller);
            var ok = await TrySendGeneratedVoiceAsync(sender.InstanceName, lead, script, ct);
            if (ok) any = true;
            else if (string.IsNullOrWhiteSpace(rendered)) rendered = script; // fallback: el guion como texto
        }
        // 2) Asset (audio → PTT; imagen/video/pdf → media con caption = texto).
        else if (mediaAssetId is not null && sender.InstanceName is not null)
        {
            var asset = await _db.MediaAssets.AsNoTracking().FirstOrDefaultAsync(a => a.Id == mediaAssetId, ct);
            if (asset is not null)
            {
                var mt = asset.MimeType ?? string.Empty;
                if (mt.StartsWith("audio", StringComparison.OrdinalIgnoreCase))
                {
                    var ok = await TrySendAudioAssetAsync(sender.InstanceName, lead, asset, ct);
                    if (ok) any = true;
                }
                else
                {
                    var caption = string.IsNullOrWhiteSpace(rendered) ? null : rendered;
                    var msg = Record(lead, sender, (mt.StartsWith("video", StringComparison.OrdinalIgnoreCase) ? "[video]"
                        : mt.StartsWith("image", StringComparison.OrdinalIgnoreCase) ? "[imagen]" : "[archivo]")
                        + (caption is null ? string.Empty : " " + caption));
                    await _db.SaveChangesAsync(ct);
                    var ok = await _evo.SendMediaAsync(sender.InstanceName, lead.WhatsappPhone!, asset.Content, asset.MimeType, asset.FileName, caption, ct);
                    if (!ok) msg.Status = MessageDeliveryStatus.Failed; else any = true;
                    await _db.SaveChangesAsync(ct);
                    return ok; // el texto ya fue como caption
                }
            }
        }
        else if (mediaAssetId is not null && sender.IsBridge)
        {
            _log.LogWarning("Pitch: el celu (bridge) no manda adjuntos — se manda solo el texto (lead {Lead})", lead.Id);
        }

        if (string.IsNullOrWhiteSpace(rendered)) return any;
        // 3) Texto.
        var m = Record(lead, sender, rendered);
        await _db.SaveChangesAsync(ct);
        bool sentText;
        if (sender.InstanceName is not null)
            sentText = await _evo.SendTextAsync(sender.InstanceName, lead.WhatsappPhone!, rendered, ct);
        else
            sentText = await _lineSender.SendTextAsync(sender.BridgeSellerId, null, lead.WhatsappPhone!, rendered, ct);
        if (!sentText) m.Status = MessageDeliveryStatus.Failed;
        await _db.SaveChangesAsync(ct);
        return sentText || any;
    }

    private async Task<bool> TrySendGeneratedVoiceAsync(string instance, Lead lead, string script, CancellationToken ct)
    {
        if (!_tts.IsConfigured) return false;
        var mp3 = await _tts.SynthesizeAsync(script, _voice.VoiceId,
            new ElevenLabsClient.TtsVoiceSettings(_voice.Stability, _voice.SimilarityBoost, _voice.Style, _voice.Speed), ct);
        if (mp3 is null) return false;
        PreparedVoiceNote prep;
        try { prep = await _evo.PrepareVoiceNoteAsync(mp3, ct); }
        catch (Exception ex) { _log.LogWarning(ex, "Pitch: falló la conversión del audio (lead {Lead})", lead.Id); return false; }
        var msg = Record(lead, new Sender(instance, null, true), "[audio] " + script);
        await _db.SaveChangesAsync(ct);
        try { await _evo.SetPresenceRecordingAsync(instance, lead.WhatsappPhone!, 4, ct); await Task.Delay(TimeSpan.FromSeconds(4), ct); } catch { }
        var ok = await _evo.SendPreparedVoiceNoteAsync(instance, lead.WhatsappPhone!, prep.OggBytes, ct);
        if (!ok) msg.Status = MessageDeliveryStatus.Failed;
        await _db.SaveChangesAsync(ct);
        return ok;
    }

    private async Task<bool> TrySendAudioAssetAsync(string instance, Lead lead, MediaAsset asset, CancellationToken ct)
    {
        var msg = Record(lead, new Sender(instance, null, true), "[audio]");
        await _db.SaveChangesAsync(ct);
        try { await _evo.SetPresenceRecordingAsync(instance, lead.WhatsappPhone!, 4, ct); await Task.Delay(TimeSpan.FromSeconds(4), ct); } catch { }
        bool ok;
        try
        {
            var prep = await _evo.PrepareVoiceNoteAsync(asset.Content, ct);
            ok = await _evo.SendPreparedVoiceNoteAsync(instance, lead.WhatsappPhone!, prep.OggBytes, ct);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Pitch: no se pudo preparar el audio {Asset}; se manda como archivo", asset.Id);
            ok = await _evo.SendMediaAsync(instance, lead.WhatsappPhone!, asset.Content, asset.MimeType, asset.FileName, null, ct);
        }
        if (!ok) msg.Status = MessageDeliveryStatus.Failed;
        await _db.SaveChangesAsync(ct);
        return ok;
    }

    /// <summary>Registra el outbound ANTES de mandar (el eco fromMe del webhook lo matchea y no mutea el bot).</summary>
    private ConversationMessage Record(Lead lead, Sender sender, string text)
    {
        var m = new ConversationMessage
        {
            Id = Guid.NewGuid(),
            LeadId = lead.Id,
            SellerId = lead.SellerId,
            Direction = MessageDirection.Outbound,
            Status = MessageDeliveryStatus.Sent,
            Text = text,
            EvolutionInstance = sender.InstanceName ?? string.Empty,
            Timestamp = DateTimeOffset.UtcNow,
            IsRead = true,
        };
        _db.ConversationMessages.Add(m);
        return m;
    }

    private static bool WithinActiveHours(Seller? seller, DateTimeOffset nowUtc)
    {
        var tzId = seller?.Timezone ?? "America/Argentina/Buenos_Aires";
        TimeZoneInfo tz;
        try { tz = TimeZoneInfo.FindSystemTimeZoneById(tzId); }
        catch { tz = TimeZoneInfo.Utc; }
        var local = TimeZoneInfo.ConvertTime(nowUtc, tz);
        var start = seller?.ActiveHoursStart ?? 9;
        var end = seller?.ActiveHoursEnd ?? 21;
        if (start >= end) return true;
        return local.Hour >= start && local.Hour < end;
    }
}
