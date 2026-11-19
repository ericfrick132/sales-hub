using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using SalesHub.Core.Abstractions;
using SalesHub.Core.Domain.Entities;
using SalesHub.Core.Domain.Enums;
using SalesHub.Infrastructure.Persistence;

namespace SalesHub.Infrastructure.Services.Outbound;

/// <summary>
/// Línea OUTBOUND (pedido de Eric 19/11/2026): se escanea el QR de un teléfono y la línea le escribe sola a
/// los mejores leads del CRM, uno por vez y espaciados, hasta un tope de contactos EN TOTAL Y PARA SIEMPRE
/// (<see cref="EvolutionInstance.OutboundCap"/>, 10). Como son tan pocos, va a los de mayor
/// <see cref="Lead.ProspectScore"/>.
///
/// El mensaje sigue un outreach real que cerró (PlayCrew, 15/11): UN solo mensaje que nombra al negocio, se
/// presenta con nombre y marca, cuenta algo cierto de cómo trabajan ("vi que toman los turnos por WhatsApp"),
/// qué hacemos, y cierra pidiendo una llamada. Lo que sigue lo maneja una PERSONA: el bot queda muteado en
/// esas charlas y, apenas contestan, se le pasa el lead a la persona de la app (pase de la pre-calificación)
/// con copia al maestro. Al que no contesta no se le escribe más: sin re-enganche, sin cadencia.
/// </summary>
public class OutboundService
{
    /// <summary>Lead traído para el outbound: el reparto automático y los DMs en frío no lo tocan.</summary>
    public const string ReservedTag = "outbound";
    /// <summary>Excluido a mano del outbound (no se le escribe aunque tenga buen puntaje).</summary>
    public const string ExcludedTag = "outbound-no";
    /// <summary>Ya recibió la apertura de una línea outbound.</summary>
    public const string SentTag = "outbound-enviado";

    /// <summary>
    /// Apertura por defecto. Placeholders: {negocio} (si el nombre de Maps no sirve, se saca "hablo con ...?"),
    /// {vendedor}, {app}, {rubros}, {clientes}, {cobro}, {toman}. Se limpia con CopyStyle (sin tildes ni ¿).
    /// </summary>
    public const string DefaultTemplate =
        "hola como va? hablo con {negocio}? soy {vendedor} de {app}. vi que {toman} los turnos por whatsapp y justo " +
        "trabajamos con {rubros} en eso: un sistema donde {clientes} sacan el turno desde tu link a cualquier hora, " +
        "{cobro} y les llega el recordatorio por whatsapp el dia anterior. queres que te llame y te cuento mejor?";

    private readonly ApplicationDbContext _db;
    private readonly IEvolutionClient _evo;
    private readonly IMessageRenderer _renderer;
    private readonly PersonHandoffService _handoff;
    private readonly IAdminAlerter _alerter;
    private readonly IConfiguration _config;
    private readonly ILogger<OutboundService> _log;

    public OutboundService(ApplicationDbContext db, IEvolutionClient evo, IMessageRenderer renderer,
        PersonHandoffService handoff, IAdminAlerter alerter, IConfiguration config, ILogger<OutboundService> log)
    {
        _db = db; _evo = evo; _renderer = renderer; _handoff = handoff; _alerter = alerter; _config = config; _log = log;
    }

    // ── Elegibles ──────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Leads a los que una línea outbound les puede escribir: fríos (scrapeados), nunca contactados por
    /// nadie, con WhatsApp, con puntaje, sin cadencia en cola, no tomados a mano por un vendedor, no
    /// excluidos, abiertos y que no sean ya clientes.
    /// </summary>
    public IQueryable<Lead> Eligible(string productKey) =>
        _db.Leads.Where(l => l.ProductKey == productKey
            && (int)l.Source < 400
            && l.Source != LeadSource.Remarketing && l.Source != LeadSource.Competencia
            && (l.Status == LeadStatus.New || l.Status == LeadStatus.Assigned)
            && l.SentAt == null && l.FirstReplyAt == null && l.BotMutedAt == null
            && l.WhatsappPhone != null && l.WhatsappPhone != ""
            && l.ProspectScore != null
            && !l.Tags.Contains(ExcludedTag)
            && (l.BusinessStatus == null || l.BusinessStatus == "OPERATIONAL")
            && (l.BookingPlatform == null || !l.BookingPlatform.StartsWith("TurnosPro"))
            && !(l.SellerId != null && l.ManualAssignedAt != null)
            && !_db.OutboundContacts.Any(c => c.LeadId == l.Id)
            && !_db.ConversationMessages.Any(m => m.LeadId == l.Id)
            && !_db.Outbox.Any(o => o.LeadId == l.Id
                && (o.Status == OutboxStatus.Sent || o.Status == OutboxStatus.Sending || o.Status == OutboxStatus.Scheduled)));

    public IQueryable<Lead> CandidatesFor(EvolutionInstance line)
    {
        var q = Eligible(line.ProductKey!).Where(l => l.ProspectScore >= line.OutboundMinScore);
        if (line.OutboundRubros is { Count: > 0 })
        {
            var rubros = line.OutboundRubros;
            q = q.Where(l => l.ProspectRubro != null && rubros.Contains(l.ProspectRubro));
        }
        return q.OrderByDescending(l => l.ProspectScore).ThenByDescending(l => l.TotalReviews).ThenBy(l => l.CreatedAt);
    }

    // ── Estado de una línea ────────────────────────────────────────────────────────────

    public sealed record LineState(bool CanSend, string Reason, DateTimeOffset? NextAt, int Used, int Cap);

    public async Task<LineState> StateAsync(EvolutionInstance line, CancellationToken ct)
    {
        var used = await UsedAsync(line.InstanceName, ct);
        var cap = line.OutboundCap;
        LineState Wait(string why, DateTimeOffset? at = null) => new(false, why, at, used, cap);

        if (cap <= 0) return Wait("No es una línea outbound");
        if (used >= cap) return Wait($"Ya mandó sus {cap}: tope de por vida alcanzado");
        if (line.OutboundPausedAt is not null) return Wait("En pausa");
        if (line.ListenOnly) return Wait("Está en solo escucha: destildalo para que pueda mandar");
        if (string.IsNullOrWhiteSpace(line.ProductKey)) return Wait("La línea no tiene app");
        if (line.Status != InstanceStatus.Connected || line.ConnectedAt is null) return Wait("Escaneá el QR para que arranque");
        if (string.IsNullOrWhiteSpace(await SenderNameAsync(line, ct))) return Wait("Falta el nombre con el que se presenta");

        var now = DateTimeOffset.UtcNow;
        var warmup = line.ConnectedAt.Value.AddMinutes(_config.GetValue("Outbound:WarmupMinutes", 10));
        if (now < warmup) return Wait("Recién vinculada: arranca en unos minutos", warmup);

        var window = NextWindowStart(now);
        if (window > now) return Wait("Fuera de horario (lun a vie 10-19, sáb 10-13)", window);

        var last = await _db.OutboundContacts.AsNoTracking()
            .Where(c => c.InstanceName == line.InstanceName)
            .OrderByDescending(c => c.CreatedAt)
            .Select(c => new { c.Id, c.CreatedAt, c.Status })
            .FirstOrDefaultAsync(ct);
        if (last is { Status: OutboundContact.Reserved } && now - last.CreatedAt < TimeSpan.FromMinutes(10))
            return Wait("Mandando…");
        if (last is not null)
        {
            var next = last.CreatedAt + GapAfter(last.Id);
            if (next > now) return Wait("Espaciando entre contactos", next);
        }
        return new LineState(true, "Lista para mandar", now, used, cap);
    }

    public Task<int> UsedAsync(string instanceName, CancellationToken ct) =>
        _db.OutboundContacts.CountAsync(c => c.InstanceName == instanceName, ct);

    /// <summary>
    /// Espera entre un contacto y el siguiente (Outbound:GapMinutes, 30 por defecto) con jitter de ±1/3. El
    /// jitter sale del id del último contacto, así es estable entre ticks (si se sorteara en cada tick,
    /// terminaría saliendo siempre en el mínimo).
    /// </summary>
    private TimeSpan GapAfter(Guid lastId)
    {
        var baseMin = Math.Max(1, _config.GetValue("Outbound:GapMinutes", 30));
        var h = (lastId.GetHashCode() & 0x7fffffff) / (double)int.MaxValue; // 0..1
        return TimeSpan.FromMinutes(baseMin * (2.0 / 3 + h * 2.0 / 3));
    }

    /// <summary>Ventana de envío en hora de Argentina: lunes a viernes 10-19, sábados 10-13.</summary>
    public static DateTimeOffset NextWindowStart(DateTimeOffset now)
    {
        var tz = LeadEntryService.ArTz;
        var local = TimeZoneInfo.ConvertTime(now, tz);
        for (var i = 0; i < 8; i++)
        {
            var day = local.Date.AddDays(i);
            var (start, end) = day.DayOfWeek switch
            {
                DayOfWeek.Sunday => (0, 0),
                DayOfWeek.Saturday => (10, 13),
                _ => (10, 19),
            };
            if (end <= start) continue;
            var s = day.AddHours(start);
            var e = day.AddHours(end);
            var cursor = i == 0 ? local.DateTime : s;
            if (cursor < s) cursor = s;
            if (cursor < e) return new DateTimeOffset(cursor, tz.GetUtcOffset(cursor)).ToUniversalTime();
        }
        return now.AddDays(1);
    }

    // ── Envío ──────────────────────────────────────────────────────────────────────────

    /// <summary>Un tick: cada línea outbound lista manda a lo sumo UN contacto nuevo.</summary>
    public async Task<int> TickAsync(CancellationToken ct)
    {
        var lines = await _db.EvolutionInstances
            .Where(i => i.SellerId == null && i.OutboundCap > 0)
            .ToListAsync(ct);
        var sent = 0;
        foreach (var line in lines)
        {
            try
            {
                if (await TrySendNextAsync(line, ct)) sent++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogError(ex, "Outbound: falló el tick de {Line}", line.InstanceName);
            }
        }
        return sent;
    }

    private async Task<bool> TrySendNextAsync(EvolutionInstance line, CancellationToken ct)
    {
        var state = await StateAsync(line, ct);
        if (!state.CanSend) return false;

        var product = await _db.Products.AsNoTracking().FirstOrDefaultAsync(p => p.ProductKey == line.ProductKey, ct);
        if (product is null) return false;

        // Hasta 5 por tick: los que no tienen WhatsApp se marcan y se sigue con el próximo.
        var candidates = await CandidatesFor(line).Take(5).ToListAsync(ct);
        if (candidates.Count == 0)
        {
            _log.LogInformation("Outbound {Line}: no hay leads con puntaje >= {Min}", line.InstanceName, line.OutboundMinScore);
            return false;
        }

        foreach (var lead in candidates)
        {
            var check = (await _evo.CheckNumbersAsync(line.InstanceName, new[] { lead.WhatsappPhone! }, ct)).FirstOrDefault();
            if (check is null || (!check.Exists && check.Jid is null))
            {
                // Evolution no contestó (la respuesta de error no trae jid): no se puede decir que no tenga.
                _log.LogWarning("Outbound {Line}: no pude verificar el WhatsApp de {Phone}, reintento en el próximo tick",
                    line.InstanceName, lead.WhatsappPhone);
                return false;
            }
            if (!check.Exists)
            {
                lead.Status = LeadStatus.NoWhatsApp;
                lead.WhatsappValidated = false;
                lead.UpdatedAt = DateTimeOffset.UtcNow;
                await _db.SaveChangesAsync(ct);
                _log.LogInformation("Outbound {Line}: {Lead} no tiene WhatsApp, sigo con el próximo", line.InstanceName, lead.Name);
                continue;
            }
            lead.WhatsappValidated = true;
            lead.WhatsappJid = check.Jid;
            await SendAsync(line, lead, product, ct);
            return true;
        }
        return false;
    }

    private async Task SendAsync(EvolutionInstance line, Lead lead, Product product, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var handoffId = await HandoffSellerAsync(product.ProductKey, ct);
        var text = CopyStyle.Clean(Render(lead, product, line, await SenderNameAsync(line, ct) ?? ""));

        // 1) Reservar el lugar ANTES de mandar: si algo se cae en el medio, el tope no se pasa nunca.
        var contact = new OutboundContact
        {
            InstanceName = line.InstanceName,
            LeadId = lead.Id,
            Score = lead.ProspectScore ?? 0,
            Message = text,
            Status = OutboundContact.Reserved,
            CreatedAt = now,
        };
        _db.OutboundContacts.Add(contact);

        // El lead pasa a la persona de la app (la que llama) y queda fuera del reparto y del bot.
        if (handoffId is not null) lead.SellerId = handoffId;
        lead.AssignedAt = now;
        lead.ManualAssignedAt = now;
        lead.BotMutedAt = now;
        lead.LastNudgeAt = now;
        if (!lead.Tags.Contains(SentTag)) lead.Tags = lead.Tags.Append(SentTag).ToList();
        lead.UpdatedAt = now;

        // 2) El outbound se registra antes de mandar: el eco fromMe del webhook llega en ~1 s y sin esto se
        // tomaría como mensaje manual.
        var msg = new ConversationMessage
        {
            Id = Guid.NewGuid(),
            LeadId = lead.Id,
            SellerId = lead.SellerId,
            Direction = MessageDirection.Outbound,
            Status = MessageDeliveryStatus.Sent,
            Text = text,
            EvolutionInstance = line.InstanceName,
            Timestamp = now,
            IsRead = true,
        };
        _db.ConversationMessages.Add(msg);
        await _db.SaveChangesAsync(ct);

        try
        {
            // "escribiendo…" proporcional al largo (Evolution espera ese tiempo antes de volver).
            var typing = Math.Clamp(text.Length / 12, 6, 20);
            await _evo.SetPresenceTypingAsync(line.InstanceName, $"{lead.WhatsappPhone}@s.whatsapp.net", typing, ct);
            var ok = await _evo.SendTextAsync(line.InstanceName, lead.WhatsappPhone!, text, ct);
            if (!ok) throw new InvalidOperationException("Evolution rechazó el envío");

            contact.Status = OutboundContact.Sent;
            contact.SentAt = DateTimeOffset.UtcNow;
            lead.Status = LeadStatus.Sent;
            lead.SentAt = contact.SentAt;
            lead.RenderedMessage = text;
            msg.Timestamp = contact.SentAt.Value;
            _log.LogInformation("Outbound {Line}: le escribí a {Lead} (puntaje {Score})", line.InstanceName, lead.Name, contact.Score);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Pudo haber salido igual: el lugar queda gastado (cuenta para el tope) y no se reintenta.
            _log.LogWarning(ex, "Outbound {Line}: falló el envío a {Lead}", line.InstanceName, lead.Name);
            contact.Status = OutboundContact.Failed;
            contact.Error = ex.Message.Length > 500 ? ex.Message[..500] : ex.Message;
            msg.Status = MessageDeliveryStatus.Failed;
        }
        await _db.SaveChangesAsync(ct);

        var used = await UsedAsync(line.InstanceName, ct);
        if (used >= line.OutboundCap)
            await _alerter.AlertAsync($"outbound: la linea {line.Label ?? line.InstanceName} ya mando sus {line.OutboundCap} mensajes de por vida. Para seguir, agrega otro telefono en Leads > Linea outbound.", ct);
    }

    // ── Mensaje ────────────────────────────────────────────────────────────────────────

    public string Render(Lead lead, Product product, EvolutionInstance line, string senderName)
    {
        var rubro = ProspectScorer.RubroByKey(lead.ProspectRubro);
        var template = string.IsNullOrWhiteSpace(line.OutboundTemplate) ? DefaultTemplate : line.OutboundTemplate!;
        var negocio = BusinessName(lead.Name);
        if (negocio is null) template = Regex.Replace(template, @"hablo con \{negocio\}\?\s*", "", RegexOptions.IgnoreCase);

        var text = template
            .Replace("{negocio}", negocio ?? "")
            .Replace("{vendedor}", senderName.Trim().ToLowerInvariant())
            .Replace("{app}", product.DisplayName)
            .Replace("{rubros}", rubro?.Plural ?? "negocios como el tuyo")
            .Replace("{clientes}", rubro?.Clientes ?? "tus clientes")
            .Replace("{cobro}", rubro?.Cobro ?? "cobras la seña si queres")
            .Replace("{toman}", rubro?.Toman ?? "toman");
        // Spin-text {a|b} y el resto de los placeholders de siempre ({saludo}, {nombre}...).
        return _renderer.RenderTemplate(text, lead, product);
    }

    /// <summary>
    /// Nombre del negocio usable en "hablo con X?": el de Maps suele venir con todo el SEO adentro
    /// ("Nails Studio Palermo | Uñas esculpidas - Kapping"). Se corta en el primer separador y, si queda
    /// largo o raro, se devuelve null (el mensaje sale sin esa pregunta).
    /// </summary>
    public static string? BusinessName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var n = Regex.Split(name, @"\s[|\-–—·•:/]\s|\s?[|•·]\s?|\(|,")[0].Trim();
        n = Regex.Replace(n, @"[®™]", "").Trim();
        // "Psicóloga Diana Usach" / "Lic. Juan Pérez" → "Diana Usach": se le habla a la persona, no al título.
        n = Regex.Replace(n, @"^(psic[oó]log[oa]s?|lic\.?|licenciad[oa]|dra?\.?|doctor[a]?)\s+", "", RegexOptions.IgnoreCase).Trim();
        if (n.Length < 3 || n.Length > 32) return null;
        if (n.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length > 5) return null;
        // Todo en mayúsculas grita: se pasa a "Título".
        if (n.Any(char.IsLetter) && n.Where(char.IsLetter).All(char.IsUpper) && n.Length > 4)
            n = System.Globalization.CultureInfo.GetCultureInfo("es-AR").TextInfo.ToTitleCase(n.ToLowerInvariant());
        return n;
    }

    /// <summary>Con qué nombre se presenta: el de la línea, o el de pila de la persona de la app.</summary>
    public async Task<string?> SenderNameAsync(EvolutionInstance line, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(line.OutboundSenderName)) return line.OutboundSenderName!.Trim();
        var id = await HandoffSellerAsync(line.ProductKey, ct);
        if (id is null) return null;
        var name = await _db.Sellers.AsNoTracking().Where(s => s.Id == id).Select(s => s.DisplayName).FirstOrDefaultAsync(ct);
        return name?.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
    }

    /// <summary>La persona que sigue las charlas de la app (pase de la pre-calificación, /onboarding-apps).</summary>
    public Task<Guid?> HandoffSellerAsync(string? productKey, CancellationToken ct) =>
        _db.OnboardingConfigs.AsNoTracking()
            .Where(c => c.ProductKey == productKey)
            .Select(c => c.HandoffSellerId)
            .FirstOrDefaultAsync(ct);

    // ── Respuesta ──────────────────────────────────────────────────────────────────────

    /// <summary>
    /// El lead contestó la apertura: se le pasa a la persona de la app por la misma línea, con copia al
    /// maestro, para que siga ella (como en el outreach que cerró). No guarda: lo persiste quien llama.
    /// </summary>
    public async Task OnReplyAsync(Lead lead, DateTimeOffset at, string? text, CancellationToken ct)
    {
        var contact = await _db.OutboundContacts
            .FirstOrDefaultAsync(c => c.LeadId == lead.Id && c.RepliedAt == null && c.Status != OutboundContact.Reserved, ct);
        if (contact is null) return;
        contact.RepliedAt = at;
        var handoffId = await HandoffSellerAsync(lead.ProductKey, ct);
        await _handoff.HandoffAsync(lead, handoffId, "contestó al outbound", text,
            $"Le escribimos desde la línea outbound: {contact.Message}", ct, mute: true);
    }
}
