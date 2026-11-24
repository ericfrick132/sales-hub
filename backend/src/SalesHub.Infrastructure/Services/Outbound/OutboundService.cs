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
/// los mejores leads del CRM, uno por vez y espaciados, hasta un tope de contactos nuevos POR DÍA (día de
/// Argentina, <see cref="EvolutionInstance.OutboundCap"/>, 10), todos los días. Como son pocos, va a los de
/// mayor <see cref="Lead.ProspectScore"/>.
///
/// El mensaje sigue un outreach real que cerró (PlayCrew, 15/11): UN solo mensaje que nombra al negocio, se
/// presenta con nombre y marca, dice con qué rubro trabajamos y qué hacemos, y cierra pidiendo una llamada. No
/// dice "vi que toman los turnos por whatsapp" (Eric 24/11/2026: implica que están haciendo las cosas mal). Lo que sigue lo maneja una PERSONA: el bot queda muteado en
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
    /// Misma estructura que el DM de Instagram que le gustó a Eric (24/11): sin observar cómo toman los turnos hoy.
    /// </summary>
    public const string DefaultTemplate =
        "hola como va? hablo con {negocio}? soy {vendedor} de {app}. trabajamos con {rubros}: un sistema donde " +
        "{clientes} sacan el turno desde tu link a cualquier hora, {cobro} y les llega el recordatorio por whatsapp " +
        "el dia anterior. queres que te llame y te cuento mejor?";

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
    public IQueryable<Lead> Eligible(string productKey) => EligibleQuery(_db).Where(l => l.ProductKey == productKey);

    /// <summary>Horas entre el primer canal y el segundo (WhatsApp ↔ Instagram) para el mismo lead.</summary>
    public const int SecondChannelHours = 20;

    /// <summary>
    /// DM en frío por Instagram desde la cuenta de la app (@turnos.pro): los mismos analizados a fondo, mejores
    /// primero, con la cuenta de Instagram confirmada. Si ya le llegó el WhatsApp, recién 20 h después y si no
    /// contestó (los mejores van por los dos canales, escalonado).
    /// </summary>
    public static IQueryable<Lead> InstagramDmQueue(ApplicationDbContext db, string productKey, int minScore = 50)
    {
        var secondChannel = DateTimeOffset.UtcNow.AddHours(-SecondChannelHours);
        return db.Leads.Where(l => l.ProductKey == productKey
            && l.Tags.Contains(ReservedTag) && !l.Tags.Contains(ExcludedTag)
            && l.ProspectEnrichedAt != null && l.InstagramCheckedAt != null
            && l.InstagramHandle != null && l.InstagramHandle != ""
            && l.IgDmSentAt == null && l.IgDmError == null && l.InstagramScopedId == null
            && l.FirstReplyAt == null
            && (l.Status == LeadStatus.New || l.Status == LeadStatus.Assigned || l.Status == LeadStatus.Sent)
            && (l.BusinessStatus == null || l.BusinessStatus == "OPERATIONAL")
            && (l.BookingPlatform == null || !l.BookingPlatform.StartsWith("TurnosPro"))
            && l.ProspectScore >= minScore
            && (l.SentAt == null || l.SentAt < secondChannel)
            // Tomado a mano por un vendedor (que no sea el pase de la propia línea outbound): no.
            && !(l.SellerId != null && l.ManualAssignedAt != null && !l.Tags.Contains(SentTag))
            && !db.ConversationMessages.Any(m => m.LeadId == l.Id && m.Direction == MessageDirection.Inbound)
            && !db.Outbox.Any(o => o.LeadId == l.Id && (o.Status == OutboxStatus.Scheduled || o.Status == OutboxStatus.Sending)))
            .OrderByDescending(l => l.ProspectScore).ThenByDescending(l => l.TotalReviews);
    }

    /// <summary>
    /// DM de Instagram por defecto (misma estructura que el outreach que cerró, sin "hablo con…?": ya estás en su
    /// cuenta). {observacion} se arma según el lead: si ya le llegó el WhatsApp lo dice; si no, "vi su perfil".
    /// </summary>
    public const string DefaultIgTemplate =
        "hola como va? soy {vendedor} de {app}. {observacion}: un sistema donde {clientes} sacan el turno desde tu link " +
        "a cualquier hora, {cobro} y les llega el recordatorio por whatsapp el dia anterior. queres que te llame y te cuento mejor?";

    public static string IgObservation(Lead lead)
    {
        var rubro = ProspectScorer.RubroByKey(lead.ProspectRubro)?.Plural ?? "negocios como el tuyo";
        if (lead.SentAt is not null) return $"te escribi por whatsapp, por si no lo viste: trabajamos con {rubro}";
        return $"vi su perfil y justo trabajamos con {rubro}";
    }

    /// <summary>Completa los placeholders del outbound ({negocio}, {vendedor}, {app}, {rubros}, {clientes}, {cobro}, {toman}, {observacion}).</summary>
    public static string FillTemplate(string template, Lead lead, Product product, string senderName)
    {
        var rubro = ProspectScorer.RubroByKey(lead.ProspectRubro);
        var negocio = BusinessName(lead.Name);
        if (negocio is null) template = Regex.Replace(template, @"hablo con \{negocio\}\?\s*", "", RegexOptions.IgnoreCase);
        return template
            .Replace("{observacion}", IgObservation(lead))
            .Replace("{negocio}", negocio ?? "")
            .Replace("{vendedor}", senderName.Trim().ToLowerInvariant())
            .Replace("{app}", product.DisplayName)
            .Replace("{rubros}", rubro?.Plural ?? "negocios como el tuyo")
            .Replace("{clientes}", rubro?.Clientes ?? "tus clientes")
            .Replace("{cobro}", rubro?.Cobro ?? "cobras la seña si queres")
            .Replace("{toman}", rubro?.Toman ?? "toman");
    }

    /// <summary>Nombre de pila de la persona de la app (la del pase), para "soy mateo de...".</summary>
    public static async Task<string?> HandoffFirstNameAsync(ApplicationDbContext db, string productKey, CancellationToken ct)
    {
        var id = await db.OnboardingConfigs.AsNoTracking().Where(c => c.ProductKey == productKey)
            .Select(c => c.HandoffSellerId).FirstOrDefaultAsync(ct);
        if (id is null) return null;
        var name = await db.Sellers.AsNoTracking().Where(s => s.Id == id).Select(s => s.DisplayName).FirstOrDefaultAsync(ct);
        return name?.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
    }

    /// <summary>Contactables con Instagram conocido que todavía no se leyó (la extensión de la Mac los recorre).</summary>
    public static IQueryable<Lead> InstagramPendingQuery(ApplicationDbContext db) =>
        EligibleQuery(db).Where(l => l.InstagramCheckedAt == null
            && ((l.InstagramHandle != null && l.InstagramHandle != "") || (l.Website != null && l.Website.Contains("instagram.com/"))));

    /// <summary>Handle de Instagram del lead: el cargado o el de su "web" cuando es un perfil de Instagram.</summary>
    public static string? InstagramHandleOf(string? handle, string? website)
    {
        if (!string.IsNullOrWhiteSpace(handle)) return handle.Trim().TrimStart('@').TrimEnd('/');
        var m = Regex.Match(website ?? "", @"instagram\.com/([A-Za-z0-9_\.]{2,30})");
        return m.Success && m.Groups[1].Value is not ("p" or "reel" or "explore" or "whatsapp") ? m.Groups[1].Value.TrimEnd('.') : null;
    }

    public static IQueryable<Lead> EligibleQuery(ApplicationDbContext db) =>
        db.Leads.Where(l => (int)l.Source < 400
            && l.Source != LeadSource.Remarketing && l.Source != LeadSource.Competencia
            && (l.Status == LeadStatus.New || l.Status == LeadStatus.Assigned)
            && l.SentAt == null && l.FirstReplyAt == null && l.BotMutedAt == null
            && l.WhatsappPhone != null && l.WhatsappPhone != ""
            && l.ProspectScore != null
            && !l.Tags.Contains(ExcludedTag)
            && (l.BusinessStatus == null || l.BusinessStatus == "OPERATIONAL")
            && (l.BookingPlatform == null || !l.BookingPlatform.StartsWith("TurnosPro"))
            && !(l.SellerId != null && l.ManualAssignedAt != null)
            && !db.OutboundContacts.Any(c => c.LeadId == l.Id)
            // Lo único que puede tener es el DM en frío por Instagram (los mejores van por los dos canales, Eric
            // 19/11): ninguna respuesta, ningún mensaje por WhatsApp, y el DM de hace más de 20 h.
            && !db.ConversationMessages.Any(m => m.LeadId == l.Id
                && (m.Direction == MessageDirection.Inbound || !m.EvolutionInstance.StartsWith(Instagram.InstagramMessengerClient.LinePrefix)))
            && (l.IgDmSentAt == null || l.IgDmSentAt < DateTimeOffset.UtcNow.AddHours(-SecondChannelHours))
            && !db.Outbox.Any(o => o.LeadId == l.Id
                && (o.Status == OutboxStatus.Sent || o.Status == OutboxStatus.Sending || o.Status == OutboxStatus.Scheduled)));

    public IQueryable<Lead> CandidatesFor(EvolutionInstance line)
    {
        // Solo los ANALIZADOS A FONDO (Eric 19/11: "los outbounds que tienen que salir son los que haya analizado el
        // bridge"): su web leída por el worker Y su Instagram leído por el lector de la Mac. Sin Instagram leído no sale.
        var q = Eligible(line.ProductKey!).Where(l => l.ProspectScore >= line.OutboundMinScore
            && l.ProspectEnrichedAt != null && l.InstagramCheckedAt != null);
        // Solo los que publican su WhatsApp (web o Instagram): el número es de WhatsApp seguro y lo atiende el negocio.
        if (line.OutboundRequireWhatsapp)
            q = q.Where(l => l.PhoneOrigin == "whatsapp-publicado" || l.PhoneOrigin == "whatsapp-instagram");
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
        var used = await UsedTodayAsync(line.InstanceName, ct);
        var cap = line.OutboundCap;
        LineState Wait(string why, DateTimeOffset? at = null) => new(false, why, at, used, cap);

        if (cap <= 0) return Wait("No es una línea outbound");
        if (used >= cap)
            return Wait($"Ya mandó sus {cap} de hoy",
                NextWindowStart(LeadEntryService.ArDayStartUtc(LeadEntryService.TodayAr().AddDays(1))));
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

    /// <summary>Contactos nuevos que abrió la línea HOY (día de Argentina): es lo que cuenta para el tope.</summary>
    public Task<int> UsedTodayAsync(string instanceName, CancellationToken ct)
    {
        var dayStart = LeadEntryService.ArDayStartUtc(LeadEntryService.TodayAr());
        return _db.OutboundContacts.CountAsync(c => c.InstanceName == instanceName && c.CreatedAt >= dayStart, ct);
    }

    /// <summary>Todos los contactos que abrió la línea desde siempre (para mostrar).</summary>
    public Task<int> TotalAsync(string instanceName, CancellationToken ct) =>
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

        // 1) Reservar el lugar ANTES de mandar: si algo se cae en el medio, el tope del día no se pasa nunca.
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
    }

    // ── Mensaje ────────────────────────────────────────────────────────────────────────

    public string Render(Lead lead, Product product, EvolutionInstance line, string senderName)
    {
        var template = string.IsNullOrWhiteSpace(line.OutboundTemplate) ? DefaultTemplate : line.OutboundTemplate!;
        var text = FillTemplate(template, lead, product, senderName);
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
        if (contact is not null)
        {
            if (IsAutoGreeting(text, at, contact.SentAt))
            {
                _log.LogInformation("Outbound: saludo automático de {Lead} ({Sec:0}s), se espera a que conteste una persona",
                    lead.Id, (at - (contact.SentAt ?? at)).TotalSeconds);
                return;
            }
            contact.RepliedAt = at;
            await _handoff.HandoffAsync(lead, await HandoffSellerAsync(lead.ProductKey, ct), "contestó al outbound", text,
                $"Le escribimos desde la línea outbound: {contact.Message}", ct, mute: true);
            return;
        }
        // Contestó el DM en frío por Instagram de un lead del outbound: mismo pase (una sola vez).
        if (lead.IgDmSentAt is not null && lead.Tags.Contains(ReservedTag) && !lead.Tags.Contains(PersonHandoffService.Tag)
            && !IsAutoGreeting(text, at, lead.IgDmSentAt))
            await _handoff.HandoffAsync(lead, await HandoffSellerAsync(lead.ProductKey, ct), "contestó el DM de Instagram del outbound", text,
                "Le escribimos por Instagram (DM en frío) desde la cuenta de la app.", ct, mute: true);
    }

    /// <summary>Hasta acá después de nuestro mensaje, lo que llega es el saludo automático del negocio.</summary>
    internal static readonly TimeSpan AutoGreetingWindow = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Saludo automático de WhatsApp Business (o respuesta instantánea de Instagram), no una persona: llega a
    /// los 1-6 s de nuestro mensaje ("Gracias por comunicarte con…", "Bienvenida a…"), mientras que una persona
    /// tarda minutos (outbound 19-20/11: 11 automáticos entre 1 y 6 s, las 2 personas a los 11 y 17 min). No
    /// cuenta como respuesta: el contacto sigue sin contestar y el pase sale con lo primero que escriba alguien.
    /// </summary>
    internal static bool IsAutoGreeting(string? text, DateTimeOffset at, DateTimeOffset? sentAt)
        => (sentAt is { } s && at - s <= AutoGreetingWindow) || AiSuggestionService.IsAutoResponder(text);
}
