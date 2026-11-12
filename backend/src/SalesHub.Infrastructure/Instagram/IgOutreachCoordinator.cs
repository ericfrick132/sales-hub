using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SalesHub.Core.Abstractions;
using SalesHub.Core.Domain.Entities;
using SalesHub.Core.Domain.Enums;
using SalesHub.Infrastructure.Persistence;

namespace SalesHub.Infrastructure.Instagram;

/// <summary>
/// Qué le contestó SalesHub a cada celular la última vez que preguntó por Instagram. Vive en
/// memoria (se pierde al reiniciar la API y no pasa nada): es lo que muestra la pantalla para
/// saber si el celular está preguntando y por qué no está mandando.
/// </summary>
public class IgDeviceActivity
{
    public record Entry(DateTimeOffset At, string Text, string? AppVersion);

    private readonly ConcurrentDictionary<Guid, Entry> _byDevice = new();

    public void Set(Guid deviceId, string text, string? appVersion) =>
        _byDevice[deviceId] = new Entry(DateTimeOffset.UtcNow, text, appVersion);

    public Entry? Get(Guid? deviceId) => deviceId is null ? null : _byDevice.GetValueOrDefault(deviceId.Value);
}

/// <summary>
/// El coordinador del outreach por Instagram desde un celular. El celular pregunta "¿qué hago?"
/// y acá se decide, con las reglas del plan de cada app: confirmar una vez por día que su
/// Instagram está en la cuenta correcta, leer la lista de seguidores, mandar un DM en frío o a
/// un seguidor (el que vaya más atrasado contra su tope del día) o esperar. La respuesta lleva
/// los pasos a ejecutar (<see cref="IgDeviceScripts"/>); cuando el celular reporta el resultado
/// se actualizan el lead, la pausa hasta el próximo y los frenos.
///
/// Nunca le da dos cosas a la vez a un celular, y WhatsApp va siempre primero: el celular solo
/// pregunta por Instagram cuando no tiene nada de WhatsApp para mandar.
/// </summary>
public class IgOutreachCoordinator
{
    /// <summary>Pasado este tiempo sin reporte, la tarea se da por perdida (y no se reintenta el DM).</summary>
    public const int TaskTimeoutMinutes = 12;
    /// <summary>Una prueba que el celular no tomó en este tiempo caduca: que no salga horas después.</summary>
    public const int TestExpiresMinutes = 30;
    /// <summary>A la tercera falla seguida del celular (no del destinatario) la app se frena.</summary>
    public const int MaxConsecutiveFailures = 3;

    public const string BlockedError = "blocked";
    public const string HumanError = "humano_en_el_celu";
    public const string NotConfirmedError = "send_not_confirmed";
    public const string NoReportError = "sin_reporte";

    private static readonly string[] WrongAccountErrors = { "cuenta_equivocada", "sin_sesion" };
    private static readonly string[] TargetErrors = { "perfil_no_existe", "no_acepta_mensajes", "es_la_cuenta_propia" };

    private readonly ApplicationDbContext _db;
    private readonly InstagramMessagingOptions _ig;
    private readonly ILeadAssigner _assigner;
    private readonly ILogger<IgOutreachCoordinator> _log;

    public IgOutreachCoordinator(ApplicationDbContext db, IOptions<InstagramMessagingOptions> ig,
        ILeadAssigner assigner, ILogger<IgOutreachCoordinator> log)
    {
        _db = db; _ig = ig.Value; _assigner = assigner; _log = log;
    }

    /// <summary>Lo que se le contesta al celular: una tarea con sus pasos, o cuánto esperar y por qué.</summary>
    public record Instruction(Guid? Id, string? Kind, string? Label, int TimeoutSeconds,
        List<Dictionary<string, object?>>? Steps, int WaitSeconds, string? Reason);

    /// <summary>Lo que reporta el celular al terminar una tarea.</summary>
    public record DeviceResult(Guid DeviceId, bool Ok, string? Error, List<string>? Collected, string? Screen);

    private static Instruction Wait(int seconds, string reason) => new(null, null, null, 0, null, seconds, reason);

    public InstagramMessagingOptions.Account? OfficialAccount(string productKey) =>
        _ig.Accounts.FirstOrDefault(a => a.ProductKey == productKey);

    public string? AccountOf(IgDmSettings s) =>
        string.IsNullOrWhiteSpace(s.AccountUsername) ? OfficialAccount(s.ProductKey)?.Username : s.AccountUsername;

    // ═══════════════════════════════════════════════════════════════════════════════════════
    //  ¿Qué hace el celular ahora?
    // ═══════════════════════════════════════════════════════════════════════════════════════

    public async Task<Instruction> NextAsync(Guid deviceId, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var apps = await _db.IgDmSettings
            .Where(s => s.Sender == IgDmSettings.SenderDevice && s.DeviceId == deviceId)
            .ToListAsync(ct);
        if (apps.Count == 0) return Wait(900, "Este celular no manda Instagram");

        // Una cosa por vez: con una tarea sin reporte no se le da otra.
        var running = await _db.IgDeviceTasks
            .Where(t => t.DeviceId == deviceId && t.Status == IgDeviceTask.StatusRunning)
            .ToListAsync(ct);
        var expired = false;
        foreach (var t in running)
        {
            if (t.StartedAt > now.AddMinutes(-TaskTimeoutMinutes))
                return Wait(60, "Esperando el resultado de la tarea anterior");
            await CloseAsync(t, apps.FirstOrDefault(a => a.ProductKey == t.ProductKey),
                new DeviceResult(deviceId, false, NoReportError, null, null), now, ct);
            expired = true;
        }
        if (expired) await _db.SaveChangesAsync(ct);

        // Prueba pedida a mano desde la pantalla: va primero y saltea topes, horario y pausas.
        var tests = await _db.IgDeviceTasks
            .Where(t => t.DeviceId == deviceId && t.Status == IgDeviceTask.StatusQueued && t.Kind == IgDeviceTask.KindTest)
            .OrderBy(t => t.CreatedAt)
            .ToListAsync(ct);
        foreach (var test in tests)
        {
            if (test.CreatedAt < now.AddMinutes(-TestExpiresMinutes) || !IgOutreachPlan.IsValidHandle(test.Handle) || string.IsNullOrWhiteSpace(test.Text))
            {
                test.Status = IgDeviceTask.StatusFailed;
                test.Error = $"La prueba caducó: el celular no la tomó en {TestExpiresMinutes} minutos";
                test.FinishedAt = now;
                continue;
            }
            return await StartAsync(test, IgDeviceScripts.Dm(test.Handle!, test.Text!), $"Instagram: prueba a @{test.Handle}", 300, now, ct);
        }
        if (tests.Count > 0) await _db.SaveChangesAsync(ct);

        Instruction? best = null;
        foreach (var s in apps.Where(a => a.Enabled))
        {
            var candidate = await NextForAppAsync(s, deviceId, now, ct);
            if (candidate.Id is not null) return candidate;
            if (best is null || candidate.WaitSeconds < best.WaitSeconds) best = candidate;
        }
        return best ?? Wait(600, "Los DMs por Instagram de este celular están apagados");
    }

    private async Task<Instruction> NextForAppAsync(IgDmSettings s, Guid deviceId, DateTimeOffset now, CancellationToken ct)
    {
        var account = IgOutreachPlan.Norm(AccountOf(s));
        if (!IgOutreachPlan.IsValidHandle(account))
            return Wait(900, "Falta la cuenta de Instagram que manda");

        var local = IgOutreachPlan.ArLocal(now);
        Instruction Until(DateTimeOffset until, string why) =>
            Wait((int)Math.Clamp((until - now).TotalSeconds, 30, 6 * 3600), why);
        DateTimeOffset AtHour(int dayOffset, int hour) =>
            new(local.Date.AddDays(dayOffset).AddHours(hour), local.Offset);
        var hours = $"Fuera de horario (manda de {s.FromHour} a {s.ToHour} h)";

        if (s.PausedUntil > now) return Until(s.PausedUntil.Value, $"Pausado: {s.PausedReason}");
        if (local.Hour < s.FromHour) return Until(AtHour(0, s.FromHour), hours);
        if (local.Hour >= s.ToHour) return Until(AtHour(1, s.FromHour), hours);
        // La pausa vale para todo lo que haga el celular en Instagram, no solo para los DMs: es
        // también la espera después de una falla o de que alguien estuviera usando el teléfono.
        if (s.NextAllowedAt > now) return Until(s.NextAllowedAt.Value, "Pausa entre DMs");

        var today = IgOutreachPlan.ArToday(now);
        var followerCap = string.IsNullOrWhiteSpace(s.FollowerTemplate) ? 0 : IgOutreachPlan.FollowerCapToday(s, now);

        // Seguidores: leer la lista. De paso confirma que el celular está en la cuenta correcta.
        if (s.FollowersEnabled && (followerCap > 0 || s.FollowerScanRequested))
        {
            var pending = await PendingFollowers(s.ProductKey).CountAsync(ct);
            if (NeedsFollowerScan(s, now, pending, followerCap))
            {
                var swipes = Math.Clamp(s.FollowerScanSwipes, 4, IgDmSettings.MaxFollowerScanSwipes);
                var scan = NewTask(s, deviceId, IgDeviceTask.KindFollowers, account, null);
                return await StartAsync(scan, IgDeviceScripts.Followers(account, swipes, swipes * 12),
                    $"Instagram: leyendo seguidores de @{account}", 180 + swipes * 8, now, ct);
            }
        }

        // Una vez por día, antes del primer DM: ¿el Instagram del celular está en la cuenta de la app?
        if (s.AccountVerifiedOn != today)
        {
            var verify = NewTask(s, deviceId, IgDeviceTask.KindVerify, account, null);
            return await StartAsync(verify, IgDeviceScripts.Verify(account), $"Instagram: confirmando la cuenta @{account}", 120, now, ct);
        }

        var dayStart = IgOutreachPlan.ArDayStart(now);
        var (coldSent, followerSent) = await SentTodayAsync(s.ProductKey, dayStart, ct);
        var coldCap = string.IsNullOrWhiteSpace(s.Template) ? 0 : IgOutreachPlan.ColdCapToday(s, now);
        var coldOpen = coldSent < coldCap;
        var followerOpen = followerSent < followerCap;
        if (!coldOpen && !followerOpen)
            return Until(AtHour(1, s.FromHour),
                $"Tope del día alcanzado ({coldSent} en frío" + (followerCap > 0 ? $", {followerSent} a seguidores" : "") + ")");

        // Se alternan solos: sale primero el que va más atrasado contra su tope del día.
        var kinds = new List<string>();
        if (coldOpen) kinds.Add(IgDeviceTask.KindCold);
        if (followerOpen) kinds.Add(IgDeviceTask.KindFollower);
        if (kinds.Count == 2 && (double)followerSent / followerCap < (double)coldSent / coldCap) kinds.Reverse();

        foreach (var kind in kinds)
        {
            var dm = kind == IgDeviceTask.KindCold
                ? await NextColdAsync(s, deviceId, now, ct)
                : await NextFollowerAsync(s, deviceId, now, ct);
            if (dm is not null) return dm;
        }
        return Wait(30 * 60, followerCap > 0
            ? "No hay leads con Instagram ni seguidores sin contactar"
            : "No hay leads con Instagram sin contactar");
    }

    /// <summary>DMs que ya salieron hoy: en frío y a seguidores (todo DM a un seguidor también marca su lead).</summary>
    public async Task<(int Cold, int Follower)> SentTodayAsync(string productKey, DateTimeOffset dayStart, CancellationToken ct)
    {
        var follower = await _db.IgFollowers.CountAsync(f => f.ProductKey == productKey && f.DmSentAt >= dayStart, ct);
        var all = await _db.Leads.CountAsync(l => l.ProductKey == productKey && l.IgDmSentAt >= dayStart, ct);
        return (Math.Max(0, all - follower), follower);
    }

    public IQueryable<IgFollower> PendingFollowers(string productKey) =>
        _db.IgFollowers.Where(f => f.ProductKey == productKey && f.DmSentAt == null && f.Error == null && f.SkippedReason == null);

    /// <summary>
    /// Cuándo leer la lista: si lo pidieron a mano, la primera vez, una vez por día para ver a los
    /// nuevos, o cada 4 horas si la cola de seguidores quedó vacía (cada lectura baja más pantallas).
    /// </summary>
    private static bool NeedsFollowerScan(IgDmSettings s, DateTimeOffset now, int pending, int capToday)
    {
        if (s.FollowerScanRequested) return true;
        if (capToday <= 0) return false;
        if (s.FollowerScanAt is null) return true;
        var age = now - s.FollowerScanAt.Value;
        if (age > TimeSpan.FromHours(20)) return true;
        return pending == 0 && age > TimeSpan.FromHours(4);
    }

    private async Task<Instruction?> NextColdAsync(IgDmSettings s, Guid deviceId, DateTimeOffset now, CancellationToken ct)
    {
        var claimCutoff = now.AddMinutes(-15);
        for (var i = 0; i < 5; i++)
        {
            var lead = await IgOutreachPlan.ColdQueue(_db.Leads, s.ProductKey).Include(l => l.Product)
                .Where(l => l.IgDmClaimedAt == null || l.IgDmClaimedAt < claimCutoff)
                .OrderByDescending(l => l.Score).ThenByDescending(l => l.CreatedAt)
                .FirstOrDefaultAsync(ct);
            if (lead is null) return null;

            var handle = IgOutreachPlan.Norm(lead.InstagramHandle);
            var text = IgOutreachPlan.FoldForDevice(
                IgOutreachPlan.RenderCold(IgOutreachPlan.PickVariant(s.Template), lead, lead.Product));
            if (!IgOutreachPlan.IsValidHandle(handle) || text.Length == 0)
            {
                lead.IgDmError = text.Length == 0 ? "el texto del DM quedó vacío" : "usuario de Instagram inválido";
                await _db.SaveChangesAsync(ct);
                continue;
            }

            lead.IgDmClaimedAt = now;
            var task = NewTask(s, deviceId, IgDeviceTask.KindCold, handle, text);
            task.LeadId = lead.Id;
            return await StartAsync(task, IgDeviceScripts.Dm(handle, text), $"Instagram: DM a @{handle}", 300, now, ct);
        }
        return null;
    }

    private async Task<Instruction?> NextFollowerAsync(IgDmSettings s, Guid deviceId, DateTimeOffset now, CancellationToken ct)
    {
        var appName = await _db.Products.AsNoTracking().Where(p => p.ProductKey == s.ProductKey)
            .Select(p => p.DisplayName).FirstOrDefaultAsync(ct) ?? s.ProductKey;
        var excluded = IgOutreachPlan.ParseHandles(s.FollowerExclude);
        var own = await OwnAccountsAsync(ct);
        var claimCutoff = now.AddMinutes(-15);

        for (var i = 0; i < 40; i++)
        {
            // Los más nuevos primero: los de la última lectura y, dentro de ella, los de más arriba.
            var f = await PendingFollowers(s.ProductKey)
                .Where(x => x.ClaimedAt == null || x.ClaimedAt < claimCutoff)
                .OrderByDescending(x => x.FirstSeenAt).ThenBy(x => x.Rank)
                .FirstOrDefaultAsync(ct);
            if (f is null) return null;

            var skip = excluded.Contains(f.Username) ? "excluido a mano"
                : own.Contains(f.Username) ? "es una cuenta propia"
                : !IgOutreachPlan.IsValidHandle(f.Username) ? "usuario inválido"
                : await FollowerSkipReasonAsync(f.Username, ct);
            if (skip is not null)
            {
                f.SkippedReason = skip;
                await _db.SaveChangesAsync(ct);
                continue;
            }

            var text = IgOutreachPlan.FoldForDevice(
                IgOutreachPlan.RenderFollower(IgOutreachPlan.PickVariant(s.FollowerTemplate), appName));
            if (text.Length == 0) return null;

            f.ClaimedAt = now;
            var task = NewTask(s, deviceId, IgDeviceTask.KindFollower, f.Username, text);
            task.FollowerId = f.Id;
            return await StartAsync(task, IgDeviceScripts.Dm(f.Username, text), $"Instagram: DM al seguidor @{f.Username}", 300, now, ct);
        }
        return null;
    }

    /// <summary>
    /// A quién NO se le escribe aunque nos siga: clientes, gente con la que ya hay una charla o a
    /// la que ya le salió un DM, y leads que alguien ya está trabajando. Se mira por @usuario en
    /// todas las apps: un seguidor que ya es lead de otra app tampoco recibe el mensaje.
    /// </summary>
    private async Task<string?> FollowerSkipReasonAsync(string username, CancellationToken ct)
    {
        var leads = await _db.Leads.AsNoTracking()
            .Where(l => l.InstagramHandle != null && l.InstagramHandle.ToLower().Replace("@", "") == username)
            .Select(l => new { l.Status, l.IgDmSentAt, l.FirstReplyAt, l.InstagramScopedId })
            .ToListAsync(ct);
        if (leads.Any(l => l.Status == LeadStatus.Closed)) return "ya es cliente";
        if (leads.Any(l => l.InstagramScopedId != null || l.FirstReplyAt != null)) return "ya tiene una charla con nosotros";
        if (leads.Any(l => l.IgDmSentAt != null)) return "ya recibió un DM";
        if (leads.Any(l => !IgOutreachPlan.NotContacted.Contains(l.Status))) return "ya está en gestión";
        return null;
    }

    private async Task<HashSet<string>> OwnAccountsAsync(CancellationToken ct)
    {
        var own = _ig.Accounts.Select(a => IgOutreachPlan.Norm(a.Username)).ToHashSet();
        foreach (var u in await _db.IgDmSettings.AsNoTracking().Select(x => x.AccountUsername).ToListAsync(ct))
            own.Add(IgOutreachPlan.Norm(u));
        own.Remove("");
        return own;
    }

    private static IgDeviceTask NewTask(IgDmSettings s, Guid deviceId, string kind, string handle, string? text) => new()
    {
        Id = Guid.NewGuid(), ProductKey = s.ProductKey, DeviceId = deviceId, Kind = kind, Handle = handle, Text = text,
    };

    private async Task<Instruction> StartAsync(IgDeviceTask task, List<Dictionary<string, object?>> steps,
        string label, int timeoutSeconds, DateTimeOffset now, CancellationToken ct)
    {
        task.Status = IgDeviceTask.StatusRunning;
        task.StartedAt = now;
        if (_db.Entry(task).State == EntityState.Detached) _db.IgDeviceTasks.Add(task);
        await _db.SaveChangesAsync(ct);
        return new Instruction(task.Id, task.Kind, label, timeoutSeconds, steps, 0, null);
    }

    // ═══════════════════════════════════════════════════════════════════════════════════════
    //  El celular reporta cómo terminó
    // ═══════════════════════════════════════════════════════════════════════════════════════

    /// <summary>false si la tarea no existe o es de otro celular.</summary>
    public async Task<bool> ReportAsync(Guid taskId, DeviceResult r, CancellationToken ct)
    {
        var task = await _db.IgDeviceTasks.FirstOrDefaultAsync(t => t.Id == taskId, ct);
        if (task is null || task.DeviceId != r.DeviceId) return false;
        if (task.Status != IgDeviceTask.StatusRunning) return true;   // ya cerrada: venció o reportó dos veces

        var s = await _db.IgDmSettings.FirstOrDefaultAsync(x => x.ProductKey == task.ProductKey, ct);
        await CloseAsync(task, s, r, DateTimeOffset.UtcNow, ct);
        await _db.SaveChangesAsync(ct);
        _log.LogInformation("Instagram por celular: {Kind} @{Handle} app={Product} ok={Ok} error={Error}",
            task.Kind, task.Handle, task.ProductKey, r.Ok, r.Error);
        return true;
    }

    private enum Outcome { Ok, Blocked, Human, WrongAccount, Target, Ambiguous, Infra }

    private static Outcome Classify(DeviceResult r)
    {
        if (r.Ok) return Outcome.Ok;
        var e = r.Error ?? "";
        if (e == BlockedError) return Outcome.Blocked;
        if (e.StartsWith(HumanError, StringComparison.Ordinal)) return Outcome.Human;
        if (WrongAccountErrors.Contains(e)) return Outcome.WrongAccount;
        if (TargetErrors.Contains(e)) return Outcome.Target;
        if (e is NotConfirmedError or NoReportError) return Outcome.Ambiguous;
        return Outcome.Infra;
    }

    private async Task CloseAsync(IgDeviceTask task, IgDmSettings? s, DeviceResult r, DateTimeOffset now, CancellationToken ct)
    {
        task.Status = r.Ok ? IgDeviceTask.StatusOk : IgDeviceTask.StatusFailed;
        task.FinishedAt = now;
        task.Error = r.Ok ? null : Trunc(r.Error, 300) ?? "error";
        task.Screen = r.Ok ? null : Trunc(r.Screen, 8000);
        task.Collected = r.Collected?.Count;

        // La prueba no mueve leads, topes ni pausas: solo queda en la bitácora.
        if (task.Kind == IgDeviceTask.KindTest || s is null) return;

        var outcome = Classify(r);
        var today = IgOutreachPlan.ArToday(now);
        switch (task.Kind)
        {
            case IgDeviceTask.KindVerify:
                if (r.Ok) { s.AccountVerifiedOn = today; s.ConsecutiveFailures = 0; }
                else ApplyFailure(s, outcome, task, now);
                break;

            case IgDeviceTask.KindFollowers:
                s.FollowerScanRequested = false;
                s.FollowerScanAt = now;   // también si falló: que no reintente en cada consulta
                if (r.Ok)
                {
                    s.AccountVerifiedOn = today;
                    s.ConsecutiveFailures = 0;
                    await SaveFollowersAsync(s, r.Collected ?? new List<string>(), now, ct);
                }
                else ApplyFailure(s, outcome, task, now);
                break;

            case IgDeviceTask.KindCold:
            {
                var lead = task.LeadId is null ? null : await _db.Leads.FirstOrDefaultAsync(l => l.Id == task.LeadId, ct);
                if (lead is not null) { lead.IgDmClaimedAt = null; lead.UpdatedAt = now; }
                if (r.Ok && lead is not null)
                {
                    lead.IgDmSentAt = now;
                    AddSentRecords(lead, task, now, "DM en frío por Instagram");
                    AfterSent(s, now);
                    break;
                }
                // Culpa del destinatario o envío sin confirmar: ese lead no se vuelve a intentar.
                if (lead is not null && outcome is Outcome.Target or Outcome.Ambiguous) lead.IgDmError = task.Error;
                ApplyFailure(s, outcome, task, now);
                break;
            }

            case IgDeviceTask.KindFollower:
            {
                var f = task.FollowerId is null ? null : await _db.IgFollowers.FirstOrDefaultAsync(x => x.Id == task.FollowerId, ct);
                if (f is not null) f.ClaimedAt = null;
                if (r.Ok && f is not null)
                {
                    var lead = await FollowerLeadAsync(s, f, now, ct);
                    lead.IgDmSentAt = now;
                    lead.IgDmClaimedAt = null;
                    lead.UpdatedAt = now;
                    f.DmSentAt = now;
                    f.LeadId = lead.Id;
                    AddSentRecords(lead, task, now, "DM a seguidor por Instagram");
                    AfterSent(s, now);
                    break;
                }
                if (f is not null && outcome is Outcome.Target or Outcome.Ambiguous) f.Error = task.Error;
                ApplyFailure(s, outcome, task, now);
                break;
            }
        }
        s.UpdatedAt = now;
    }

    private static void AfterSent(IgDmSettings s, DateTimeOffset now)
    {
        s.NextAllowedAt = now.AddSeconds(IgOutreachPlan.NextGapSeconds(s));
        s.ConsecutiveFailures = 0;
    }

    private void ApplyFailure(IgDmSettings s, Outcome outcome, IgDeviceTask task, DateTimeOffset now)
    {
        switch (outcome)
        {
            case Outcome.Blocked:
                IgOutreachPlan.ApplyBlock(s, now, "Instagram mostró un aviso de bloqueo en el celular");
                _log.LogWarning("DMs de IG frenados para {Product}: aviso de bloqueo en el celular", s.ProductKey);
                break;

            case Outcome.Human:
                // Alguien estaba usando el teléfono: no es una falla, se reintenta en un rato.
                s.NextAllowedAt = now.AddMinutes(Random.Shared.Next(3, 9));
                break;

            case Outcome.WrongAccount:
                s.AccountVerifiedOn = null;
                s.PausedUntil = IgOutreachPlan.TomorrowAt(now, s.FromHour);
                s.PausedReason = task.Error == "sin_sesion"
                    ? "El Instagram del celular está sin sesión iniciada"
                    : $"El Instagram del celular no está en @{IgOutreachPlan.Norm(AccountOf(s))}: abrilo y dejá esa cuenta activa";
                break;

            case Outcome.Target:
                s.ConsecutiveFailures = 0;   // el celular funcionó: el problema era ese perfil
                s.NextAllowedAt = now.AddMinutes(1);
                break;

            default:
                s.ConsecutiveFailures++;
                s.NextAllowedAt = now.AddMinutes(5);
                if (s.ConsecutiveFailures >= MaxConsecutiveFailures)
                {
                    s.PausedUntil = now.AddDays(30);
                    s.PausedReason = $"El celular falló {s.ConsecutiveFailures} veces seguidas ({task.Error}). Mirá Movimientos y reanudá";
                    _log.LogWarning("DMs de IG frenados para {Product}: {N} fallas seguidas del celular ({Error})",
                        s.ProductKey, s.ConsecutiveFailures, task.Error);
                }
                break;
        }
    }

    /// <summary>Mismo rastro que deja el envío por la extensión: nota en el lead y, si la cuenta tiene webhook oficial, el mensaje en el chat.</summary>
    private void AddSentRecords(Lead lead, IgDeviceTask task, DateTimeOffset now, string what)
    {
        var text = task.Text ?? "";
        _db.LeadNotes.Add(new LeadNote
        {
            Id = Guid.NewGuid(), LeadId = lead.Id, Kind = LeadNoteKind.System, CreatedAt = now,
            Text = $"{what} a @{task.Handle} (celular)" + (text.Length > 0 ? $": {text}" : ""),
        });
        // Con webhook oficial el DM queda en el chat: cuando conteste, la respuesta cae en el
        // mismo hilo (ConversationService engancha el IGSID por @usuario).
        var official = OfficialAccount(lead.ProductKey);
        if (official is null || text.Length == 0) return;
        _db.ConversationMessages.Add(new ConversationMessage
        {
            Id = Guid.NewGuid(), LeadId = lead.Id, SellerId = lead.SellerId,
            Direction = MessageDirection.Outbound, Status = MessageDeliveryStatus.Sent,
            Text = text, EvolutionInstance = InstagramMessengerClient.LineName(official.IgUserId),
            Timestamp = now, IsRead = true,
        });
    }

    /// <summary>
    /// El lead de un seguidor: si ya había uno sin contactar con ese @usuario en la app se usa
    /// ese (y así no le sale después el DM en frío); si no, se crea uno ya contactado a nombre
    /// de quien atiende la app.
    /// </summary>
    private async Task<Lead> FollowerLeadAsync(IgDmSettings s, IgFollower f, DateTimeOffset now, CancellationToken ct)
    {
        var existing = await IgOutreachPlan.ColdQueue(_db.Leads, s.ProductKey)
            .Where(l => l.InstagramHandle!.ToLower().Replace("@", "") == f.Username)
            .OrderByDescending(l => l.CreatedAt)
            .FirstOrDefaultAsync(ct);
        if (existing is not null) return existing;

        var owner = await _db.OnboardingConfigs.AsNoTracking()
                .Where(c => c.ProductKey == s.ProductKey && c.Enabled && c.HandoffSellerId != null
                    && _db.Sellers.Any(x => x.Id == c.HandoffSellerId && x.IsActive))
                .Select(c => c.HandoffSellerId).FirstOrDefaultAsync(ct)
            ?? await _assigner.PickOwnerAsync(s.ProductKey, ct);
        var lead = new Lead
        {
            Id = Guid.NewGuid(),
            ProductKey = s.ProductKey,
            Source = LeadSource.InstagramFollower,
            Name = "@" + f.Username,
            InstagramHandle = f.Username,
            SellerId = owner,
            AssignedAt = owner is null ? null : now,
            Status = LeadStatus.Sent,
            SentAt = now,
            CreatedAt = now,
            UpdatedAt = now,
        };
        _db.Leads.Add(lead);
        return lead;
    }

    /// <summary>
    /// Guarda lo que leyó el celular. Si casi no aparecieron seguidores nuevos, la próxima lectura
    /// baja más pantallas (hasta el techo); cuando vuelven a aparecer, vuelve al mínimo.
    /// </summary>
    private async Task SaveFollowersAsync(IgDmSettings s, List<string> collected, DateTimeOffset now, CancellationToken ct)
    {
        var names = collected.Select(IgOutreachPlan.Norm).Where(IgOutreachPlan.IsValidHandle).Distinct().ToList();
        var known = names.Count == 0
            ? new Dictionary<string, IgFollower>()
            : await _db.IgFollowers.Where(f => f.ProductKey == s.ProductKey && names.Contains(f.Username))
                .ToDictionaryAsync(f => f.Username, ct);
        var added = 0;
        for (var i = 0; i < names.Count; i++)
        {
            if (known.TryGetValue(names[i], out var f)) { f.LastSeenAt = now; f.Rank = i; continue; }
            _db.IgFollowers.Add(new IgFollower
            {
                Id = Guid.NewGuid(), ProductKey = s.ProductKey, Username = names[i],
                FirstSeenAt = now, LastSeenAt = now, Rank = i,
            });
            added++;
        }
        s.FollowerScanSwipes = added < 10
            ? Math.Min(IgDmSettings.MaxFollowerScanSwipes, s.FollowerScanSwipes + 12)
            : IgDmSettings.DefaultFollowerScanSwipes;
        _log.LogInformation("Seguidores de {Product}: {Seen} leídos, {Added} nuevos; próxima lectura baja {Swipes} pantallas",
            s.ProductKey, names.Count, added, s.FollowerScanSwipes);
    }

    // ═══════════════════════════════════════════════════════════════════════════════════════
    //  Pedidos desde la pantalla
    // ═══════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// DM de prueba a un @usuario elegido a mano: el celular lo manda en su próxima consulta con
    /// los mismos pasos que un DM real, sin tocar leads ni topes. Devuelve el error si no se puede.
    /// </summary>
    public async Task<(IgDeviceTask? Task, string? Error)> QueueTestAsync(string productKey, string? handle, string? text, CancellationToken ct)
    {
        var s = await _db.IgDmSettings.FirstOrDefaultAsync(x => x.ProductKey == productKey, ct);
        if (s is null || s.Sender != IgDmSettings.SenderDevice || s.DeviceId is null)
            return (null, "Primero elegí el celular que manda y guardá");
        var h = IgOutreachPlan.Norm(handle);
        if (!IgOutreachPlan.IsValidHandle(h)) return (null, "Ese usuario de Instagram no es válido");
        var t = IgOutreachPlan.FoldForDevice(text);
        if (t.Length == 0) return (null, "El texto de la prueba no puede estar vacío");
        if (await _db.IgDeviceTasks.AnyAsync(x => x.DeviceId == s.DeviceId && x.Kind == IgDeviceTask.KindTest
                && (x.Status == IgDeviceTask.StatusQueued || x.Status == IgDeviceTask.StatusRunning), ct))
            return (null, "Ya hay una prueba en curso en ese celular");

        var task = NewTask(s, s.DeviceId.Value, IgDeviceTask.KindTest, h, t);
        _db.IgDeviceTasks.Add(task);
        await _db.SaveChangesAsync(ct);
        return (task, null);
    }

    private static string? Trunc(string? s, int max) =>
        string.IsNullOrWhiteSpace(s) ? null : s.Length <= max ? s : s[..max];
}
