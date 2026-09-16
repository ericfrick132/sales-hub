using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SalesHub.Core.Domain.Entities;
using SalesHub.Core.Domain.Entities.Social;
using SalesHub.Core.Domain.Enums;
using SalesHub.Infrastructure.Evolution;
using SalesHub.Infrastructure.Persistence;

namespace SalesHub.Infrastructure.Services;

/// <summary>
/// Cuántos leads entraron a cada teléfono, por día. Sale de los mensajes de WhatsApp que
/// escucha cada línea: un lead "entró" a una línea el día (hora Argentina) de su PRIMER
/// mensaje entrante por esa línea. Un lead que ya había escrito antes no vuelve a contar; uno
/// que escribe a dos teléfonos distintos cuenta en cada uno.
/// Lo usan el gráfico de /entradas, los contadores de /devices y el reporte diario por WhatsApp.
/// </summary>
public class LeadEntryService
{
    private readonly ApplicationDbContext _db;
    private readonly EvolutionClient _evo;
    private readonly ILogger<LeadEntryService> _log;

    public LeadEntryService(ApplicationDbContext db, EvolutionClient evo, ILogger<LeadEntryService> log)
    {
        _db = db; _evo = evo; _log = log;
    }

    public const string ChartUrl = "https://sales.efcloud.tech/entradas";

    public record Entry(
        string Line, Guid LeadId, DateTimeOffset FirstAt, DateOnly Day,
        LeadStatus Status, string ProductKey, LeadSource Source, Guid? SellerId,
        string Name, string? Phone);

    public record LineInfo(
        string InstanceName, string Label, string? Phone, InstanceStatus? Status,
        bool ListenOnly, Guid? SellerId, bool Registered, DateTimeOffset? DisconnectedAt,
        DateTimeOffset? CreatedAt);

    public static readonly TimeZoneInfo ArTz = ResolveArTz();

    private static TimeZoneInfo ResolveArTz()
    {
        foreach (var id in new[] { "America/Argentina/Buenos_Aires", "Argentina Standard Time" })
        {
            try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
            catch { /* siguiente */ }
        }
        return TimeZoneInfo.CreateCustomTimeZone("AR", TimeSpan.FromHours(-3), "AR", "AR");
    }

    public static DateOnly ArDay(DateTimeOffset t) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(t, ArTz).DateTime);

    public static DateOnly TodayAr() => ArDay(DateTimeOffset.UtcNow);

    /// <summary>00:00 de ese día en Argentina, expresado en UTC.</summary>
    public static DateTimeOffset ArDayStartUtc(DateOnly day)
    {
        var local = day.ToDateTime(TimeOnly.MinValue);
        return new DateTimeOffset(local, ArTz.GetUtcOffset(local)).ToUniversalTime();
    }

    /// <summary>Entradas cuyo primer mensaje cae entre esos días de Argentina (ambos inclusive).</summary>
    public async Task<List<Entry>> GetEntriesAsync(DateOnly fromDay, DateOnly toDay, CancellationToken ct)
    {
        var fromUtc = ArDayStartUtc(fromDay);
        var toUtc = ArDayStartUtc(toDay.AddDays(1));

        // El primer entrante de cada (línea, lead) sale de TODO el historial, no sólo del rango:
        // si no, alguien que escribió el mes pasado contaría de nuevo como entrada de hoy.
        var firsts = _db.ConversationMessages.AsNoTracking()
            .Where(m => m.Direction == MessageDirection.Inbound && m.EvolutionInstance != null)
            .GroupBy(m => new { m.EvolutionInstance, m.LeadId })
            .Select(g => new { Line = g.Key.EvolutionInstance, g.Key.LeadId, FirstAt = g.Min(m => m.Timestamp) })
            .Where(f => f.FirstAt >= fromUtc && f.FirstAt < toUtc);

        var rows = await firsts
            .Join(_db.Leads.AsNoTracking(), f => f.LeadId, l => l.Id, (f, l) => new
            {
                f.Line, f.LeadId, f.FirstAt,
                l.Status, l.ProductKey, l.Source, l.SellerId, l.Name, l.WhatsappPhone
            })
            .ToListAsync(ct);

        return rows
            .Select(r => new Entry(r.Line!, r.LeadId, r.FirstAt, ArDay(r.FirstAt),
                r.Status, r.ProductKey, r.Source, r.SellerId, r.Name, r.WhatsappPhone))
            .OrderBy(e => e.FirstAt)
            .ToList();
    }

    /// <summary>
    /// Todas las líneas registradas + las que aparecen en mensajes viejos pero ya no existen
    /// (se borraron), para que el historial no quede con nombres huérfanos.
    /// </summary>
    public async Task<List<LineInfo>> GetLinesAsync(IEnumerable<string> seenInstanceNames, CancellationToken ct)
    {
        var registered = await _db.EvolutionInstances.AsNoTracking()
            .Include(i => i.Seller)
            .OrderBy(i => i.CreatedAt)
            .ToListAsync(ct);

        var lines = registered.Select(i => new LineInfo(
            i.InstanceName, DisplayName(i), i.ConnectedPhoneNumber, i.Status, i.ListenOnly,
            i.SellerId, true, i.DisconnectedAt, i.CreatedAt)).ToList();

        var known = new HashSet<string>(lines.Select(l => l.InstanceName), StringComparer.OrdinalIgnoreCase);
        foreach (var name in seenInstanceNames.Distinct(StringComparer.OrdinalIgnoreCase).Where(n => !known.Contains(n)))
            lines.Add(new LineInfo(name, $"{name} (borrada)", null, null, false, null, false, null, null));
        return lines;
    }

    /// <summary>Cómo se nombra una línea en pantalla y en el reporte.</summary>
    public static string DisplayName(EvolutionInstance i)
    {
        if (!string.IsNullOrWhiteSpace(i.Label)) return i.Label!.Trim();
        if (i.Seller is not null && !string.IsNullOrWhiteSpace(i.Seller.DisplayName)) return $"Línea de {i.Seller.DisplayName}";
        if (!string.IsNullOrWhiteSpace(i.ProductKey)) return $"Línea {i.ProductKey}";
        return i.InstanceName;
    }

    // ── Reporte diario ──────────────────────────────────────────────────────

    public async Task<LeadEntryReportSettings> GetSettingsAsync(CancellationToken ct)
    {
        var s = await _db.LeadEntryReportSettings.FirstOrDefaultAsync(ct);
        if (s is not null) return s;
        s = new LeadEntryReportSettings();
        _db.LeadEntryReportSettings.Add(s);
        await _db.SaveChangesAsync(ct);
        return s;
    }

    private static readonly string[] DayNames = { "dom", "lun", "mar", "mié", "jue", "vie", "sáb" };

    private static string StatusLabel(LeadStatus s) => s switch
    {
        LeadStatus.New => "Nuevo",
        LeadStatus.Assigned => "Asignado",
        LeadStatus.Queued => "En cola",
        LeadStatus.Sent => "Contactado",
        LeadStatus.Replied => "Respondió",
        LeadStatus.Interested => "Interesado",
        LeadStatus.DemoScheduled => "Demo agendada",
        LeadStatus.Closed => "Cerrado",
        LeadStatus.Lost => "Perdido",
        LeadStatus.Blocked => "Bloqueado",
        LeadStatus.NoWhatsApp => "WhatsApp inexistente",
        _ => s.ToString()
    };

    /// <summary>Texto del reporte de un día (normalmente ayer).</summary>
    public async Task<string> BuildReportAsync(DateOnly day, CancellationToken ct)
    {
        var entries = await GetEntriesAsync(day.AddDays(-7), day, ct);
        var lines = await GetLinesAsync(entries.Select(e => e.Line), ct);
        var byName = lines.ToDictionary(l => l.InstanceName, StringComparer.OrdinalIgnoreCase);

        var ofDay = entries.Where(e => e.Day == day).ToList();
        var prev7 = entries.Where(e => e.Day < day).ToList();
        var last7 = entries.Where(e => e.Day > day.AddDays(-7)).ToList();

        string Name(string line) => byName.TryGetValue(line, out var l) ? l.Label : line;

        var sb = new StringBuilder();
        var dayText = $"{DayNames[(int)day.DayOfWeek]} {day:dd/MM}";
        sb.AppendLine(ofDay.Count == 0
            ? $"*Leads por teléfono — {dayText}: no entró ninguno*"
            : $"*Leads que entraron el {dayText}: {ofDay.Count}*");
        sb.AppendLine($"Promedio de los 7 días anteriores: {Math.Round(prev7.Count / 7.0, 1):0.#} por día");

        // Por teléfono: los que tuvieron entradas + los conectados aunque estén en cero (un
        // teléfono conectado en cero todo el día es justo lo que hay que ver).
        var counts = ofDay.GroupBy(e => e.Line, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
        var shown = lines
            .Where(l => counts.ContainsKey(l.InstanceName) || l.Status == InstanceStatus.Connected)
            .OrderByDescending(l => counts.GetValueOrDefault(l.InstanceName))
            .ThenBy(l => l.Label)
            .ToList();
        sb.AppendLine();
        sb.AppendLine("*Por teléfono*");
        if (shown.Count == 0) sb.AppendLine("Ningún teléfono conectado.");
        foreach (var l in shown)
        {
            var phone = string.IsNullOrWhiteSpace(l.Phone) ? "" : $" (+{l.Phone})";
            var off = l.Registered && l.Status != InstanceStatus.Connected ? " — desconectado" : "";
            sb.AppendLine($"{l.Label}{phone}: {counts.GetValueOrDefault(l.InstanceName)}{off}");
        }

        if (ofDay.Count > 0)
        {
            var statuses = ofDay.GroupBy(e => e.Status)
                .OrderByDescending(g => g.Count())
                .Select(g => $"{StatusLabel(g.Key)} {g.Count()}");
            sb.AppendLine();
            sb.AppendLine("*En qué estado están ahora*");
            sb.AppendLine(string.Join(", ", statuses));
        }

        sb.AppendLine();
        sb.AppendLine($"*Últimos 7 días: {last7.Count}*");
        if (last7.Count > 0)
            sb.AppendLine(string.Join(", ", last7.GroupBy(e => e.Line, StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(g => g.Count())
                .Select(g => $"{Name(g.Key)} {g.Count()}")));

        // Teléfonos propios (sin vendedor) que se cayeron: si no, un celu muerto se ve igual
        // que un celu al que no le escribió nadie.
        var down = lines
            .Where(l => l.Registered && l.SellerId is null && l.Status != InstanceStatus.Connected)
            .Select(l => l.DisconnectedAt is { } d ? $"{l.Label} (desde {ArDay(d):dd/MM})" : l.Label)
            .ToList();
        if (down.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"Sin conexión: {string.Join(", ", down)}");
        }

        sb.AppendLine();
        sb.Append($"Gráfico: {ChartUrl}");
        return sb.ToString();
    }

    public record SendResult(bool Ok, string? Via, string? Error, string Text);

    /// <summary>
    /// Arma y manda el reporte del día. Prueba las líneas en orden hasta que una confirme:
    /// la elegida en la config, o las conectadas (primero las que pueden enviar), y por último
    /// la línea de avisos al maestro. No toca la fila de config: eso lo decide quien llama.
    /// </summary>
    public async Task<SendResult> SendReportAsync(DateOnly day, CancellationToken ct)
    {
        var settings = await GetSettingsAsync(ct);
        var text = await BuildReportAsync(day, ct);
        var to = new string((settings.RecipientPhone ?? "").Where(char.IsDigit).ToArray());
        if (to.Length < 8) return new SendResult(false, null, "Falta el número al que se manda el reporte.", text);

        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(settings.SendInstanceName))
        {
            candidates.Add(settings.SendInstanceName!);
        }
        else
        {
            candidates.AddRange(await _db.EvolutionInstances.AsNoTracking()
                .Where(i => i.Status == InstanceStatus.Connected)
                .OrderBy(i => i.ListenOnly).ThenBy(i => i.CreatedAt)
                .Select(i => i.InstanceName)
                .ToListAsync(ct));
            var master = await _db.Set<InspirationSettings>().AsNoTracking()
                .Select(s => s.InstanceName).FirstOrDefaultAsync(ct);
            if (!string.IsNullOrWhiteSpace(master)) candidates.Add(master!);
        }
        if (candidates.Count == 0)
            return new SendResult(false, null, "No hay ninguna línea conectada para mandar el reporte.", text);

        foreach (var instance in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                if (await _evo.SendInternalTextAsync(instance, to, text, ct))
                    return new SendResult(true, instance, null, text);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Reporte de leads: falló el envío por {Instance}", instance);
            }
        }
        return new SendResult(false, null,
            $"Ninguna línea pudo mandarlo (probé {string.Join(", ", candidates.Distinct(StringComparer.OrdinalIgnoreCase))}).", text);
    }
}
