using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SalesHub.Core.Abstractions;
using SalesHub.Core.Domain.Entities;
using SalesHub.Core.Domain.Enums;
using SalesHub.Infrastructure.Persistence;

namespace SalesHub.Infrastructure.Services;

/// <summary>
/// Campañas de remarketing con video (ver <see cref="VideoCampaign"/>): arma la audiencia, manda
/// de a un lead (texto → video → texto, con "escribiendo…") y registra las respuestas.
/// </summary>
public class VideoCampaignService
{
    private readonly ApplicationDbContext _db;
    private readonly IEvolutionClient _evo;
    private readonly PersonHandoffService _handoff;
    private readonly ILogger<VideoCampaignService> _log;

    private static readonly TimeZoneInfo Art = TimeZoneInfo.FindSystemTimeZoneById("America/Argentina/Buenos_Aires");

    public VideoCampaignService(ApplicationDbContext db, IEvolutionClient evo, PersonHandoffService handoff,
        ILogger<VideoCampaignService> log)
    {
        _db = db; _evo = evo; _handoff = handoff; _log = log;
    }

    // ── Audiencia ──────────────────────────────────────────────────────────

    public record AudienceItem(Guid LeadId, string Name, string? Phone, string Source, string? FirstMessage,
        DateTimeOffset? LastActivity, int OurMessages, bool Suggested, string? WhyNot);

    // Primer mensaje que no es de un prospecto: contestadores automáticos, spam, nada de texto.
    private static readonly Regex JunkRx = new(
        @"gracias por comunicarte|gracias por (tu|su) mensaje|en breve te|horario de atencion|can i learn more|^\[(imagen|audio|video|sticker|documento)|https?://",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Leads de la app que nos escribieron ELLOS primero (anuncio, consulta) y después no contestaron
    /// nada de lo que les mandamos, quietos hace más de <paramref name="minIdleDays"/> días y que
    /// nunca recibieron una campaña con video. Marca como no sugeridos los que parecen basura.
    /// </summary>
    public async Task<List<AudienceItem>> AudienceAsync(string productKey, int minIdleDays, CancellationToken ct)
    {
        var idleCut = DateTimeOffset.UtcNow.AddDays(-minIdleDays);
        var dead = new[] { LeadStatus.Closed, LeadStatus.Lost, LeadStatus.Blocked, LeadStatus.NoWhatsApp };
        var rows = await (
            from l in _db.Leads.AsNoTracking()
            where l.ProductKey == productKey && l.WhatsappPhone != null && l.WhatsappPhone != ""
                  && !dead.Contains(l.Status) && !l.Tags.Contains("sandbox")
                  && !_db.VideoCampaignRecipients.Any(r => r.LeadId == l.Id)
            let firstIn = _db.ConversationMessages.Where(m => m.LeadId == l.Id && m.Direction == MessageDirection.Inbound).Min(m => (DateTimeOffset?)m.Timestamp)
            let lastIn = _db.ConversationMessages.Where(m => m.LeadId == l.Id && m.Direction == MessageDirection.Inbound).Max(m => (DateTimeOffset?)m.Timestamp)
            let firstOut = _db.ConversationMessages.Where(m => m.LeadId == l.Id && m.Direction == MessageDirection.Outbound).Min(m => (DateTimeOffset?)m.Timestamp)
            let lastOut = _db.ConversationMessages.Where(m => m.LeadId == l.Id && m.Direction == MessageDirection.Outbound).Max(m => (DateTimeOffset?)m.Timestamp)
            where firstIn != null && firstOut != null && firstIn < firstOut && lastIn < firstOut && lastOut < idleCut
            orderby lastOut descending
            select new
            {
                l.Id, l.Name, l.WhatsappPhone, l.Source, LastOut = lastOut,
                Ours = _db.ConversationMessages.Count(m => m.LeadId == l.Id && m.Direction == MessageDirection.Outbound),
                First = _db.ConversationMessages.Where(m => m.LeadId == l.Id && m.Direction == MessageDirection.Inbound)
                    .OrderBy(m => m.Timestamp).Select(m => m.Text).FirstOrDefault(),
            }).Take(1000).ToListAsync(ct);

        var otherProducts = await _db.Products.AsNoTracking().Where(p => p.ProductKey != productKey)
            .Select(p => p.DisplayName).ToListAsync(ct);
        return rows.Select(r =>
        {
            var first = (r.First ?? "").Trim();
            string? why = null;
            if (first.Length < 4) why = "primer mensaje sin contenido";
            else if (JunkRx.IsMatch(first)) why = "parece contestador automatico o spam";
            else if (otherProducts.Any(p => p.Length > 3 && first.Contains(p, StringComparison.OrdinalIgnoreCase))
                     || Regex.IsMatch(first, "seguimiento de obra", RegexOptions.IgnoreCase))
                why = "pregunto por otra app";
            return new AudienceItem(r.Id, r.Name, r.WhatsappPhone, r.Source.ToString(), Trunc(first, 140), r.LastOut,
                r.Ours, why is null, why);
        }).ToList();
    }

    // ── Envío ──────────────────────────────────────────────────────────────

    public static DateTimeOffset ArtNow() => TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, Art);

    public static bool InWindow(VideoCampaign c, DateTimeOffset artNow)
    {
        var dow = artNow.DayOfWeek == DayOfWeek.Sunday ? 7 : (int)artNow.DayOfWeek;
        return c.Weekdays.Contains(dow) && artNow.Hour >= c.WindowFromHour && artNow.Hour < c.WindowToHour;
    }

    /// <summary>Minutos entre envíos: reparte el tope del día en la franja (entre 3 y 20).</summary>
    public static int GapMinutes(VideoCampaign c) =>
        Math.Clamp((c.WindowToHour - c.WindowFromHour) * 60 / Math.Max(1, c.PerDay), 3, 20);

    public async Task<int> SentTodayAsync(VideoCampaign c, CancellationToken ct)
    {
        var art = ArtNow();
        var startOfDay = new DateTimeOffset(art.Year, art.Month, art.Day, 0, 0, 0, art.Offset);
        return await _db.VideoCampaignRecipients.CountAsync(r => r.CampaignId == c.Id && r.SentAt >= startOfDay, ct);
    }

    // Variantes: "{hola|buenas|que tal}" → una al azar por lead. "{nombre}" no tiene "|" y no entra acá.
    private static readonly Regex SpinRx = new(@"\{([^{}]*\|[^{}]*)\}", RegexOptions.Compiled);

    /// <summary>Azar fijo por lead: la misma persona ve siempre la misma variante (prueba = envío real).</summary>
    public static Random SeededRandom(Guid id) => new(BitConverter.ToInt32(id.ToByteArray(), 0));

    /// <summary>
    /// Elige una variante de cada "{a|b|c}" y reemplaza "{nombre}" por el nombre de pila (sin nombre
    /// usable se saca, con el espacio de antes). Aplica las reglas de copy (sin tildes, ¿ ni emojis).
    /// </summary>
    public static string Render(string template, string? leadName, Random? rnd = null)
    {
        rnd ??= Random.Shared;
        template = SpinRx.Replace(template, m =>
        {
            var opts = m.Groups[1].Value.Split('|');
            return opts[rnd.Next(opts.Length)].Trim();
        });
        var first = RemarketingService.FriendlyName(leadName);
        var t = first is null
            ? Regex.Replace(template, @"[ \t]*\{nombre\}", "")
            : template.Replace("{nombre}", first);
        return CopyStyle.Clean(t);
    }

    /// <summary>Bloques separados por una línea en blanco = burbujas separadas.</summary>
    public static List<string> Bubbles(string text) =>
        Regex.Split(text.Replace("\r", ""), @"\n\s*\n").Select(b => b.Trim()).Where(b => b.Length > 0).ToList();

    private static int TypingSeconds(string text) => Math.Clamp((int)Math.Round(text.Length / (50 * 5 / 60.0)), 2, 30);

    private async Task TypeAsync(string instance, string phone, string text, CancellationToken ct)
    {
        var seconds = TypingSeconds(text);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try { await _evo.SetPresenceTypingAsync(instance, phone, seconds, ct); } catch { /* best-effort */ }
        var rest = TimeSpan.FromSeconds(seconds) - sw.Elapsed;
        if (rest > TimeSpan.Zero) await Task.Delay(rest, ct);
    }

    /// <summary>Línea conectada y que puede mandar; null si está OK, el motivo si no.</summary>
    public async Task<string?> LineProblemAsync(string instanceName, CancellationToken ct)
    {
        var line = await _db.EvolutionInstances.AsNoTracking().FirstOrDefaultAsync(i => i.InstanceName == instanceName, ct);
        if (line is null) return "la linea no existe";
        if (line.ListenOnly) return "la linea es de solo escucha";
        if (line.Status != InstanceStatus.Connected) return "la linea no esta conectada";
        return null;
    }

    /// <summary>Manda la campaña completa a un número (prueba). No crea lead ni registra nada.</summary>
    public async Task<bool> SendTestAsync(VideoCampaign c, string phone, string? sampleName, CancellationToken ct)
    {
        var asset = await _db.MediaAssets.AsNoTracking().FirstAsync(m => m.Id == c.MediaAssetId, ct);
        var rnd = new Random();
        foreach (var b in Bubbles(Render(c.MessageBefore, sampleName, rnd)))
        {
            await TypeAsync(c.InstanceName, phone, b, ct);
            if (!await _evo.SendTextAsync(c.InstanceName, phone, b, ct)) return false;
        }
        if (!await _evo.SendMediaAsync(c.InstanceName, phone, asset.Content, asset.MimeType, asset.FileName, null, ct)) return false;
        foreach (var b in Bubbles(Render(c.MessageAfter, sampleName, rnd)))
        {
            await Task.Delay(TimeSpan.FromSeconds(4), ct);
            await TypeAsync(c.InstanceName, phone, b, ct);
            if (!await _evo.SendTextAsync(c.InstanceName, phone, b, ct)) return false;
        }
        return true;
    }

    /// <summary>
    /// Manda la campaña al próximo lead pendiente. Re-chequea al lead justo antes: si escribió
    /// mientras tanto o ya se cerró, se saltea. Devuelve false si no quedaba nadie.
    /// </summary>
    public async Task<bool> SendNextAsync(VideoCampaign c, CancellationToken ct)
    {
        var rec = await _db.VideoCampaignRecipients.Include(r => r.Lead)
            .Where(r => r.CampaignId == c.Id && r.Status == VideoCampaignRecipientStatus.Pending)
            .OrderBy(r => r.Id)
            .FirstOrDefaultAsync(ct);
        if (rec is null) return false;
        var lead = rec.Lead!;
        var now = DateTimeOffset.UtcNow;

        var wroteMeanwhile = await _db.ConversationMessages.AnyAsync(m => m.LeadId == lead.Id
            && m.Direction == MessageDirection.Inbound && m.Timestamp > c.CreatedAt, ct);
        string? skip = string.IsNullOrWhiteSpace(lead.WhatsappPhone) ? "sin telefono"
            : lead.Status is LeadStatus.Closed or LeadStatus.Lost or LeadStatus.Blocked or LeadStatus.NoWhatsApp ? $"estado {lead.Status}"
            : wroteMeanwhile ? "escribio despues de armar la campaña"
            : null;
        if (skip is not null)
        {
            rec.Status = VideoCampaignRecipientStatus.Skipped;
            rec.Error = skip;
            await _db.SaveChangesAsync(ct);
            return true;
        }

        var asset = await _db.MediaAssets.AsNoTracking().FirstAsync(m => m.Id == c.MediaAssetId, ct);
        var phone = lead.WhatsappPhone!;
        // Lo que conteste lo atiende una persona: el bot no se mete en esta charla.
        lead.BotMutedAt ??= now;
        rec.Status = VideoCampaignRecipientStatus.Sent;
        rec.SentAt = now;
        c.LastSentAt = now;
        await _db.SaveChangesAsync(ct);

        try
        {
            var rnd = SeededRandom(lead.Id);
            var parts = new List<(string Text, bool Video)>();
            parts.AddRange(Bubbles(Render(c.MessageBefore, lead.Name, rnd)).Select(t => (t, false)));
            parts.Add(($"[video] {asset.FileName}", true));
            parts.AddRange(Bubbles(Render(c.MessageAfter, lead.Name, rnd)).Select(t => (t, false)));
            var afterVideo = false;
            foreach (var p in parts)
            {
                if (afterVideo) { await Task.Delay(TimeSpan.FromSeconds(4), ct); afterVideo = false; }
                if (!p.Video) await TypeAsync(c.InstanceName, phone, p.Text, ct);
                // Persistir ANTES de mandar: si el eco fromMe llega antes, el takeover no lo confunde con un humano.
                var msg = new ConversationMessage
                {
                    Id = Guid.NewGuid(), LeadId = lead.Id, SellerId = lead.SellerId,
                    Direction = MessageDirection.Outbound, Status = MessageDeliveryStatus.Sent,
                    Text = p.Text, EvolutionInstance = c.InstanceName, Timestamp = DateTimeOffset.UtcNow, IsRead = true,
                };
                _db.ConversationMessages.Add(msg);
                await _db.SaveChangesAsync(ct);
                var ok = p.Video
                    ? await _evo.SendMediaAsync(c.InstanceName, phone, asset.Content, asset.MimeType, asset.FileName, null, ct)
                    : await _evo.SendTextAsync(c.InstanceName, phone, p.Text, ct);
                if (!ok)
                {
                    msg.Status = MessageDeliveryStatus.Failed;
                    throw new InvalidOperationException(p.Video ? "WhatsApp no acepto el video" : "WhatsApp no acepto el texto");
                }
                afterVideo = p.Video;
            }
            lead.UpdatedAt = DateTimeOffset.UtcNow;
            await _db.SaveChangesAsync(ct);
            _log.LogInformation("Campaña con video {Campaign}: enviada a {Lead}", c.Name, lead.Id);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Ya se intentó mandar: no se reintenta (duplicaría mensajes).
            rec.Status = VideoCampaignRecipientStatus.Failed;
            rec.Error = Trunc(ex.Message, 500);
            await _db.SaveChangesAsync(ct);
            _log.LogWarning(ex, "Campaña con video {Campaign}: falló el envío a {Lead}", c.Name, lead.Id);
        }
        return true;
    }

    // ── Respuestas ─────────────────────────────────────────────────────────

    /// <summary>
    /// El lead contestó después de recibir la campaña: queda para la métrica y se le pasa a la
    /// persona del pase de la app (o al maestro). No guarda: lo persiste quien llama.
    /// </summary>
    public async Task OnReplyAsync(Lead lead, DateTimeOffset at, string? latestText, CancellationToken ct)
    {
        var rec = await _db.VideoCampaignRecipients.Include(r => r.Campaign)
            .Where(r => r.LeadId == lead.Id && r.Status == VideoCampaignRecipientStatus.Sent
                        && r.RepliedAt == null && r.SentAt <= at)
            .OrderByDescending(r => r.SentAt)
            .FirstOrDefaultAsync(ct);
        if (rec is null) return;
        rec.RepliedAt = at;
        var personId = await _db.OnboardingConfigs.AsNoTracking()
            .Where(c => c.ProductKey == lead.ProductKey).Select(c => c.HandoffSellerId).FirstOrDefaultAsync(ct);
        lead.NextActionAt = DateTimeOffset.UtcNow;
        lead.NextActionNote = "Contesto a la campaña con video: coordinar la demo";
        await _handoff.HandoffAsync(lead, personId, "contesto a la campaña con video", latestText,
            $"le mandamos la campaña \"{rec.Campaign!.Name}\"", ct);
    }

    private static string Trunc(string s, int max) => s.Length <= max ? s : s[..max] + "…";
}
