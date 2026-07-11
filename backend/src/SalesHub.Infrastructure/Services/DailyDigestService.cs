using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SalesHub.Core.Abstractions;
using SalesHub.Core.Domain.Entities;
using SalesHub.Core.Domain.Entities.Social;
using SalesHub.Core.Domain.Enums;
using SalesHub.Infrastructure.Persistence;

namespace SalesHub.Infrastructure.Services;

/// <summary>
/// Resumen diario por WhatsApp: compone un mensaje con los números del día
/// (leads nuevos por app, respuestas, hot leads sin atender, cierres, posteos vs cupo
/// y líneas caídas) y lo manda por la línea configurada en <see cref="DigestSettings"/>.
/// Lo dispara <c>DailyDigestWorker</c> a la hora elegida; desde /digest también se puede
/// previsualizar y mandar a demanda.
/// </summary>
public class DailyDigestService
{
    private readonly ApplicationDbContext _db;
    private readonly IEvolutionClient _evo;
    private readonly ILogger<DailyDigestService> _log;

    public DailyDigestService(ApplicationDbContext db, IEvolutionClient evo, ILogger<DailyDigestService> log)
    {
        _db = db; _evo = evo; _log = log;
    }

    public record SendResult(bool Sent, string? Error, string? Instance, string? Phone);

    public static TimeZoneInfo ArTz { get; } = CreateArTz();

    private static TimeZoneInfo CreateArTz()
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById("America/Argentina/Buenos_Aires"); }
        catch { return TimeZoneInfo.CreateCustomTimeZone("AR", TimeSpan.FromHours(-3), "AR", "AR"); }
    }

    private static readonly string[] Dias =
        { "domingo", "lunes", "martes", "miércoles", "jueves", "viernes", "sábado" };

    /// <summary>Manda el resumen al número configurado (o al maestro de inspiraciones como fallback).</summary>
    /// <param name="markSent">true = envío automático del día (actualiza LastSentAt); false = envío manual de prueba.</param>
    public async Task<SendResult> SendAsync(bool markSent, CancellationToken ct)
    {
        var s = await _db.DigestSettings.FirstOrDefaultAsync(ct);
        if (s is null || !s.Enabled) return new(false, "El resumen diario está apagado.", null, null);
        if (string.IsNullOrWhiteSpace(s.InstanceName)) return new(false, "No hay línea de WhatsApp elegida.", null, null);

        var phone = s.Phone;
        if (string.IsNullOrWhiteSpace(phone))
            phone = await _db.InspirationSettings.AsNoTracking().Select(x => x.MasterPhone).FirstOrDefaultAsync(ct);
        if (string.IsNullOrWhiteSpace(phone))
            return new(false, "No hay número destino (ni maestro de inspiraciones configurado).", s.InstanceName, null);

        var text = await ComposeAsync(ct);
        var ok = await _evo.SendTextAsync(s.InstanceName, phone, text, ct);
        if (!ok)
        {
            _log.LogWarning("Resumen diario: Evolution rechazó el envío por {Instance} a {Phone}", s.InstanceName, phone);
            return new(false, "Evolution no aceptó el envío (¿línea desconectada?).", s.InstanceName, phone);
        }

        if (markSent)
        {
            s.LastSentAt = DateTimeOffset.UtcNow;
            s.UpdatedAt = DateTimeOffset.UtcNow;
            await _db.SaveChangesAsync(ct);
        }
        return new(true, null, s.InstanceName, phone);
    }

    /// <summary>Arma el texto del resumen (formato WhatsApp). "Hoy" = día calendario Argentina.</summary>
    public async Task<string> ComposeAsync(CancellationToken ct)
    {
        var nowAr = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, ArTz);
        var since = new DateTimeOffset(nowAr.Year, nowAr.Month, nowAr.Day, 0, 0, 0, nowAr.Offset);

        var newByProduct = await _db.Leads.AsNoTracking()
            .Where(l => l.CreatedAt >= since)
            .GroupBy(l => l.ProductKey)
            .Select(g => new { Key = g.Key, N = g.Count() })
            .OrderByDescending(x => x.N)
            .ToListAsync(ct);
        var firstReplies = await _db.Leads.AsNoTracking().CountAsync(l => l.FirstReplyAt >= since, ct);
        var activeConvos = await _db.ConversationMessages.AsNoTracking()
            .Where(m => m.Timestamp >= since && m.Direction == MessageDirection.Inbound)
            .Select(m => m.LeadId).Distinct().CountAsync(ct);
        var closedByProduct = await _db.Leads.AsNoTracking()
            .Where(l => l.Status == LeadStatus.Closed && l.ClosedAt >= since)
            .GroupBy(l => l.ProductKey)
            .Select(g => new { Key = g.Key, N = g.Count() })
            .OrderByDescending(x => x.N)
            .ToListAsync(ct);

        var hot = await _db.Leads.AsNoTracking()
            .Where(l => l.HotAlertedAt >= since)
            .Select(l => new { l.Name, l.ProductKey, Tomado = l.BotMutedAt != null })
            .ToListAsync(ct);

        var sb = new StringBuilder();
        sb.Append("📊 *Resumen del día — ").Append(Dias[(int)nowAr.DayOfWeek])
          .Append(' ').Append(nowAr.Day).Append('/').Append(nowAr.Month).Append("*\n\n");

        static string PorProducto(IEnumerable<(string Key, int N)> xs) =>
            string.Join(" · ", xs.Select(x => $"{(x.Key == "" ? "sin producto" : x.Key)} {x.N}"));

        sb.Append("*Leads*\n");
        sb.Append("📥 Nuevos: ").Append(newByProduct.Sum(x => x.N));
        if (newByProduct.Count > 0) sb.Append(" — ").Append(PorProducto(newByProduct.Select(x => (x.Key, x.N))));
        sb.Append('\n');
        sb.Append("💬 Primeras respuestas: ").Append(firstReplies)
          .Append(" · charlas con actividad: ").Append(activeConvos).Append('\n');
        sb.Append("✅ Cierres/altas: ").Append(closedByProduct.Sum(x => x.N));
        if (closedByProduct.Count > 0) sb.Append(" — ").Append(PorProducto(closedByProduct.Select(x => (x.Key, x.N))));
        sb.Append('\n');

        if (hot.Count > 0)
        {
            var sinAtender = hot.Count(h => !h.Tomado);
            sb.Append("\n🔥 *Hot leads*: ").Append(hot.Count);
            if (sinAtender > 0) sb.Append(" (").Append(sinAtender).Append(" sin atender)");
            sb.Append('\n');
            foreach (var h in hot.OrderBy(h => h.Tomado).Take(5))
            {
                var nombre = string.IsNullOrWhiteSpace(h.Name) ? "(sin nombre)" : h.Name;
                sb.Append("  • ").Append(nombre).Append(" (").Append(h.ProductKey).Append(')')
                  .Append(h.Tomado ? "" : " — *sin atender*").Append('\n');
            }
            if (hot.Count > 5) sb.Append("  … y ").Append(hot.Count - 5).Append(" más\n");
        }

        await AppendPosteosAsync(sb, ct);
        await AppendLineasAsync(sb, ct);

        sb.Append("\n_sales-hub · resumen automático_");
        return sb.ToString();
    }

    /// <summary>
    /// Posteos generados vs cupo del día. Mismo criterio de "hoy"/cupo que el gauge
    /// today-health y el catch-up de SocialContentWorker (hora local del server), para
    /// que los números cuadren con el dashboard.
    /// </summary>
    private async Task AppendPosteosAsync(StringBuilder sb, CancellationToken ct)
    {
        var profiles = await _db.PostingProfiles.AsNoTracking().Where(p => p.Enabled).ToListAsync(ct);
        if (profiles.Count == 0) return;

        var hour = DateTime.Now.Hour;
        var now = DateTimeOffset.Now;
        var dayStart = new DateTimeOffset(now.Year, now.Month, now.Day, 0, 0, 0, now.Offset);

        var channels = await _db.PostingChannels.AsNoTracking().Where(c => c.Enabled).ToListAsync(ct);
        var todays = await _db.SocialPosts.AsNoTracking()
            .Where(s => s.CreatedAt >= dayStart)
            .GroupBy(s => new { s.ProductKey, s.Status })
            .Select(g => new { g.Key.ProductKey, g.Key.Status, N = g.Count() })
            .ToListAsync(ct);

        int totalCreated = 0, totalQuota = 0;
        var issues = new List<string>();
        foreach (var p in profiles.OrderBy(p => p.ProductKey))
        {
            var chs = channels.Count(c => c.ProductKey == p.ProductKey);
            var perSlot = Math.Max(1, p.PostsPerDay) * chs;
            var slotsPassed = (p.PostHours ?? new List<int>()).Count(h => h <= hour);
            var quotaSoFar = perSlot * slotsPassed;
            var dayQuota = perSlot * (p.PostHours?.Count ?? 0);
            var rows = todays.Where(t => t.ProductKey == p.ProductKey).ToList();
            var created = rows.Sum(r => r.N);
            var errors = rows.Where(r => r.Status == SocialPostStatus.Error).Sum(r => r.N);

            totalCreated += created;
            totalQuota += dayQuota;
            if (errors > 0) issues.Add($"  ❌ {p.ProductKey}: {errors} con error");
            else if (created < quotaSoFar) issues.Add($"  ⚠️ {p.ProductKey}: {created}/{quotaSoFar} al momento (atrasado)");
        }

        sb.Append("\n📱 *Posteos*: ").Append(totalCreated).Append('/').Append(totalQuota).Append(" del cupo");
        if (issues.Count == 0) sb.Append(" · todo en cronograma ✅");
        sb.Append('\n');
        foreach (var line in issues) sb.Append(line).Append('\n');
    }

    private async Task AppendLineasAsync(StringBuilder sb, CancellationToken ct)
    {
        var down = await _db.EvolutionInstances.AsNoTracking()
            .Include(i => i.Seller)
            .Where(i => i.Status != InstanceStatus.Connected)
            .Select(i => new
            {
                Label = i.Seller != null ? i.Seller.DisplayName : (i.ProductKey ?? i.InstanceName),
                i.Status,
            })
            .ToListAsync(ct);

        if (down.Count == 0)
        {
            sb.Append("\n📶 Líneas: todas conectadas ✅\n");
            return;
        }
        sb.Append("\n📶 *Líneas caídas*: ").Append(down.Count).Append('\n');
        foreach (var d in down.Take(6))
            sb.Append("  • ").Append(d.Label).Append(" (").Append(d.Status).Append(")\n");
        if (down.Count > 6) sb.Append("  … y ").Append(down.Count - 6).Append(" más\n");
    }
}
