using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SalesHub.Core.Abstractions;
using SalesHub.Core.Domain.Entities;
using SalesHub.Infrastructure.Persistence;

namespace SalesHub.Infrastructure.Services;

/// <summary>
/// Cola de tareas para Claude Code en la Mac (ver <see cref="ClaudeTask"/>): la arma el relay del webhook,
/// la toma el poller de la Mac (que de paso avisa que está prendida) y al cerrarla le manda el resultado
/// a Eric por WhatsApp. Toda respuesta arranca con <see cref="Prefix"/> y el código de la tarea (#a3f9c1):
/// citar cualquiera de esos mensajes (o el audio original) sigue el thread de esa tarea.
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

    public record NextTask(Guid Id, string Prompt, string? ResumeSessionId, int Attachments);
    public record ResultIn(bool Ok, string? Result, string? SessionId, decimal? CostUsd, string? Error);

    /// <summary>Tarea citada: por el código (#a3f9c1) del mensaje citado o por el id del audio citado.</summary>
    public Task<ClaudeTask?> FindQuotedAsync(string replyTo, string? code, string? quotedMessageId, CancellationToken ct)
    {
        if (code is null && quotedMessageId is null) return Task.FromResult<ClaudeTask?>(null);
        return _db.ClaudeTasks
            .Where(t => t.ReplyTo == replyTo && ((code != null && t.Code == code) || (quotedMessageId != null && t.AudioMessageId == quotedMessageId)))
            .OrderByDescending(t => t.CreatedAt).FirstOrDefaultAsync(ct);
    }

    /// <summary>
    /// Tarea nueva desde un audio (queda esperando el "sí") o desde un texto escrito citando una tarea
    /// (<paramref name="confirmed"/>: va directo a la cola). <paramref name="parent"/> = thread a seguir;
    /// <paramref name="resumeLast"/> = "seguí" sin citar → la última sesión terminada.
    /// </summary>
    public async Task<ClaudeTask> CreateAsync(string instance, string replyTo, string prompt, string? audioMessageId,
        ClaudeTask? parent, bool resumeLast, bool confirmed, CancellationToken ct)
    {
        Guid? parentId = parent?.Id;
        if (parent is null && resumeLast)
            parentId = await _db.ClaudeTasks.AsNoTracking()
                .Where(t => t.ReplyTo == replyTo && t.Status == "done" && t.SessionId != null)
                .OrderByDescending(t => t.FinishedAt).Select(t => (Guid?)t.Id).FirstOrDefaultAsync(ct);

        // Un audio nuevo reemplaza al que esperaba confirmación (era una corrección); si cita una tarea que
        // todavía esperaba el sí, hereda su thread.
        var waiting = await _db.ClaudeTasks.Where(t => t.ReplyTo == replyTo && t.Status == "confirm").ToListAsync(ct);
        foreach (var w in waiting) { w.Status = "cancelled"; w.FinishedAt = DateTimeOffset.UtcNow; w.Error = "reemplazada por otro audio"; }
        if (parent is { Status: "confirm" }) parentId = parent.ParentTaskId;

        var task = new ClaudeTask
        {
            InstanceName = instance, ReplyTo = replyTo, Prompt = prompt, AudioMessageId = audioMessageId,
            ParentTaskId = parentId, Code = NewCode(), Status = confirmed ? "pending" : "confirm",
        };
        _db.ClaudeTasks.Add(task);
        await _db.SaveChangesAsync(ct);

        var thread = parentId is null
            ? "Sesión nueva."
            : $"Sigue el thread de #{await _db.ClaudeTasks.Where(t => t.Id == parentId).Select(t => t.Code).FirstOrDefaultAsync(ct)}.";
        if (confirmed)
            await NotifyAsync(task, $"Mandada: «{Cut(prompt, 300)}»\n{thread} {Where()}", ct);
        else
            await NotifyAsync(task, $"Le voy a pedir a Claude:\n«{prompt}»\n({thread})\n\n¿Lo mando? Respondé sí o no, o mandá otro audio para corregirlo. Si mandás imágenes antes del sí, se las paso.", ct);
        return task;
    }

    /// <summary>
    /// Respuesta sí/no a la confirmación: a la tarea citada, o si no cita nada a la última esperando.
    /// Devuelve false si no hay tarea esperando (el mensaje sigue la cadena normal del webhook).
    /// </summary>
    public async Task<bool> ConfirmAsync(string replyTo, bool yes, ClaudeTask? quoted, CancellationToken ct)
    {
        var since = DateTimeOffset.UtcNow - ConfirmTtl;
        var task = quoted is not null
            ? await _db.ClaudeTasks.FirstOrDefaultAsync(t => t.Id == quoted.Id && t.Status == "confirm" && t.CreatedAt >= since, ct)
            : await _db.ClaudeTasks
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
        await NotifyAsync(task, "Mandada. " + Where(), ct);
        return true;
    }

    public const int MaxAttachments = 10;
    private const int MaxAttachmentBytes = 8 * 1024 * 1024;

    /// <summary>Tarea esperando el "sí" a la que se adjunta una imagen: la citada o la última.</summary>
    public Task<ClaudeTask?> FindWaitingAsync(string replyTo, ClaudeTask? quoted, CancellationToken ct)
    {
        var since = DateTimeOffset.UtcNow - ConfirmTtl;
        var q = _db.ClaudeTasks.Where(t => t.ReplyTo == replyTo && t.Status == "confirm" && t.CreatedAt >= since);
        if (quoted is not null) q = q.Where(t => t.Id == quoted.Id);
        return q.OrderByDescending(t => t.CreatedAt).FirstOrDefaultAsync(ct);
    }

    public async Task AttachAsync(ClaudeTask task, string mime, byte[] data, CancellationToken ct)
    {
        var count = await _db.ClaudeTaskAttachments.CountAsync(a => a.TaskId == task.Id, ct);
        if (count >= MaxAttachments || data.Length > MaxAttachmentBytes)
        {
            await NotifyAsync(task, count >= MaxAttachments
                ? $"Ya tiene {MaxAttachments} imágenes, esta no la adjunto."
                : "Esa imagen pesa más de 8 MB, no la adjunto.", ct);
            return;
        }
        _db.ClaudeTaskAttachments.Add(new ClaudeTaskAttachment { TaskId = task.Id, Index = count + 1, MimeType = mime, Data = data });
        // Extiende la ventana de confirmación: está armando el pedido.
        task.CreatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);
        await NotifyAsync(task, $"📎 Imagen {count + 1} adjuntada. ¿Lo mando? sí o no.", ct);
    }

    /// <summary>Imagen para el poller: solo de una tarea que la Mac ya tomó.</summary>
    public Task<ClaudeTaskAttachment?> GetAttachmentAsync(Guid taskId, int index, CancellationToken ct) =>
        _db.ClaudeTaskAttachments.AsNoTracking()
            .Where(a => a.TaskId == taskId && a.Index == index
                && _db.ClaudeTasks.Any(t => t.Id == taskId && t.Status == "running"))
            .FirstOrDefaultAsync(ct);

    private static string Where() => MacOnline
        ? "La Mac la toma ahora."
        : $"💤 La Mac está apagada: la corro cuando se prenda (si no, vence en {PendingTtl.TotalHours:0} h).";

    /// <summary>
    /// Consulta del poller: marca la Mac como prendida y le da la tarea más vieja en cola. Una tarea que sigue
    /// un thread espera a que la tarea padre termine, y retoma su sesión de Claude Code.
    /// </summary>
    public async Task<NextTask?> ClaimNextAsync(CancellationToken ct)
    {
        _lastPoll = DateTimeOffset.UtcNow;
        await SweepAsync(ct);

        var queue = await _db.ClaudeTasks.Where(t => t.Status == "pending").OrderBy(t => t.CreatedAt).Take(20).ToListAsync(ct);
        foreach (var task in queue)
        {
            if (task.ParentTaskId is { } pid && task.ResumeSessionId is null)
            {
                var parent = await _db.ClaudeTasks.AsNoTracking().Where(t => t.Id == pid)
                    .Select(t => new { t.Status, t.SessionId }).FirstOrDefaultAsync(ct);
                if (parent is { Status: "pending" or "running" }) continue; // el padre todavía no tiene sesión
                task.ResumeSessionId = parent?.SessionId;
            }
            task.Status = "running";
            task.ClaimedAt = DateTimeOffset.UtcNow;
            await _db.SaveChangesAsync(ct);
            var images = await _db.ClaudeTaskAttachments.CountAsync(a => a.TaskId == task.Id, ct);
            return new NextTask(task.Id, task.Prompt, task.ResumeSessionId, images);
        }
        return null;
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
        await NotifyAsync(task, body + "\n\n(Citá este mensaje para seguir este thread.)", ct);
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
        var ok = await _evo.SendTextAsync(task.InstanceName, task.ReplyTo, $"{Prefix}#{task.Code} {text}", ct);
        if (!ok) _log.LogWarning("ClaudeTask {Id}: no se pudo mandar el aviso por WhatsApp", task.Id);
    }

    private static string NewCode() => Guid.NewGuid().ToString("N")[..6];

    private static string? Cut(string? s, int max) =>
        s is null ? null : s.Length <= max ? s : s[..max] + "…";
}
