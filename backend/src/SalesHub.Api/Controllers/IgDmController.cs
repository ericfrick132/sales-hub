using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SalesHub.Core.Domain.Entities;
using SalesHub.Core.Domain.Enums;
using SalesHub.Infrastructure.Instagram;
using SalesHub.Infrastructure.Persistence;

namespace SalesHub.Api.Controllers;

/// <summary>
/// DMs por Instagram de cada app: la configuración del plan (quién manda, tope o rampa semanal,
/// horario, pausas, textos, seguidores) y el reparto para la extensión de Chrome. Cuando manda un
/// celular, el reparto lo hace <see cref="IgOutreachCoordinator"/> por /api/bridge/ig; las reglas
/// del plan (<see cref="IgOutreachPlan"/>) son las mismas para los dos caminos.
/// </summary>
[ApiController]
[Route("api/ig-dm")]
[Authorize]
public class IgDmController : ControllerBase
{
    private readonly ApplicationDbContext _db;
    private readonly IgOutreachCoordinator _coordinator;
    private readonly IgDeviceActivity _activity;
    private readonly ILogger<IgDmController> _log;

    public IgDmController(ApplicationDbContext db, IgOutreachCoordinator coordinator, IgDeviceActivity activity, ILogger<IgDmController> log)
    {
        _db = db; _coordinator = coordinator; _activity = activity; _log = log;
    }

    private static string Norm(string? u) => IgOutreachPlan.Norm(u);

    public record SettingsDto(
        string ProductKey, string DisplayName, bool Enabled, string? AccountUsername, string? DefaultAccount,
        int DailyCap, int MinGapMinutes, int MaxGapMinutes, int FromHour, int ToHour, string Template,
        DateTimeOffset? PausedUntil, string? PausedReason, int SentToday, int Pending, bool RepliesToConversations,
        // Plan: quién manda y la rampa.
        string Sender, Guid? DeviceId, string? ColdRamp, DateOnly? RampStartedOn, int Week, int CapToday,
        DateTimeOffset? ThrottledUntil,
        // Seguidores.
        bool FollowersEnabled, string? FollowerRamp, string FollowerTemplate, string? FollowerExclude,
        int FollowerCapToday, int FollowerSentToday, int FollowersPending, int FollowersTotal, DateTimeOffset? FollowerScanAt,
        // Qué le contestamos al celular la última vez que preguntó por Instagram.
        DateTimeOffset? DeviceAskedAt, string? DeviceStatus, string? DeviceAppVersion);

    [HttpGet("settings")]
    public async Task<ActionResult<List<SettingsDto>>> GetSettings(CancellationToken ct)
    {
        if (!CurrentUser.IsAdmin(User)) return Forbid();
        var now = DateTimeOffset.UtcNow;
        var dayStart = IgOutreachPlan.ArDayStart(now);
        var products = await _db.Products.AsNoTracking()
            .Where(p => p.Active && p.ProductKey != "").OrderBy(p => p.DisplayName).ToListAsync(ct);
        var all = await _db.IgDmSettings.AsNoTracking().ToDictionaryAsync(s => s.ProductKey, ct);
        var sent = await _db.Leads.AsNoTracking().Where(l => l.IgDmSentAt >= dayStart)
            .GroupBy(l => l.ProductKey).Select(g => new { g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.N, ct);
        var pending = await IgOutreachPlan.ColdQueue(_db.Leads.AsNoTracking())
            .GroupBy(l => l.ProductKey).Select(g => new { g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.N, ct);
        var followers = await _db.IgFollowers.AsNoTracking()
            .GroupBy(f => f.ProductKey)
            .Select(g => new
            {
                g.Key,
                Total = g.Count(),
                Pending = g.Count(f => f.DmSentAt == null && f.Error == null && f.SkippedReason == null),
                SentToday = g.Count(f => f.DmSentAt >= dayStart),
            })
            .ToDictionaryAsync(x => x.Key, ct);

        return products.Select(p =>
        {
            var s = all.GetValueOrDefault(p.ProductKey) ?? new IgDmSettings { ProductKey = p.ProductKey };
            var official = _coordinator.OfficialAccount(p.ProductKey);
            var f = followers.GetValueOrDefault(p.ProductKey);
            var followerSent = f?.SentToday ?? 0;
            var paused = s.PausedUntil > now;
            var device = s.Sender == IgDmSettings.SenderDevice ? _activity.Get(s.DeviceId) : null;
            return new SettingsDto(p.ProductKey, p.DisplayName, s.Enabled, s.AccountUsername, official?.Username,
                s.DailyCap, s.MinGapMinutes, s.MaxGapMinutes, s.FromHour, s.ToHour, s.Template,
                paused ? s.PausedUntil : null, paused ? s.PausedReason : null,
                // Todo DM a un seguidor también marca su lead: lo que queda es lo que salió en frío.
                Math.Max(0, sent.GetValueOrDefault(p.ProductKey) - followerSent), pending.GetValueOrDefault(p.ProductKey), official is not null,
                s.Sender, s.DeviceId, s.ColdRamp, s.RampStartedOn, IgOutreachPlan.WeekOf(s, now), IgOutreachPlan.ColdCapToday(s, now),
                IgOutreachPlan.IsThrottled(s, now) ? s.ThrottledUntil : null,
                s.FollowersEnabled, s.FollowerRamp, s.FollowerTemplate, s.FollowerExclude,
                IgOutreachPlan.FollowerCapToday(s, now), followerSent, f?.Pending ?? 0, f?.Total ?? 0, s.FollowerScanAt,
                device?.At, device?.Text, device?.AppVersion);
        }).ToList();
    }

    /// <summary>
    /// <paramref name="Sender"/> null = cliente viejo que no conoce el plan nuevo: se guarda solo lo
    /// de antes y no se toca quién manda, la rampa ni los seguidores.
    /// </summary>
    public record SaveSettingsRequest(
        bool Enabled, string? AccountUsername, int DailyCap, int MinGapMinutes, int MaxGapMinutes,
        int FromHour, int ToHour, string? Template, bool ClearPause = false,
        string? Sender = null, Guid? DeviceId = null, string? ColdRamp = null, bool RestartRamp = false,
        bool FollowersEnabled = false, string? FollowerRamp = null, string? FollowerTemplate = null, string? FollowerExclude = null);

    [HttpPut("settings/{productKey}")]
    public async Task<IActionResult> SaveSettings(string productKey, [FromBody] SaveSettingsRequest r, CancellationToken ct)
    {
        if (!CurrentUser.IsAdmin(User)) return Forbid();
        if (!await _db.Products.AnyAsync(p => p.ProductKey == productKey, ct)) return NotFound();
        if (r.DailyCap is < 0 or > 100) return BadRequest(new { error = "El tope diario va de 0 a 100" });
        if (r.MinGapMinutes < 1 || r.MaxGapMinutes < r.MinGapMinutes || r.MaxGapMinutes > 240)
            return BadRequest(new { error = "La pausa va de 1 a 240 minutos y la mínima no puede ser mayor que la máxima" });
        if (r.FromHour is < 0 or > 23 || r.ToHour is < 1 or > 24 || r.FromHour >= r.ToHour)
            return BadRequest(new { error = "Horario inválido: el 'desde' tiene que ser menor que el 'hasta'" });

        var now = DateTimeOffset.UtcNow;
        var s = await _db.IgDmSettings.FirstOrDefaultAsync(x => x.ProductKey == productKey, ct);
        if (s is null) { s = new IgDmSettings { ProductKey = productKey }; _db.IgDmSettings.Add(s); }
        var accountBefore = Norm(_coordinator.AccountOf(s));
        var deviceBefore = s.Sender == IgDmSettings.SenderDevice ? s.DeviceId : null;

        s.AccountUsername = string.IsNullOrWhiteSpace(r.AccountUsername) ? null : Norm(r.AccountUsername);
        s.DailyCap = r.DailyCap;
        s.MinGapMinutes = r.MinGapMinutes;
        s.MaxGapMinutes = r.MaxGapMinutes;
        s.FromHour = r.FromHour;
        s.ToHour = r.ToHour;
        s.Template = (r.Template ?? "").Trim();

        if (r.Sender is not null)
        {
            if (r.Sender is not (IgDmSettings.SenderExtension or IgDmSettings.SenderDevice))
                return BadRequest(new { error = "Quién manda tiene que ser la extensión o un celular" });
            if (!IgOutreachPlan.TryParseRamp(r.ColdRamp, 100, out var coldRamp, out var rampError))
                return BadRequest(new { error = rampError });
            if (!IgOutreachPlan.TryParseRamp(r.FollowerRamp, 150, out var followerRamp, out rampError))
                return BadRequest(new { error = rampError });

            var byDevice = r.Sender == IgDmSettings.SenderDevice;
            if (byDevice)
            {
                if (r.DeviceId is null || !await _db.Devices.AnyAsync(d => d.Id == r.DeviceId, ct))
                    return BadRequest(new { error = "Elegí el celular que manda" });
                // El Instagram del celular tiene una sola cuenta activa: un celular, una app.
                if (await _db.IgDmSettings.AnyAsync(x => x.ProductKey != productKey
                        && x.Sender == IgDmSettings.SenderDevice && x.DeviceId == r.DeviceId, ct))
                    return BadRequest(new { error = "Ese celular ya manda Instagram para otra app: va un celular por cuenta" });
            }
            var followerTemplate = (r.FollowerTemplate ?? "").Trim();
            if (r.FollowersEnabled)
            {
                if (!byDevice) return BadRequest(new { error = "Escribirles a los seguidores solo funciona mandando desde un celular" });
                if (followerTemplate.Length == 0) return BadRequest(new { error = "Falta el texto para los seguidores" });
                if (followerRamp.Length == 0) return BadRequest(new { error = "Falta cuántos seguidores por día (la rampa de seguidores)" });
            }

            s.Sender = r.Sender;
            s.DeviceId = byDevice ? r.DeviceId : null;
            s.ColdRamp = IgOutreachPlan.FormatRamp(coldRamp);
            s.FollowerRamp = IgOutreachPlan.FormatRamp(followerRamp);
            s.FollowersEnabled = r.FollowersEnabled;
            s.FollowerTemplate = followerTemplate;
            s.FollowerExclude = string.IsNullOrWhiteSpace(r.FollowerExclude) ? null : r.FollowerExclude.Trim();

            // La semana 1 arranca el día que se carga la rampa (o cuando piden volver a empezar).
            var hasRamp = coldRamp.Length > 0 || followerRamp.Length > 0;
            if (!hasRamp) s.RampStartedOn = null;
            else if (s.RampStartedOn is null || r.RestartRamp) s.RampStartedOn = IgOutreachPlan.ArToday(now);
        }

        var hasText = s.Template.Length > 0 || (s.FollowersEnabled && s.FollowerTemplate.Length > 0);
        if (r.Enabled && !hasText)
            return BadRequest(new { error = "Para prenderlo falta el texto del DM" });
        if (r.Enabled && string.IsNullOrWhiteSpace(_coordinator.AccountOf(s)))
            return BadRequest(new { error = "Para prenderlo falta la cuenta de Instagram que manda" });
        if (r.Enabled && s.Sender == IgDmSettings.SenderDevice && s.DeviceId is null)
            return BadRequest(new { error = "Para prenderlo falta elegir el celular que manda" });
        s.Enabled = r.Enabled;

        // Cambió la cuenta o el celular: hay que volver a confirmar que el Instagram del celular es el de la app.
        var deviceNow = s.Sender == IgDmSettings.SenderDevice ? s.DeviceId : null;
        if (accountBefore != Norm(_coordinator.AccountOf(s)) || deviceBefore != deviceNow) s.AccountVerifiedOn = null;

        if (r.ClearPause) { s.PausedUntil = null; s.PausedReason = null; s.ConsecutiveFailures = 0; }
        s.UpdatedAt = now;
        await _db.SaveChangesAsync(ct);
        return Ok();
    }

    // ── Extensión de Chrome ────────────────────────────────────────────────────────────────

    /// <summary>Respuesta de "next": un DM para mandar, o cuántos segundos esperar y por qué.</summary>
    public record NextDm(Guid? LeadId, string? Handle, string? Text, string? ProductKey, int WaitSeconds, string? Reason);

    /// <summary>
    /// Próximo DM para la cuenta de IG logueada en la extensión. El lead queda reservado 15 min
    /// (si la extensión se cae, vuelve a la cola).
    /// </summary>
    [HttpGet("next")]
    public async Task<ActionResult<NextDm>> Next([FromQuery] string account, CancellationToken ct)
    {
        var acct = Norm(account);
        if (acct.Length == 0) return BadRequest(new { error = "Falta la cuenta" });
        var now = DateTimeOffset.UtcNow;
        var local = IgOutreachPlan.ArLocal(now);
        var dayStart = IgOutreachPlan.ArDayStart(now);

        var settings = (await _db.IgDmSettings.Where(s => s.Enabled).ToListAsync(ct))
            .Where(s => Norm(_coordinator.AccountOf(s)) == acct).ToList();
        if (settings.Count == 0)
            return new NextDm(null, null, null, null, 600, $"@{acct} no tiene DMs prendidos en SalesHub");
        // Las apps que mandan desde un celular no le dan nada a la extensión: si no, saldrían dos DMs.
        settings = settings.Where(s => s.Sender != IgDmSettings.SenderDevice).ToList();
        if (settings.Count == 0)
            return new NextDm(null, null, null, null, 900, $"@{acct} manda desde el celular, no desde la extensión");

        NextDm? best = null;
        foreach (var s in settings)
        {
            NextDm Wait(DateTimeOffset until, string why) =>
                new(null, null, null, s.ProductKey, (int)Math.Clamp((until - now).TotalSeconds, 30, 6 * 3600), why);
            DateTimeOffset AtHour(int dayOffset, int hour) =>
                new DateTimeOffset(local.Date.AddDays(dayOffset).AddHours(hour), local.Offset);
            var cap = IgOutreachPlan.ColdCapToday(s, now);

            NextDm candidate;
            if (s.PausedUntil > now)
                candidate = Wait(s.PausedUntil.Value, $"Pausado: {s.PausedReason}");
            else if (local.Hour < s.FromHour)
                candidate = Wait(AtHour(0, s.FromHour), $"Fuera de horario (manda de {s.FromHour} a {s.ToHour} h)");
            else if (local.Hour >= s.ToHour)
                candidate = Wait(AtHour(1, s.FromHour), $"Fuera de horario (manda de {s.FromHour} a {s.ToHour} h)");
            else if (await _db.Leads.CountAsync(l => l.ProductKey == s.ProductKey && l.IgDmSentAt >= dayStart, ct) >= cap)
                candidate = Wait(AtHour(1, s.FromHour), $"Tope del día alcanzado ({cap})");
            else if (s.NextAllowedAt > now)
                candidate = Wait(s.NextAllowedAt.Value, "Pausa entre DMs");
            else
            {
                var claimCutoff = now.AddMinutes(-15);
                var lead = await IgOutreachPlan.ColdQueue(_db.Leads, s.ProductKey).Include(l => l.Product)
                    .Where(l => l.IgDmClaimedAt == null || l.IgDmClaimedAt < claimCutoff)
                    .OrderByDescending(l => l.Score).ThenByDescending(l => l.CreatedAt)
                    .FirstOrDefaultAsync(ct);
                if (lead is null)
                    candidate = Wait(now.AddMinutes(30), "No hay leads con Instagram sin contactar");
                else
                {
                    lead.IgDmClaimedAt = now;
                    await _db.SaveChangesAsync(ct);
                    var text = IgOutreachPlan.RenderCold(IgOutreachPlan.PickVariant(s.Template), lead, lead.Product);
                    return new NextDm(lead.Id, Norm(lead.InstagramHandle), text, s.ProductKey, 0, null);
                }
            }
            if (best is null || candidate.WaitSeconds < best.WaitSeconds) best = candidate;
        }
        return best!;
    }

    public record ResultRequest(bool Ok, string? Text, string? Error, bool Blocked = false);

    [HttpPost("{leadId:guid}/result")]
    public async Task<IActionResult> Result(Guid leadId, [FromBody] ResultRequest r, CancellationToken ct)
    {
        var lead = await _db.Leads.FirstOrDefaultAsync(l => l.Id == leadId, ct);
        if (lead is null) return NotFound();
        var s = await _db.IgDmSettings.FirstOrDefaultAsync(x => x.ProductKey == lead.ProductKey, ct);
        var now = DateTimeOffset.UtcNow;
        lead.IgDmClaimedAt = null;
        lead.UpdatedAt = now;

        if (r.Ok)
        {
            lead.IgDmSentAt = now;
            var text = (r.Text ?? "").Trim();
            _db.LeadNotes.Add(new LeadNote
            {
                Id = Guid.NewGuid(), LeadId = lead.Id, Kind = LeadNoteKind.System, CreatedAt = now,
                Text = $"DM en frío por Instagram a @{Norm(lead.InstagramHandle)}" + (text.Length > 0 ? $": {text}" : ""),
            });
            // Si la cuenta tiene webhook oficial, el DM queda en el chat: cuando conteste, la
            // respuesta cae en el mismo hilo (match por @usuario en ConversationService).
            var official = _coordinator.OfficialAccount(lead.ProductKey);
            if (official is not null && text.Length > 0)
                _db.ConversationMessages.Add(new ConversationMessage
                {
                    Id = Guid.NewGuid(), LeadId = lead.Id, SellerId = lead.SellerId,
                    Direction = MessageDirection.Outbound, Status = MessageDeliveryStatus.Sent,
                    Text = text, EvolutionInstance = InstagramMessengerClient.LineName(official.IgUserId),
                    Timestamp = now, IsRead = true,
                });
            if (s is not null) s.NextAllowedAt = now.AddSeconds(IgOutreachPlan.NextGapSeconds(s));
        }
        else if (r.Blocked)
        {
            // Aviso de bloqueo de Instagram: se frena la app hasta mañana a la hora de arranque y
            // vuelve una semana a mitad de tope. El lead vuelve a la cola (no fue culpa de él).
            if (s is not null) IgOutreachPlan.ApplyBlock(s, now, Trunc(r.Error, 300));
            _log.LogWarning("DMs de IG frenados para {Product}: {Reason}", lead.ProductKey, r.Error);
        }
        else
        {
            lead.IgDmError = Trunc(r.Error, 300) ?? "error";
            if (s is not null) s.NextAllowedAt = now.AddMinutes(1);
        }
        await _db.SaveChangesAsync(ct);
        return Ok();
    }

    // ── Celular ────────────────────────────────────────────────────────────────────────────

    public record TaskDto(Guid Id, string Kind, string Status, string? Handle, string? Text, string? Error,
        string? Screen, int? Collected, DateTimeOffset CreatedAt, DateTimeOffset? StartedAt, DateTimeOffset? FinishedAt);

    /// <summary>Lo último que se le pidió al celular para esta app (Movimientos).</summary>
    [HttpGet("tasks")]
    public async Task<ActionResult<List<TaskDto>>> Tasks([FromQuery] string productKey, [FromQuery] int take = 12, CancellationToken ct = default)
    {
        if (!CurrentUser.IsAdmin(User)) return Forbid();
        return await _db.IgDeviceTasks.AsNoTracking()
            .Where(t => t.ProductKey == productKey)
            .OrderByDescending(t => t.CreatedAt)
            .Take(Math.Clamp(take, 1, 100))
            .Select(t => new TaskDto(t.Id, t.Kind, t.Status, t.Handle, t.Text, t.Error, t.Screen, t.Collected, t.CreatedAt, t.StartedAt, t.FinishedAt))
            .ToListAsync(ct);
    }

    public record TestRequest(string? Handle, string? Text);

    /// <summary>
    /// DM de prueba: el celular de la app le escribe a este @usuario en su próxima consulta, con
    /// los mismos pasos que un DM real y salteando topes y horario. Para probar el circuito
    /// contra una cuenta propia antes de prenderlo.
    /// </summary>
    [HttpPost("{productKey}/test")]
    public async Task<IActionResult> Test(string productKey, [FromBody] TestRequest r, CancellationToken ct)
    {
        if (!CurrentUser.IsAdmin(User)) return Forbid();
        var (task, error) = await _coordinator.QueueTestAsync(productKey, r.Handle, r.Text, ct);
        return task is null ? BadRequest(new { error }) : Ok(new { id = task.Id, handle = task.Handle, text = task.Text });
    }

    /// <summary>Pide que el celular lea la lista de seguidores en su próxima consulta dentro del horario.</summary>
    [HttpPost("{productKey}/scan-followers")]
    public async Task<IActionResult> ScanFollowers(string productKey, CancellationToken ct)
    {
        if (!CurrentUser.IsAdmin(User)) return Forbid();
        var s = await _db.IgDmSettings.FirstOrDefaultAsync(x => x.ProductKey == productKey, ct);
        if (s is null || s.Sender != IgDmSettings.SenderDevice || s.DeviceId is null || !s.FollowersEnabled)
            return BadRequest(new { error = "Primero prendé los seguidores y elegí el celular que manda" });
        if (!s.Enabled)
            return BadRequest(new { error = "La app está apagada: el celular solo lee los seguidores con los DMs prendidos" });
        s.FollowerScanRequested = true;
        await _db.SaveChangesAsync(ct);
        return Ok();
    }

    private static string? Trunc(string? s, int max) =>
        string.IsNullOrWhiteSpace(s) ? null : s.Length <= max ? s : s[..max];
}
