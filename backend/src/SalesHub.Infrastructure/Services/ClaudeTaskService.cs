using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SalesHub.Core.Abstractions;
using SalesHub.Core.Domain.Entities;
using SalesHub.Infrastructure.Persistence;

namespace SalesHub.Infrastructure.Services;

/// <summary>
/// Cola de tareas para Claude Code en la Mac (ver <see cref="ClaudeTask"/>): la arma el relay del webhook,
/// la toma el poller de la Mac (que de paso avisa que está prendida) y al cerrarla le manda el resultado
/// a Eric por WhatsApp. Toda respuesta arranca con <see cref="Prefix"/>.
/// </summary>
public class ClaudeTaskService
{
    public const string Prefix = "🤖 ";
    /// <summary>Cuánto espera en cola una tarea con la Mac apagada.</summary>
    public static readonly TimeSpan PendingTtl = TimeSpan.FromHours(12);
    /// <summary>Cuánto espera el "sí" antes de descartar la tarea sin avisar.</summary>
    public static readonly TimeSpan ConfirmTtl = TimeSpan.FromMinutes(30);
    /// <summary>Una tarea tomada sin resultado pasado esto se da por perdida (la Mac se apagó a mitad).</summary>
    public static readonly TimeSpan RunningTtl = TimeSpan.FromMinutes(45);
    /// <summary>La Mac consulta cada ~20 s: sin consultas en este lapso la damos por apagada.</summary>
    private static readonly TimeSpan OnlineWindow = TimeSpan.FromSeconds(90);
    private const int MaxReply = 3500;

    // Última consulta del poller. En memoria: tras un reinicio de la API vuelve en la próxima consulta.
    private static DateTimeOffset _lastPoll = DateTimeOffset.MinValue;
    public static bool MacOnline => DateTimeOffset.UtcNow - _lastPoll < OnlineWindow;

    private readonly ApplicationDbContext _db;
    private readonly IEvolutionClient _evo;
    private readonly ILogger<ClaudeTaskService> _log;

    public ClaudeTaskService(ApplicationDbContext db, IEvolutionClient evo, ILogger<ClaudeTaskService> log)
    {
        _db = db; _evo = evo; _log = log;
    }

    public record NextTask(Guid Id, string Prompt, string? ResumeSessionId);
    public record ResultIn(bool Ok, string? Result, string? SessionId, decimal? CostUsd, string? Error);

    public async Task<ClaudeTask> CreateAsync(string instance, string replyTo, string prompt, bool resume, CancellationToken ct)
    {
        string? resumeId = null;
        if (resume)
            resumeId = await _db.ClaudeTasks.AsNoTracking()
                .Where(t => t.Status == "done" && t.SessionId != null)
                .OrderByDescending(t => t.FinishedAt).Select(t => t.SessionId).FirstOrDefaultAsync(ct);

        // Un audio nuevo reemplaza al que esperaba confirmación (era una corrección).
        var waiting = await _db.ClaudeTasks.Where(t => t.ReplyTo == replyTo && t.Status == "confirm").ToListAsync(ct);
        foreach (var w in waiting) { w.Status = "cancelled"; w.FinishedAt = DateTimeOffset.UtcNow; w.Error = "reemplazada por otro audio"; }

        var task = new ClaudeTask { InstanceName = instance, ReplyTo = replyTo, Prompt = prompt, ResumeSessionId = resumeId, Status = "confirm" };
        _db.ClaudeTasks.Add(task);
        await _db.SaveChangesAsync(ct);

        var how = resume ? (resumeId is null ? "\n(No hay sesión anterior para seguir: arranca una nueva.)" : "\n(Sigue la sesión anterior.)") : "";
        await NotifyAsync(task, $"Le voy a pedir a Claude:\n«{prompt}»{how}\n\n¿Lo mando? Respondé sí o no, o mandá otro audio para corregirlo.", ct);
        return task;
    }

    /// <summary>
    /// Respuesta de texto a la confirmación. Devuelve false si no hay tarea esperando o el texto no es sí/no
    /// (el mensaje sigue la cadena normal del webhook).
    /// </summary>
    public async Task<bool> ConfirmAsync(string replyTo, bool yes, CancellationToken ct)
    {
        var since = DateTimeOffset.UtcNow - ConfirmTtl;
        var task = await _db.ClaudeTasks
            .Where(t => t.ReplyTo == replyTo && t.Status == "confirm" && t.CreatedAt >= since)
            .OrderByDescending(t => t.CreatedAt).FirstOrDefaultAsync(ct);
        if (task is null) return false;

        if (!yes)
        {
            task.Status = "cancelled";
            task.FinishedAt = DateTimeOffset.UtcNow;
            task.Error = "cancelada por Eric";
            await _db.SaveChangesAsync(ct);
            await NotifyAsync(task, "Listo, no la mando.", ct);
            return true;
        }

        // El vencimiento de la cola cuenta desde la confirmación.
        task.Status = "pending";
        task.CreatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);
        var where = MacOnline
            ? "Mandada, la Mac la toma ahora."
            : $"💤 Mandada, pero la Mac está apagada: la corro cuando se prenda (si no, vence en {PendingTtl.TotalHours:0} h).";
        await NotifyAsync(task, where, ct);
        return true;
    }

    /// <summary>Consulta del poller: marca la Mac como prendida y le da la tarea más vieja en cola.</summary>
    public async Task<NextTask?> ClaimNextAsync(CancellationToken ct)
    {
        _lastPoll = DateTimeOffset.UtcNow;
        await SweepAsync(ct);

        var task = await _db.ClaudeTasks.Where(t => t.Status == "pending").OrderBy(t => t.CreatedAt).FirstOrDefaultAsync(ct);
        if (task is null) return null;
        task.Status = "running";
        task.ClaimedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);
        return new NextTask(task.Id, task.Prompt, task.ResumeSessionId);
    }

    public async Task<bool> CompleteAsync(Guid id, ResultIn r, CancellationToken ct)
    {
        var task = await _db.ClaudeTasks.FirstOrDefaultAsync(t => t.Id == id, ct);
        if (task is null || task.Status != "running") return false;
        task.Status = r.Ok ? "done" : "failed";
        task.FinishedAt = DateTimeOffset.UtcNow;
        task.SessionId = Cut(r.SessionId, 64);
        task.Result = r.Result;
        task.CostUsd = r.CostUsd;
        task.Error = Cut(r.Error, 1000);
        await _db.SaveChangesAsync(ct);

        var cost = r.CostUsd is { } c ? $" (USD {c:0.00})" : "";
        var body = r.Ok
            ? $"Listo{cost}:\n\n{Cut(r.Result, MaxReply)}"
            : $"Falló la tarea «{Cut(task.Prompt, 120)}»{cost}:\n{Cut(r.Error ?? r.Result, 800)}";
        await NotifyAsync(task, body, ct);
        return true;
    }

    /// <summary>Vence las tareas en cola de más de 12 h y las tomadas que nunca volvieron.</summary>
    public async Task SweepAsync(CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var pendingCut = now - PendingTtl;
        var runningCut = now - RunningTtl;

        // Sin confirmar en 30 min: se descartan en silencio (ya se le preguntó).
        var confirmCut = now - ConfirmTtl;
        var unconfirmed = await _db.ClaudeTasks.Where(t => t.Status == "confirm" && t.CreatedAt < confirmCut).ToListAsync(ct);
        foreach (var t in unconfirmed) { t.Status = "cancelled"; t.FinishedAt = now; t.Error = "sin confirmar en 30 min"; }
        if (unconfirmed.Count > 0) await _db.SaveChangesAsync(ct);

        var stale = await _db.ClaudeTasks
            .Where(t => (t.Status == "pending" && t.CreatedAt < pendingCut) || (t.Status == "running" && t.ClaimedAt < runningCut))
            .ToListAsync(ct);
        if (stale.Count == 0) return;

        foreach (var t in stale)
        {
            var wasPending = t.Status == "pending";
            t.Status = wasPending ? "expired" : "failed";
            t.FinishedAt = now;
            t.Error = wasPending ? "la Mac no se prendió en 12 h" : "la Mac no devolvió resultado en 45 min";
        }
        await _db.SaveChangesAsync(ct);
        foreach (var t in stale)
            await NotifyAsync(t, $"Descarté la tarea «{Cut(t.Prompt, 120)}»: {t.Error}. Mandala de nuevo si sigue en pie.", ct);
    }

    private async Task NotifyAsync(ClaudeTask task, string text, CancellationToken ct)
    {
        var ok = await _evo.SendTextAsync(task.InstanceName, task.ReplyTo, Prefix + text, ct);
        if (!ok) _log.LogWarning("ClaudeTask {Id}: no se pudo mandar el aviso por WhatsApp", task.Id);
    }

    private static string? Cut(string? s, int max) =>
        s is null ? null : s.Length <= max ? s : s[..max] + "…";
}
