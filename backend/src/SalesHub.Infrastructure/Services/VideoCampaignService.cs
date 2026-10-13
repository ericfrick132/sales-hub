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
        DateTimeOffset? LastActivity, int OurMessages, bool Suggested, string? WhyNot, string Risk, string RiskWhy);

    /// <summary>Lo que hace falta de un lead para medir el riesgo de ban de escribirle desde una línea.</summary>
    public record RiskInput(bool WroteEver, bool ChatOnLine, int Unanswered);

    /// <summary>
    /// Riesgo de ban de mandarle la campaña, mirado desde la línea que manda (WhatsApp castiga los
    /// mensajes a gente que no te tiene y los que se ignoran o se reportan):
    ///  - bajo: ya charló con ESTA línea y le debemos a lo sumo 2 mensajes sin respuesta.
    ///  - medio: conoce la marca (escribió por otra línea, dejó sus datos o se registró) y no le insistimos
    ///    más de 2 veces; para esta línea es un número nuevo.
    ///  - alto: ya ignoró 3 o más mensajes, o nunca escribió y ya le mandamos 2 o más. No se le manda.
    /// </summary>
    public static (string Risk, string Why) RiskOf(RiskInput r)
    {
        if (r.Unanswered >= 3) return ("alto", $"ya ignoro {r.Unanswered} mensajes nuestros");
        if (!r.WroteEver && r.Unanswered >= 2) return ("alto", $"nunca escribio y ya le mandamos {r.Unanswered}");
        if (r.WroteEver && r.ChatOnLine) return ("bajo", r.Unanswered == 0 ? "charlo con esta linea y escribio ultimo" : "charlo con esta linea");
        if (r.WroteEver) return ("medio", r.Unanswered == 0 ? "escribio ultimo, pero por otra linea" : "charlo con nosotros por otra linea");
        return ("medio", r.Unanswered == 0 ? "dejo sus datos y nunca le escribimos" : "dejo sus datos, le escribimos 1 vez");
    }

    /// <summary>Datos de riesgo de varios leads respecto de una línea.</summary>
    public async Task<Dictionary<Guid, RiskInput>> RiskInputsAsync(IReadOnlyCollection<Guid> leadIds, string instanceName, CancellationToken ct)
    {
        var rows = await _db.Leads.AsNoTracking().Where(l => leadIds.Contains(l.Id)).Select(l => new
        {
            l.Id,
            LastIn = _db.ConversationMessages.Where(m => m.LeadId == l.Id && m.Direction == MessageDirection.Inbound).Max(m => (DateTimeOffset?)m.Timestamp),
            OnLine = _db.ConversationMessages.Any(m => m.LeadId == l.Id && m.EvolutionInstance == instanceName),
        }).ToListAsync(ct);
        var result = new Dictionary<Guid, RiskInput>();
        foreach (var r in rows)
        {
            var unanswered = await _db.ConversationMessages.CountAsync(m => m.LeadId == r.Id
                && m.Direction == MessageDirection.Outbound && (r.LastIn == null || m.Timestamp > r.LastIn), ct);
            result[r.Id] = new RiskInput(r.LastIn != null, r.OnLine, unanswered);
        }
        return result;
    }

    // Primer mensaje que no es de un prospecto: contestadores automáticos, spam, nada de texto.
    private static readonly Regex JunkRx = new(
        @"gracias por comunicarte|gracias por (tu|su) mensaje|en breve te|horario de atencion|can i learn more|^\[(imagen|audio|video|sticker|documento)|https?://",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex ConsultaRx = new(@"\b(hola|buenas?|buen dia|info|informacion|precio|cuanto|consulta|quiero)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Rubros que son de OTRA app: un "agendas para manicurista" que entró por GymHero es de TurnosPro.
    private static readonly Dictionary<string, Regex> OtherAppRx = new()
    {
        ["gymhero"] = new(@"manicur|lash|pestañ|uñas|peluquer|barber|estetica|seguimiento de obra|padel|cancha", RegexOptions.IgnoreCase),
        ["turnospro"] = new(@"gimnasio|\bgym\b|cuotas?|alumnos|seguimiento de obra|padel|cancha", RegexOptions.IgnoreCase),
        ["playcrew"] = new(@"gimnasio|\bgym\b|manicur|lash|pestañ|uñas|peluquer|seguimiento de obra", RegexOptions.IgnoreCase),
    };

    // Respuesta que cierra la puerta: a estos no se les insiste.
    private static readonly Regex NotInterestedRx = new(
        @"no me interesa|no gracias|no,? gracias|ya tengo|ya uso|no por ahora|no estoy interesad|no necesito|dejame de|deja de|basta|no molest|no me escrib|\bbaja\b|equivocad",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Grupos de leads quietos que puede tomar una campaña.</summary>
    public static readonly IReadOnlyDictionary<string, string> Segments = new Dictionary<string, string>
    {
        ["escribieron"] = "Nos escribieron primero y despues no contestaron",
        ["enfriados"] = "Charlaron con nosotros y se enfriaron",
        ["formulario"] = "Dejaron sus datos en un formulario de Meta y nunca contestaron",
        ["registrados"] = "Se registraron en la app y nunca contestaron",
    };

    /// <summary>
    /// Leads de la app de un grupo (<see cref="Segments"/>), quietos hace más de <paramref name="minIdleDays"/>
    /// días y que nunca recibieron una campaña con video. Marca como no sugeridos los que parecen basura,
    /// son de otro país o ya dijeron que no.
    /// </summary>
    public async Task<List<AudienceItem>> AudienceAsync(string productKey, int minIdleDays, string segment, string instanceName, CancellationToken ct)
    {
        var idleCut = DateTimeOffset.UtcNow.AddDays(-minIdleDays);
        var dead = new[] { LeadStatus.Closed, LeadStatus.Lost, LeadStatus.Blocked, LeadStatus.NoWhatsApp };
        var q =
            from l in _db.Leads.AsNoTracking()
            where l.ProductKey == productKey && l.WhatsappPhone != null && l.WhatsappPhone != ""
                  && !dead.Contains(l.Status) && !l.Tags.Contains("sandbox")
                  && !_db.VideoCampaignRecipients.Any(r => r.LeadId == l.Id)
            let firstIn = _db.ConversationMessages.Where(m => m.LeadId == l.Id && m.Direction == MessageDirection.Inbound).Min(m => (DateTimeOffset?)m.Timestamp)
            let lastIn = _db.ConversationMessages.Where(m => m.LeadId == l.Id && m.Direction == MessageDirection.Inbound).Max(m => (DateTimeOffset?)m.Timestamp)
            let firstOut = _db.ConversationMessages.Where(m => m.LeadId == l.Id && m.Direction == MessageDirection.Outbound).Min(m => (DateTimeOffset?)m.Timestamp)
            let lastOut = _db.ConversationMessages.Where(m => m.LeadId == l.Id && m.Direction == MessageDirection.Outbound).Max(m => (DateTimeOffset?)m.Timestamp)
            let last = lastIn > lastOut ? lastIn : (lastOut ?? lastIn ?? (DateTimeOffset?)l.CreatedAt)
            where last < idleCut
            select new { l, firstIn, lastIn, firstOut, last };
        q = segment switch
        {
            "enfriados" => q.Where(x => x.firstIn != null && x.firstOut != null && x.lastIn >= x.firstOut),
            "formulario" => q.Where(x => x.firstIn == null && x.l.Source == LeadSource.MetaLeadAd),
            "registrados" => q.Where(x => x.firstIn == null && x.l.Source == LeadSource.ProductReengage),
            _ => q.Where(x => x.firstIn != null && x.firstOut != null && x.firstIn < x.firstOut && x.lastIn < x.firstOut),
        };
        // Lo que se muestra: el primer mensaje del que escribió primero, el último del que charló.
        var useLast = segment == "enfriados";
        var rows = await q.OrderByDescending(x => x.last).Select(x => new
            {
                x.l.Id, x.l.Name, x.l.WhatsappPhone, x.l.Source, x.last, Wrote = x.firstIn != null,
                Ours = _db.ConversationMessages.Count(m => m.LeadId == x.l.Id && m.Direction == MessageDirection.Outbound),
                Unanswered = _db.ConversationMessages.Count(m => m.LeadId == x.l.Id && m.Direction == MessageDirection.Outbound
                    && (x.lastIn == null || m.Timestamp > x.lastIn)),
                OnLine = _db.ConversationMessages.Any(m => m.LeadId == x.l.Id && m.EvolutionInstance == instanceName),
                Text = useLast
                    ? _db.ConversationMessages.Where(m => m.LeadId == x.l.Id && m.Direction == MessageDirection.Inbound)
                        .OrderByDescending(m => m.Timestamp).Select(m => m.Text).FirstOrDefault()
                    : _db.ConversationMessages.Where(m => m.LeadId == x.l.Id && m.Direction == MessageDirection.Inbound)
                        .OrderBy(m => m.Timestamp).Select(m => m.Text).FirstOrDefault(),
            }).Take(1500).ToListAsync(ct);

        var otherProducts = await _db.Products.AsNoTracking().Where(p => p.ProductKey != productKey)
            .Select(p => p.DisplayName).ToListAsync(ct);
        return rows.Select(r =>
        {
            var text = (r.Text ?? "").Trim();
            string? why = null;
            var digits = new string((r.WhatsappPhone ?? "").Where(char.IsDigit).ToArray());
            // Sin código de país y con 10 dígitos también es argentino (área + número).
            if (!digits.StartsWith("54") && digits.Length != 10) why = "numero de otro pais";
            else if (segment == "enfriados")
            {
                if (NotInterestedRx.IsMatch(text)) why = "ya dijo que no";
                else if (JunkRx.IsMatch(text) && !text.StartsWith("[")) why = "parece contestador automatico o spam";
            }
            else if (segment == "escribieron")
            {
                var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (text.Length < 4) why = "primer mensaje sin contenido";
                // Una o dos palabras que no son saludo ni pedido de info ("golazo", "nono"): no es una consulta.
                else if (words.Length <= 2 && !ConsultaRx.IsMatch(text)) why = "no parece una consulta";
                else if (JunkRx.IsMatch(text)) why = "parece contestador automatico o spam";
            }
            if (why is null && text.Length > 0
                && (otherProducts.Any(p => p.Length > 3 && text.Contains(p, StringComparison.OrdinalIgnoreCase))
                    || (OtherAppRx.TryGetValue(productKey, out var rx) && rx.IsMatch(text))))
                why = "pregunto por otra app";
            var (risk, riskWhy) = RiskOf(new RiskInput(r.Wrote, r.OnLine, r.Unanswered));
            return new AudienceItem(r.Id, r.Name, r.WhatsappPhone, r.Source.ToString(),
                text.Length > 0 ? Trunc(text, 140) : null, r.last, r.Ours, why is null && risk != "alto", why, risk, riskWhy);
        }).ToList();
    }

    // ── Envío ──────────────────────────────────────────────────────────────

    public static DateTimeOffset ArtNow() => TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, Art);

    public static bool InWindow(VideoCampaign c, DateTimeOffset artNow)
    {
        var dow = artNow.DayOfWeek == DayOfWeek.Sunday ? 7 : (int)artNow.DayOfWeek;
        return c.Weekdays.Contains(dow) && artNow.Hour >= c.WindowFromHour && artNow.Hour < c.WindowToHour;
    }

    /// <summary>Minutos entre envíos: reparte el tope del día en la franja (entre 5 y 30).</summary>
    public static int GapMinutes(VideoCampaign c) =>
        Math.Clamp((c.WindowToHour - c.WindowFromHour) * 60 / Math.Max(1, c.PerDay), 5, 30);

    /// <summary>
    /// Espera hasta el próximo envío: el espacio base ±40% (de 9 min sale entre 5,4 y 12,6). Un intervalo
    /// fijo es un patrón de bot. El azar sale del último envío, así el worker (que mira cada minuto)
    /// llega siempre al mismo número para ese intervalo.
    /// </summary>
    public static TimeSpan NextGap(VideoCampaign c, DateTimeOffset lastSent)
    {
        var frac = new Random((int)(lastSent.UtcTicks % int.MaxValue)).NextDouble();
        return TimeSpan.FromMinutes(GapMinutes(c) * (0.6 + 0.8 * frac));
    }

    private static DateTimeOffset StartOfArtDay()
    {
        var art = ArtNow();
        return new DateTimeOffset(art.Year, art.Month, art.Day, 0, 0, 0, art.Offset);
    }

    /// <summary>Envíos de hoy por la LÍNEA (todas sus campañas): el tope es por número, no por campaña.</summary>
    public async Task<int> LineSentTodayAsync(string instanceName, CancellationToken ct)
    {
        var start = StartOfArtDay();
        return await _db.VideoCampaignRecipients.CountAsync(r => r.Campaign!.InstanceName == instanceName && r.SentAt >= start, ct);
    }

    /// <summary>Último envío de cualquier campaña por esa línea.</summary>
    public Task<DateTimeOffset?> LineLastSentAsync(string instanceName, CancellationToken ct) =>
        _db.VideoCampaigns.Where(c => c.InstanceName == instanceName).MaxAsync(c => c.LastSentAt, ct);

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
        if (skip is null)
        {
            var risk = (await RiskInputsAsync(new[] { lead.Id }, c.InstanceName, ct)).GetValueOrDefault(lead.Id);
            if (risk is not null && RiskOf(risk).Risk == "alto") skip = "riesgo alto de ban: " + RiskOf(risk).Why;
        }
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

    /// <summary>Saca de la campaña a los pendientes que hoy dan riesgo alto de ban. Devuelve cuántos.</summary>
    public async Task<int> PruneHighRiskAsync(VideoCampaign c, CancellationToken ct)
    {
        var pending = await _db.VideoCampaignRecipients
            .Where(r => r.CampaignId == c.Id && r.Status == VideoCampaignRecipientStatus.Pending).ToListAsync(ct);
        var risks = await RiskInputsAsync(pending.Select(r => r.LeadId).ToList(), c.InstanceName, ct);
        var high = pending.Where(r => risks.TryGetValue(r.LeadId, out var ri) && RiskOf(ri).Risk == "alto").ToList();
        _db.VideoCampaignRecipients.RemoveRange(high);
        await _db.SaveChangesAsync(ct);
        return high.Count;
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
