using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SalesHub.Core.Abstractions;
using SalesHub.Core.Domain.Entities;
using SalesHub.Core.Domain.Enums;
using SalesHub.Infrastructure.Persistence;

namespace SalesHub.Infrastructure.Services;

/// <summary>
/// Ingests inbound WhatsApp messages from Evolution webhooks and records outbound
/// messages sent by the UI. Matches the phone to an existing lead and updates lead
/// status to Replied so the vendor's inbox surfaces the conversation.
/// </summary>
public class ConversationService
{
    private readonly ApplicationDbContext _db;
    private readonly IEvolutionClient _evo;
    private readonly ILeadAssigner _assigner;
    private readonly ILogger<ConversationService> _log;
    private static readonly Regex NonDigit = new(@"\D", RegexOptions.Compiled);

    // Mensajes automáticos que las apps mandan por la MISMA línea de WhatsApp (OTP de
    // login "*GymHero* — tu código para entrar es…", reportes "📊 *SESIÓN - TurnosPro*",
    // recordatorios "te dejo un nuevo codigo…"). No son takeover humano.
    private static readonly Regex AppSystemMsgRx = new(
        @"(tu c[oó]digo para entrar|c[oó]digo para que ingreses|^📊 \*?SESI[OÓ]N|^\*(GymHero|TurnosPro|ArchiCloud|PlayCrew|UniStock|GestorZap)\*\s*—)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public ConversationService(ApplicationDbContext db, IEvolutionClient evo, ILeadAssigner assigner, ILogger<ConversationService> log)
    {
        _db = db; _evo = evo; _assigner = assigner; _log = log;
    }

    public record IncomingMessage(
        string InstanceName,
        string FromJid,
        string? FromPhone,
        string? MessageId,
        string Text,
        DateTimeOffset Timestamp,
        string RawJson,
        // Saliente/propio (self-chat o envío nuestro). El flujo de leads lo ignora; el relay de
        // transcripción SÍ lo usa (mandarte un audio a vos mismo es un caso válido).
        bool FromMe = false,
        // JID del dueño de la línea (payload.sender del webhook). Sirve para distinguir
        // self-chat real (owner == remoteJid) de un envío saliente a un chat ajeno.
        string? SenderJid = null,
        // true = viene del sync periódico contra Evolution (replay de mensajes viejos),
        // no del webhook en vivo. Cambia la semántica: nunca mutea el bot, ignora los
        // comandos "-"/"+" (ya se consumieron en vivo) y ancla el chequeo de eco al
        // timestamp del mensaje en vez de "ahora".
        bool FromSync = false);

    /// <summary>Called by the Evolution webhook on every inbound message.</summary>
    public async Task<bool> HandleIncomingAsync(IncomingMessage incoming, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(incoming.Text)) return false;
        var phone = incoming.FromPhone ?? ExtractPhone(incoming.FromJid);
        if (phone is null)
        {
            _log.LogDebug("Inbound message without resolvable phone: {Jid}", incoming.FromJid);
            return false;
        }
        // Normalizar a solo dígitos (FromPhone puede traer separadores).
        phone = NonDigit.Replace(phone, "");

        // Dedup por id de WhatsApp ANTES de crear nada. Si esto corría después de crear
        // el lead, un mensaje ya conocido retornaba temprano SIN SaveChanges y el lead
        // recién agregado quedaba colgado en el change tracker: el próximo mensaje del
        // mismo número no lo encontraba en DB y creaba OTRO — leads duplicados en serie
        // (pasó en el re-barrido del sync con chats ya ingestados por otra instancia).
        if (!string.IsNullOrWhiteSpace(incoming.MessageId)
            && await _db.ConversationMessages.AnyAsync(m => m.WhatsappMessageId == incoming.MessageId, ct))
            return true;

        // Cualquier instancia VINCULADA al hub cuenta: líneas de vendedor (Seller) y
        // líneas de APP (ProductKey, sin seller — ej. app_playcrew). Solo se descartan
        // instancias que el hub no conoce (ej. las líneas de tenants de gymhero).
        var instance = await _db.EvolutionInstances
            .Include(i => i.Seller)
            .FirstOrDefaultAsync(i => i.InstanceName == incoming.InstanceName, ct);
        if (instance is null)
        {
            _log.LogWarning("Inbound message for unknown instance {I}", incoming.InstanceName);
            return false;
        }

        // Match tolerante: los teléfonos de los leads están guardados en formatos
        // inconsistentes (con/sin +, espacios, guiones, 0 inicial, el 9 argentino),
        // así que comparamos por los últimos 8 dígitos (el número de abonado, la
        // parte estable en todos los formatos). Primero entre los leads del seller
        // de la instancia (o del producto, si es línea de app); si no hay, ampliamos
        // a cualquier lead.
        var suffix = phone.Length >= 8 ? phone[^8..] : phone;
        var lead = instance.SellerId is not null
            ? await MatchLeadByPhoneAsync(instance.SellerId, suffix, ct)
            : await MatchLeadByPhoneAsync(null, suffix, ct, instance.ProductKey);
        lead ??= await MatchLeadByPhoneAsync(null, suffix, ct);
        if (lead is null)
        {
            // Chat LID sin teléfono real resuelto: los dígitos del LID NO son un número.
            // Crear un lead con eso de WhatsappPhone sería basura imposible de contactar.
            if (incoming.FromJid.EndsWith("@lid", StringComparison.Ordinal) && incoming.FromPhone is null)
            {
                _log.LogInformation("Inbound LID sin teléfono resolvible en {I} — no se crea lead", incoming.InstanceName);
                return false;
            }
            // Número desconocido: ¿es un lead de anuncio (click-to-WhatsApp)? El texto
            // pre-armado del ad trae "activar <app>". Si sí, lo creamos taggeado WhatsAppAd.
            lead = await TryCreateAdLeadAsync(incoming, phone, instance, ct);
            if (lead is not null)
                _log.LogInformation("Lead de anuncio creado: {Lead} app={Product} tel={Phone}", lead.Id, lead.ProductKey, phone);
            // Si no, lo creamos igual (WhatsAppInbound, bot muteado): TODOS los chats de
            // las líneas vinculadas tienen que quedar centralizados en Conversaciones.
            lead ??= await TryCreateInboundLeadAsync(incoming, phone, instance, ct);
            if (lead is null)
            {
                _log.LogWarning("Inbound de {Phone} por {I} descartado: no pude determinar producto", phone, incoming.InstanceName);
                return false;
            }
        }

        _db.ConversationMessages.Add(new ConversationMessage
        {
            Id = Guid.NewGuid(),
            LeadId = lead.Id,
            SellerId = lead.SellerId,
            Direction = MessageDirection.Inbound,
            Status = MessageDeliveryStatus.Received,
            Text = incoming.Text,
            WhatsappMessageId = incoming.MessageId,
            EvolutionInstance = incoming.InstanceName,
            Timestamp = incoming.Timestamp,
            IsRead = false,
            RawJson = incoming.RawJson
        });

        // Update lead state: first reply triggers status transition.
        var isFirstReply = lead.FirstReplyAt is null;
        if (isFirstReply) lead.FirstReplyAt = incoming.Timestamp;
        if (lead.Status is LeadStatus.Sent or LeadStatus.Queued or LeadStatus.Assigned)
        {
            lead.Status = LeadStatus.Replied;
        }
        lead.UpdatedAt = DateTimeOffset.UtcNow;

        // El lead mandó algo nuevo → la sugerencia anterior (si había) quedó
        // vieja. La limpiamos para que el ConversationAgent regenere incluyendo
        // este mensaje.
        lead.AiSuggestedReply = null;
        lead.AiSuggestedReplyAt = null;

        // Cortar el drip: si el lead respondió, los siguientes steps de
        // outreach inicial ya no tienen sentido (ahora la conversación queda
        // a manos del seller). Solo cancelamos los pendientes — los que ya
        // se mandaron quedan como Sent, no se tocan.
        if (isFirstReply)
        {
            var pending = await _db.Outbox
                .Where(o => o.LeadId == lead.Id && o.Status == OutboxStatus.Scheduled)
                .ToListAsync(ct);
            foreach (var o in pending) o.Status = OutboxStatus.Cancelled;
            if (pending.Count > 0)
                _log.LogInformation("Lead {Lead} respondió — {N} steps pendientes cancelados", lead.Id, pending.Count);
        }

        await _db.SaveChangesAsync(ct);
        _log.LogInformation("Inbound msg stored: lead={Lead} text={Text}", lead.Id, incoming.Text[..Math.Min(50, incoming.Text.Length)]);
        return true;
    }

    /// <summary>
    /// Mensaje PROPIO (fromMe) en el chat de un lead: takeover humano.
    /// - "-" → mutea el bot para ese lead (deja de responder y re-enganchar).
    /// - "+" → lo reactiva.
    /// - Cualquier otro texto que NO sea eco de un envío nuestro (el bot registra
    ///   todo lo que manda como Outbound) = el humano escribió desde el celu →
    ///   mutea automáticamente y registra el mensaje en la conversación.
    /// </summary>
    public async Task<bool> HandleOwnMessageAsync(IncomingMessage incoming, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(incoming.Text)) return false;
        var phone = incoming.FromPhone ?? ExtractPhone(incoming.FromJid);
        if (phone is null) return false;
        phone = NonDigit.Replace(phone, "");
        if (phone.Length < 6) return false;

        var instance = await _db.EvolutionInstances
            .Include(i => i.Seller)
            .FirstOrDefaultAsync(i => i.InstanceName == incoming.InstanceName, ct);
        if (instance is null) return false;

        var suffix = phone.Length >= 8 ? phone[^8..] : phone;
        var lead = instance.SellerId is not null
            ? await MatchLeadByPhoneAsync(instance.SellerId, suffix, ct)
            : await MatchLeadByPhoneAsync(null, suffix, ct, instance.ProductKey);
        lead ??= await MatchLeadByPhoneAsync(null, suffix, ct);
        if (lead is null) return false; // chat que no es de un lead: no nos interesa

        var text = incoming.Text.Trim();

        // Replay del sync: los comandos "-"/"+" ya se consumieron en vivo — re-procesarlos
        // horas después mutearía/desmutearía el bot fuera de contexto.
        if (incoming.FromSync && text is "-" or "+") return false;

        if (text == "-")
        {
            lead.BotMutedAt = DateTimeOffset.UtcNow;
            lead.AiSuggestedReply = null;
            lead.AiSuggestedReplyAt = null;
            lead.UpdatedAt = DateTimeOffset.UtcNow;
            await _db.SaveChangesAsync(ct);
            _log.LogInformation("Takeover: bot MUTEADO para lead {Lead} (comando '-')", lead.Id);
            return true;
        }
        if (text == "+")
        {
            lead.BotMutedAt = null;
            lead.UpdatedAt = DateTimeOffset.UtcNow;
            // El humano llevó la charla y la devuelve: el bot tiene que SEGUIR la conversación,
            // no re-arrancar el guion de onboarding desde el intro (caso real: "+" re-mandaba
            // la cadencia entera). Sembramos el step centinela; ProcessAsync → IA libre.
            var ob = await _db.Set<LeadOnboarding>().FirstOrDefaultAsync(o => o.LeadId == lead.Id, ct);
            if (ob is null)
                _db.Add(new LeadOnboarding { LeadId = lead.Id, Step = OnboardingService.StepHumanHandoff, ContactName = lead.Name });
            else if (ob.ProvisionedAt is null)
            {
                ob.Step = OnboardingService.StepHumanHandoff;
                ob.UpdatedAt = DateTimeOffset.UtcNow;
            }
            await _db.SaveChangesAsync(ct);
            _log.LogInformation("Takeover: bot REACTIVADO para lead {Lead} (comando '+', guion en modo conversación)", lead.Id);
            return true;
        }

        // ¿Es el eco de un mensaje que mandó el propio sistema? Todo envío nuestro
        // queda registrado como Outbound — si el texto matchea uno reciente, lo ignoramos.
        // En el replay del sync "reciente" se ancla al timestamp del mensaje (no a ahora):
        // los envíos del sistema se guardan sin WhatsappMessageId, así que sin esta ventana
        // el sync los volvería a insertar duplicados.
        var anchor = incoming.FromSync ? incoming.Timestamp : DateTimeOffset.UtcNow;
        var since = anchor.AddMinutes(-10);
        var until = anchor.AddMinutes(10);
        var isOwnEcho = await _db.ConversationMessages.AnyAsync(m =>
            m.LeadId == lead.Id
            && m.Direction == MessageDirection.Outbound
            && m.Timestamp >= since && m.Timestamp <= until
            && m.Text == incoming.Text, ct);
        if (isOwnEcho) return false;

        // Dedup por message id (reentregas del webhook).
        if (!string.IsNullOrWhiteSpace(incoming.MessageId)
            && await _db.ConversationMessages.AnyAsync(m => m.WhatsappMessageId == incoming.MessageId, ct))
            return true;

        // Mensajes de SISTEMA de las apps que comparten la línea (OTP de login, reportes
        // de sesión, etc.): no son un takeover humano — se registran en el hilo pero NO
        // mutean el bot. Sin esto, cada OTP que la app manda al lead apaga el bot.
        var isAppSystemMsg = AppSystemMsgRx.IsMatch(text);

        // Mensaje manual del humano desde el celu: registrar + mutear.
        _db.ConversationMessages.Add(new ConversationMessage
        {
            Id = Guid.NewGuid(),
            LeadId = lead.Id,
            SellerId = lead.SellerId,
            Direction = MessageDirection.Outbound,
            Status = MessageDeliveryStatus.Sent,
            Text = incoming.Text,
            WhatsappMessageId = incoming.MessageId,
            EvolutionInstance = incoming.InstanceName,
            Timestamp = incoming.Timestamp,
            IsRead = true,
            RawJson = incoming.RawJson
        });
        // El replay del sync solo REGISTRA: un mensaje manual de hace horas no debe
        // mutear el bot ahora (el takeover ya pasó — o no — en vivo).
        if (!isAppSystemMsg && !incoming.FromSync)
        {
            lead.BotMutedAt ??= DateTimeOffset.UtcNow;
            lead.AiSuggestedReply = null;
            lead.AiSuggestedReplyAt = null;
        }
        lead.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);
        _log.LogInformation(
            isAppSystemMsg || incoming.FromSync
                ? "Mensaje propio registrado en lead {Lead} (sin mutear)"
                : "Takeover: mensaje manual detectado en lead {Lead} → bot muteado", lead.Id);
        return true;
    }

    /// <summary>
    /// Ingesta un DM entrante de Instagram (leído del inbox por el poller). Matchea el
    /// lead por InstagramHandle y aplica las mismas transiciones que el inbound de WhatsApp:
    /// marca Replied, corta el drip y limpia la sugerencia de IA vieja. Dedup por el
    /// item_id de IG (guardado en WhatsappMessageId con prefijo "ig:").
    /// </summary>
    public async Task<bool> HandleInstagramInboundAsync(
        string handle, string externalId, string text, DateTimeOffset timestamp, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        var h = handle.Trim().TrimStart('@');
        if (h.Length == 0) return false;

        // Dedup por id externo de IG.
        var extKey = string.IsNullOrWhiteSpace(externalId) ? null : $"ig:{externalId}";
        if (extKey is not null)
        {
            var dupe = await _db.ConversationMessages.AnyAsync(m => m.WhatsappMessageId == extKey, ct);
            if (dupe) return false;
        }

        var lead = await MatchLeadByInstagramHandleAsync(h, ct);
        if (lead is null)
        {
            _log.LogInformation("IG inbound de @{Handle} sin lead asociado", h);
            return false;
        }

        _db.ConversationMessages.Add(new ConversationMessage
        {
            Id = Guid.NewGuid(),
            LeadId = lead.Id,
            SellerId = lead.SellerId,
            Direction = MessageDirection.Inbound,
            Status = MessageDeliveryStatus.Received,
            Text = text,
            WhatsappMessageId = extKey,
            EvolutionInstance = null,
            Timestamp = timestamp,
            IsRead = false
        });

        var isFirstReply = lead.FirstReplyAt is null;
        if (isFirstReply) lead.FirstReplyAt = timestamp;
        if (lead.Status is LeadStatus.Sent or LeadStatus.Queued or LeadStatus.Assigned)
            lead.Status = LeadStatus.Replied;
        lead.UpdatedAt = DateTimeOffset.UtcNow;

        // El lead escribió → la sugerencia anterior quedó vieja; el ConversationAgent regenera.
        lead.AiSuggestedReply = null;
        lead.AiSuggestedReplyAt = null;

        if (isFirstReply)
        {
            var pending = await _db.Outbox
                .Where(o => o.LeadId == lead.Id && o.Status == OutboxStatus.Scheduled)
                .ToListAsync(ct);
            foreach (var o in pending) o.Status = OutboxStatus.Cancelled;
            if (pending.Count > 0)
                _log.LogInformation("Lead {Lead} respondió por IG — {N} steps pendientes cancelados", lead.Id, pending.Count);
        }

        await _db.SaveChangesAsync(ct);
        _log.LogInformation("IG inbound guardado: lead={Lead} @{Handle}", lead.Id, h);
        return true;
    }

    /// <summary>
    /// Busca el lead más reciente cuyo InstagramHandle matchea (case-insensitive,
    /// tolerando el @ inicial guardado de cualquier lado).
    /// </summary>
    private async Task<Lead?> MatchLeadByInstagramHandleAsync(string handle, CancellationToken ct)
    {
        var norm = handle.ToLower();
        return await _db.Leads
            .Where(l => l.InstagramHandle != null
                && l.InstagramHandle.ToLower().Replace("@", "") == norm)
            .OrderByDescending(l => l.CreatedAt)
            .FirstOrDefaultAsync(ct);
    }

    /// <summary>Called when the UI sends a reply manually. Does NOT go through the humanized outbox.</summary>
    public async Task<ConversationMessage?> SendReplyAsync(Guid sellerId, Guid leadId, string text, CancellationToken ct)
    {
        var lead = await _db.Leads
            .Include(l => l.Seller).ThenInclude(s => s!.EvolutionInstance)
            .FirstOrDefaultAsync(l => l.Id == leadId, ct);
        if (lead is null) return null;
        if (lead.SellerId != sellerId) return null;
        if (string.IsNullOrWhiteSpace(lead.WhatsappPhone)) return null;

        var seller = lead.Seller!;

        // Responder por la línea donde VIVE la conversación: si el último mensaje entró por
        // una línea de APP (ej. app_playcrew), contestar por la del seller sería escribirle
        // desde otro número. Si esa línea de app está conectada, se usa; si no, cae a la
        // del seller como siempre.
        string? sendInstanceName = null;
        var lastInstanceName = await _db.ConversationMessages
            .Where(m => m.LeadId == lead.Id && m.EvolutionInstance != null)
            .OrderByDescending(m => m.Timestamp)
            .Select(m => m.EvolutionInstance)
            .FirstOrDefaultAsync(ct);
        if (lastInstanceName is not null)
        {
            var appLine = await _db.EvolutionInstances.AsNoTracking()
                .FirstOrDefaultAsync(i => i.InstanceName == lastInstanceName
                    && i.ProductKey != null && i.Status == InstanceStatus.Connected, ct);
            sendInstanceName = appLine?.InstanceName;
        }
        if (sendInstanceName is null)
        {
            var instance = seller.EvolutionInstance;
            if (instance is null || instance.Status != InstanceStatus.Connected)
                throw new InvalidOperationException("Evolution instance no conectada");
            sendInstanceName = instance.InstanceName;
        }

        var ok = await _evo.SendTextAsync(sendInstanceName, lead.WhatsappPhone, text, ct);
        var entry = new ConversationMessage
        {
            Id = Guid.NewGuid(),
            LeadId = lead.Id,
            SellerId = seller.Id,
            Direction = MessageDirection.Outbound,
            Status = ok ? MessageDeliveryStatus.Sent : MessageDeliveryStatus.Failed,
            Text = text,
            EvolutionInstance = sendInstanceName,
            Timestamp = DateTimeOffset.UtcNow,
            IsRead = true
        };
        _db.ConversationMessages.Add(entry);
        lead.UpdatedAt = DateTimeOffset.UtcNow;
        // El vendedor respondió: la sugerencia ya cumplió su función.
        lead.AiSuggestedReply = null;
        lead.AiSuggestedReplyAt = null;
        await _db.SaveChangesAsync(ct);
        return entry;
    }

    public async Task MarkReadAsync(Guid sellerId, Guid leadId, CancellationToken ct)
    {
        var msgs = await _db.ConversationMessages
            .Where(m => m.LeadId == leadId && m.Direction == MessageDirection.Inbound && !m.IsRead)
            .ToListAsync(ct);
        foreach (var m in msgs)
        {
            m.IsRead = true;
            m.ReadAt = DateTimeOffset.UtcNow;
        }
        await _db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Busca el lead más reciente cuyo teléfono termina en <paramref name="suffix"/>.
    /// Si <paramref name="sellerId"/> no es null, restringe a los leads de ese
    /// seller. Los teléfonos de los leads están en formatos inconsistentes (con
    /// +, espacios, guiones, paréntesis); los strippeamos con string.Replace —
    /// que Npgsql sí traduce a SQL (Regex.Replace NO se traduce).
    /// </summary>
    private async Task<Lead?> MatchLeadByPhoneAsync(Guid? sellerId, string suffix, CancellationToken ct, string? productKey = null)
    {
        var q = _db.Leads.Where(l =>
            l.WhatsappPhone != null
            && l.WhatsappPhone
                .Replace(" ", "").Replace("-", "").Replace("+", "")
                .Replace("(", "").Replace(")", "").Replace(".", "")
                .EndsWith(suffix));
        if (sellerId is not null)
            q = q.Where(l => l.SellerId == sellerId);
        if (!string.IsNullOrWhiteSpace(productKey))
            q = q.Where(l => l.ProductKey == productKey);
        return await q.OrderByDescending(l => l.CreatedAt).FirstOrDefaultAsync(ct);
    }

    // Intención típica del texto pre-armado de un anuncio click-to-WhatsApp.
    private static readonly Regex AdIntentRx = new(
        @"activar|quiero|me gustar|me interesa|informaci[oó]n|empezar|comenzar|probar|sumar",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Si un número DESCONOCIDO escribe el texto pre-armado de un anuncio (ej.
    /// "me gustaría activar Gym Hero para mi gimnasio"), crea el lead taggeado como
    /// <see cref="LeadSource.WhatsAppAd"/>, con el producto detectado del texto y
    /// asignado al seller de la instancia (el dueño del número que recibió el ad).
    /// Devuelve null si no parece un lead de anuncio (no creamos nada).
    /// </summary>
    private async Task<Lead?> TryCreateAdLeadAsync(IncomingMessage incoming, string phone, EvolutionInstance instance, CancellationToken ct)
    {
        if (!AdIntentRx.IsMatch(incoming.Text)) return null;

        var product = await DetectProductAsync(incoming.Text, ct)
            // Línea de APP: si el texto no nombra la app, la línea misma ya la define.
            ?? await ProductForInstanceAsync(instance, ct);
        if (product is null) return null; // no sabemos de qué app → no creamos un lead mal taggeado

        var lead = new Lead
        {
            Id = Guid.NewGuid(),
            ProductKey = product.ProductKey,
            Source = LeadSource.WhatsAppAd,
            Name = ExtractPushName(incoming.RawJson) ?? "Lead de anuncio",
            WhatsappPhone = phone,
            WhatsappValidated = true,
            SellerId = instance.SellerId ?? await _assigner.PickOwnerAsync(product.ProductKey, ct),
            AssignedAt = DateTimeOffset.UtcNow,
            Status = LeadStatus.Replied,        // ya escribió ellos
            FirstReplyAt = incoming.Timestamp,
            // Línea de APP: el bot responde por la línea del SELLER (número distinto al que
            // le escribieron) → confundiría al cliente. Muteado hasta cablear ese envío.
            BotMutedAt = instance.SellerId is null ? DateTimeOffset.UtcNow : null,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        _db.Leads.Add(lead);
        return lead;
    }

    /// <summary>
    /// Número desconocido con texto que NO es de anuncio, en una línea vinculada: se crea
    /// igual (origen WhatsAppInbound) para que TODOS los chats queden en Conversaciones.
    /// El bot arranca MUTEADO — es un chat orgánico/soporte, lo maneja un humano salvo que
    /// lo reactiven con "+" o desde la UI. Producto: por mención en el texto → línea de
    /// app → whitelist del seller de la línea. Si no se puede determinar, no se crea.
    /// </summary>
    private async Task<Lead?> TryCreateInboundLeadAsync(IncomingMessage incoming, string phone, EvolutionInstance instance, CancellationToken ct)
    {
        var product = await DetectProductAsync(incoming.Text, ct)
            ?? await ProductForInstanceAsync(instance, ct);
        if (product is null && instance.Seller?.VerticalsWhitelist is { Count: > 0 } wl)
        {
            var key = wl[0];
            product = await _db.Products.FirstOrDefaultAsync(p => p.Active && p.ProductKey == key, ct);
        }
        if (product is null) return null;

        var lead = new Lead
        {
            Id = Guid.NewGuid(),
            ProductKey = product.ProductKey,
            Source = LeadSource.WhatsAppInbound,
            Name = ExtractPushName(incoming.RawJson) ?? "Contacto WhatsApp",
            WhatsappPhone = phone,
            WhatsappValidated = true,
            SellerId = instance.SellerId ?? await _assigner.PickOwnerAsync(product.ProductKey, ct),
            AssignedAt = DateTimeOffset.UtcNow,
            Status = LeadStatus.Replied,        // arrancó escribiendo él
            FirstReplyAt = incoming.Timestamp,
            BotMutedAt = DateTimeOffset.UtcNow, // orgánico: sin bot ni cadencias automáticas
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        _db.Leads.Add(lead);
        _log.LogInformation("Lead orgánico creado: {Lead} app={Product} tel={Phone} línea={I}",
            lead.Id, lead.ProductKey, phone, instance.InstanceName);
        return lead;
    }

    /// <summary>Producto activo mencionado en el texto (por ProductKey o DisplayName), o null.</summary>
    private async Task<Product?> DetectProductAsync(string text, CancellationToken ct)
    {
        var lower = text.ToLowerInvariant();
        var compact = lower.Replace(" ", "");
        var products = await _db.Products.Where(p => p.Active).ToListAsync(ct);
        // OJO: guardas de longitud mínima. Un producto con ProductKey vacío hace que
        // compact.Contains("") sea SIEMPRE true → matchearía primero y dejaría el lead sin
        // producto (rompía el onboarding). Exigimos key/displayname con largo real.
        return products.FirstOrDefault(p =>
            (p.ProductKey.Length >= 2 && compact.Contains(p.ProductKey.ToLowerInvariant()))
            || (p.DisplayName.Length >= 3 && lower.Contains(p.DisplayName.ToLowerInvariant())));
    }

    /// <summary>Producto de una línea de APP (instance.ProductKey), o null si es línea de seller.</summary>
    private async Task<Product?> ProductForInstanceAsync(EvolutionInstance instance, CancellationToken ct)
        => string.IsNullOrWhiteSpace(instance.ProductKey)
            ? null
            : await _db.Products.FirstOrDefaultAsync(p => p.Active && p.ProductKey == instance.ProductKey, ct);

    /// <summary>Nombre del contacto que manda Evolution en el payload (pushName), o null.</summary>
    private static string? ExtractPushName(string rawJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(rawJson);
            if (doc.RootElement.TryGetProperty("pushName", out var pn) && pn.ValueKind == JsonValueKind.String)
            {
                var name = pn.GetString();
                return string.IsNullOrWhiteSpace(name) ? null : name!.Trim();
            }
        }
        catch { /* no-op */ }
        return null;
    }

    private static string? ExtractPhone(string? jid)
    {
        if (string.IsNullOrWhiteSpace(jid)) return null;
        var at = jid.IndexOf('@');
        var raw = at > 0 ? jid[..at] : jid;
        var digits = NonDigit.Replace(raw, "");
        return string.IsNullOrWhiteSpace(digits) ? null : digits;
    }
}
