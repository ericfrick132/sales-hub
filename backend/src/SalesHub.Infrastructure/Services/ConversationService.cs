using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SalesHub.Core.Abstractions;
using SalesHub.Core.Domain.Entities;
using SalesHub.Core.Domain.Enums;
using SalesHub.Infrastructure.Instagram;
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
    private readonly TakeoverSignal _takeover;
    private readonly PitchEngine _pitch;
    private readonly RemarketingService _remarketing;
    private readonly VideoCampaignService _videoCampaigns;
    private readonly IntentClassifier _intents;
    private readonly MetaAdsSyncService _metaAds;
    private readonly InstagramMessengerClient _ig;
    private readonly SellerLineSender _lineSender;
    private readonly IAdminAlerter _alerter;
    private readonly ILogger<ConversationService> _log;
    private static readonly Regex NonDigit = new(@"\D", RegexOptions.Compiled);

    // Mensajes automáticos que las apps mandan por la MISMA línea de WhatsApp (OTP de
    // login "*GymHero* — tu código para entrar es…", reportes "📊 *SESIÓN - TurnosPro*",
    // recordatorios "te dejo un nuevo codigo…"). No son takeover humano.
    /// <summary>
    /// Avisos internos que manda el sistema desde la línea (lead nuevo al maestro, pase a la persona y su
    /// copia). Su eco fromMe no es de ningún lead: si el número de Eric o Mateo existe como lead de prueba,
    /// registrarlo ahí lo tomaría como mensaje manual y le pausaría el bot.
    /// </summary>
    private static readonly Regex InternalNoticeRx = new(@"^(lead nuevo en |copia \(le llego a )|necesita atencion \(", RegexOptions.Compiled);

    private static readonly Regex AppSystemMsgRx = new(
        @"(tu c[oó]digo para entrar|c[oó]digo para que ingreses|^📊 \*?SESI[OÓ]N|^\*(GymHero|TurnosPro|ArchiCloud|PlayCrew|DashFlow|UniStock|GestorZap)\*\s*—)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public ConversationService(ApplicationDbContext db, IEvolutionClient evo, ILeadAssigner assigner,
        TakeoverSignal takeover, PitchEngine pitch, RemarketingService remarketing, IntentClassifier intents,
        MetaAdsSyncService metaAds, VideoCampaignService videoCampaigns, InstagramMessengerClient ig,
        SellerLineSender lineSender, IAdminAlerter alerter, ILogger<ConversationService> log)
    {
        _ig = ig; _lineSender = lineSender; _alerter = alerter;
        _db = db; _evo = evo; _assigner = assigner; _takeover = takeover; _pitch = pitch; _remarketing = remarketing;
        _videoCampaigns = videoCampaigns;
        _intents = intents; _metaAds = metaAds; _log = log;
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
        bool FromSync = false,
        // Atribución del anuncio (contextInfo.externalAdReply de un click-to-WhatsApp):
        // id del ad, título, url y ctwa_clid. Null si el mensaje no viene de un anuncio.
        AdReferral? Ad = null,
        // Carga del historial de un teléfono escaneado con prospectos prendidos: vendedores
        // que se reparten los contactos de ese teléfono. Con esto, un número que no está en
        // el sistema se crea como PROSPECTO de remarketing asignado a uno de ellos (también
        // si el chat lo arrancamos nosotros y nunca contestaron). Null/vacío = flujo normal.
        IReadOnlyList<Guid>? ProspectOwners = null,
        // Nombre del contacto en la agenda del teléfono (pushName del chat). Es el nombre que
        // lleva el prospecto en el CRM.
        string? ContactName = null);

    /// <summary>
    /// Un mensaje del sync/importación más viejo que esto es HISTORIA (ej. el historial de un
    /// teléfono recién escaneado): queda registrado, pero no puede hacer que el bot conteste ni
    /// re-enganche — le escribiría a alguien por una charla de hace meses.
    /// </summary>
    public static readonly TimeSpan HistoryAge = TimeSpan.FromHours(2);

    private static bool IsHistory(IncomingMessage m) => m.FromSync && m.Timestamp < DateTimeOffset.UtcNow - HistoryAge;

    /// <summary>Datos del anuncio que WhatsApp adjunta al primer mensaje de un CTWA.</summary>
    public record AdReferral(string? SourceId, string? Title, string? Body, string? SourceUrl, string? CtwaClid);

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
        var createdNow = false;
        if (lead is null)
        {
            // Chat LID sin teléfono real resuelto: los dígitos del LID NO son un número.
            // Crear un lead con eso de WhatsappPhone sería basura imposible de contactar.
            if (incoming.FromJid.EndsWith("@lid", StringComparison.Ordinal) && incoming.FromPhone is null)
            {
                _log.LogInformation("Inbound LID sin teléfono resolvible en {I} — no se crea lead", incoming.InstanceName);
                return false;
            }
            // Carga del historial con prospectos prendidos: TODO chat del teléfono es un lead
            // de remarketing para llamar, con dueño ya elegido. Va antes que los otros dos
            // caminos: acá el origen lo decide quien escaneó el teléfono, no el texto.
            lead = await TryCreateProspectLeadAsync(incoming, phone, instance, ct);
            if (lead is null)
            {
                // Número desconocido: ¿es un lead de anuncio (click-to-WhatsApp)? El texto
                // pre-armado del ad trae "activar <app>". Si sí, lo creamos taggeado WhatsAppAd.
                lead = await TryCreateAdLeadAsync(incoming, phone, instance, ct);
                if (lead is not null)
                    _log.LogInformation("Lead de anuncio creado: {Lead} app={Product} tel={Phone}", lead.Id, lead.ProductKey, phone);
            }
            // Si no, lo creamos igual (WhatsAppInbound, bot muteado): TODOS los chats de
            // las líneas vinculadas tienen que quedar centralizados en Conversaciones.
            lead ??= await TryCreateInboundLeadAsync(incoming, phone, instance, ct);
            if (lead is null)
            {
                _log.LogWarning("Inbound de {Phone} por {I} descartado: no pude determinar producto", phone, incoming.InstanceName);
                return false;
            }
            createdNow = true;
            // Aviso al maestro (Eric, pedido 2026-10-12): cada lead nuevo que escribe a una línea, en el
            // momento. No para la carga del historial ni para los prospectos del celular escaneado.
            if (!incoming.FromSync && !IsHistory(incoming) && lead.Source != LeadSource.Remarketing)
                await NotifyMasterNewLeadAsync(lead, incoming, ct);
        }
        // Atribución del anuncio: si el mensaje trae externalAdReply y el lead no lo tenía, se guarda.
        if (incoming.Ad is not null && string.IsNullOrWhiteSpace(lead.AdId))
        {
            lead.AdId = incoming.Ad.SourceId;
            lead.AdTitle = Trunc(incoming.Ad.Title ?? incoming.Ad.Body, 256);
            lead.AdSourceUrl = Trunc(incoming.Ad.SourceUrl, 512);
            lead.CtwaClid = Trunc(incoming.Ad.CtwaClid, 256);
            if (lead.Source == LeadSource.WhatsAppInbound) lead.Source = LeadSource.WhatsAppAd;
        }
        // Ventana de respuesta (24 h desde el último inbound) + reabrir si estaba cerrada.
        if (!incoming.FromSync || lead.LastInboundAt is null || lead.LastInboundAt < incoming.Timestamp)
            lead.LastInboundAt = incoming.Timestamp;
        if (!incoming.FromSync) lead.ConversationClosedAt = null;

        // Tipo de mensaje según el diccionario (modo sombra: se guarda para medir, no cambia la
        // respuesta). En vivo mira nuestro último mensaje para entender respuestas cortas al guion.
        string? previousOutbound = null;
        if (!incoming.FromSync && !createdNow)
            previousOutbound = await _db.ConversationMessages.AsNoTracking()
                .Where(m => m.LeadId == lead.Id && m.Direction == MessageDirection.Outbound && m.Timestamp <= incoming.Timestamp)
                .OrderByDescending(m => m.Timestamp)
                .Select(m => m.Text)
                .FirstOrDefaultAsync(ct);
        var (intentKey, intentConfident) = (await _intents.GetMatcherAsync(ct)).ClassifyWithConfidence(incoming.Text, previousOutbound);
        // Simulación en vivo: qué haría el bot con este mensaje (NO se manda nada; se compara con lo
        // que se respondió de verdad en /diccionario). Solo en vivo, no en el replay del historial.
        var messageId = Guid.NewGuid();
        IntentReplyPlanner.Plan? plan = null;
        if (!incoming.FromSync && !IsHistory(incoming))
        {
            var sellerName = lead.SellerId is null ? null : await _db.Sellers.AsNoTracking()
                .Where(s => s.Id == lead.SellerId).Select(s => s.DisplayName).FirstOrDefaultAsync(ct);
            var productName = await _db.Products.AsNoTracking()
                .Where(p => p.ProductKey == lead.ProductKey).Select(p => p.DisplayName).FirstOrDefaultAsync(ct);
            plan = IntentReplyPlanner.Decide(intentKey, intentConfident, await _intents.GetRuleAsync(intentKey, ct),
                previousOutbound, messageId, sellerName, lead.ProductKey, productName, incoming.Text);
        }

        _db.ConversationMessages.Add(new ConversationMessage
        {
            Id = messageId,
            LeadId = lead.Id,
            SellerId = lead.SellerId,
            Direction = MessageDirection.Inbound,
            Status = MessageDeliveryStatus.Received,
            Text = incoming.Text,
            WhatsappMessageId = incoming.MessageId,
            EvolutionInstance = incoming.InstanceName,
            Timestamp = incoming.Timestamp,
            IsRead = false,
            RawJson = incoming.RawJson,
            IntentKey = intentKey,
            IntentConfident = intentConfident,
            IntentAction = plan?.Action,
            IntentSimulatedReply = plan?.Text,
            IntentWhy = plan?.Why,
        });

        // Update lead state: first reply triggers status transition.
        var isFirstReply = lead.FirstReplyAt is null;
        if (isFirstReply) lead.FirstReplyAt = incoming.Timestamp;
        if (lead.Status is LeadStatus.Sent or LeadStatus.Queued or LeadStatus.Assigned)
        {
            lead.Status = LeadStatus.Replied;
        }
        lead.UpdatedAt = DateTimeOffset.UtcNow;

        // Respuesta a un mensaje de remarketing: métrica + "coordinar la llamada" en el CRM.
        if (!incoming.FromSync && !IsHistory(incoming))
        {
            await _remarketing.OnReplyAsync(lead, incoming.Timestamp, ct, incoming.Text);
            // Respuesta a una campaña con video: pase a la persona de la app.
            await _videoCampaigns.OnReplyAsync(lead, incoming.Timestamp, incoming.Text, ct);
        }

        if (IsHistory(incoming))
        {
            // Historia: marcador "ya evaluado, sin respuesta" en la MISMA escritura que el mensaje,
            // así el agente no alcanza a tomarlo en el medio. El próximo inbound en vivo lo limpia.
            if (lead.AiSuggestedReply is null) lead.AiSuggestedReplyAt ??= DateTimeOffset.UtcNow;
        }
        else
        {
            // El lead mandó algo nuevo → la sugerencia anterior (si había) quedó
            // vieja. La limpiamos para que el ConversationAgent regenere incluyendo
            // este mensaje.
            lead.AiSuggestedReply = null;
            lead.AiSuggestedReplyAt = null;
        }

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
        // Pitch por anuncio: enrola (lead nuevo de ad) o avanza el paso (respuesta). El replay del
        // sync no dispara nada — son mensajes viejos.
        if (!incoming.FromSync)
        {
            try { await _pitch.OnInboundAsync(lead, incoming.Text, incoming.Ad, createdNow, ct); }
            catch (Exception ex) { _log.LogError(ex, "Pitch hook falló para lead {Lead}", lead.Id); }
        }
        return true;
    }

    /// <summary>
    /// Un lead que nace del sync/historial lleva la fecha de su primer mensaje, no la de hoy: si no,
    /// cargar el historial de un teléfono inflaría los "leads nuevos de hoy" y el reparto a quien
    /// arrancó de cero (sellers.leads_from_at) le daría chats de hace meses.
    /// </summary>
    private static DateTimeOffset CreatedAtFor(IncomingMessage m)
        => m.FromSync && m.Timestamp < DateTimeOffset.UtcNow ? m.Timestamp : DateTimeOffset.UtcNow;

    private static string? Trunc(string? s, int n) => string.IsNullOrEmpty(s) ? s : (s.Length > n ? s[..n] : s);

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
        if (InternalNoticeRx.IsMatch(incoming.Text)) return true; // aviso interno, no va a ningún chat de lead
        // Ya registrado (reentrega del webhook o repaso del historial): cortar antes de buscar el
        // lead, que es lo caro — cargar el historial de un teléfono repasa miles de mensajes.
        if (!string.IsNullOrWhiteSpace(incoming.MessageId)
            && await _db.ConversationMessages.AnyAsync(m => m.WhatsappMessageId == incoming.MessageId, ct))
            return true;
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

        var text = incoming.Text.Trim();

        // Replay del sync: los comandos "-"/"+" ya se consumieron en vivo — re-procesarlos
        // horas después mutearía/desmutearía el bot fuera de contexto. Va ANTES de crear nada:
        // un lead agregado al tracker y abandonado sin SaveChanges lo vuelve a crear el
        // próximo mensaje del mismo chat (leads duplicados en serie).
        if (incoming.FromSync && text is "-" or "+") return false;

        // Carga del historial con prospectos prendidos: un chat que arrancamos NOSOTROS y que
        // nunca contestaron también es un prospecto (justamente el que quedó colgado). Fuera de
        // esa carga, un chat propio que no es de ningún lead no nos interesa.
        lead ??= await TryCreateProspectLeadAsync(incoming, phone, instance, ct);
        if (lead is null) return false;

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
            // Limpiar la sugerencia vieja Y el marcador "evaluado sin respuesta": el "+" es
            // una orden explícita de que el bot retome — tiene que re-entrar al pool ya.
            lead.AiSuggestedReply = null;
            lead.AiSuggestedReplyAt = null;
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
            // Salto de cola: el próximo tick del agente lo atiende primero (sin settle ni
            // sorteo del batch) — "+" tiene que producir una respuesta YA, no "cuando toque".
            _takeover.Enqueue(lead.Id);
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
        // Historia: un mensaje nuestro viejo como "último" haría que el re-enganche lo persiga ya.
        // Cuenta como el último toque (lo escribió una persona), así no sale nada por esto.
        if (IsHistory(incoming)) lead.LastNudgeAt = DateTimeOffset.UtcNow;
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
        lead.LastInboundAt = timestamp;
        lead.ConversationClosedAt = null;
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
        // Pitch de Instagram: la respuesta avanza el paso (mismo motor que WhatsApp).
        try { await _pitch.OnInboundAsync(lead, text, null, false, ct); }
        catch (Exception ex) { _log.LogError(ex, "Pitch hook (IG) falló para lead {Lead}", lead.Id); }
        return true;
    }

    /// <summary>Un DM entrante (o eco saliente) de una cuenta propia de IG, ya parseado del webhook de Meta.</summary>
    public record InstagramDm(
        string AccountIgId,      // nuestra cuenta de IG (recipient.id del inbound / sender.id del eco)
        string ContactIgsid,     // IGSID del contacto
        string? MessageId,       // message.mid
        string Text,
        DateTimeOffset Timestamp,
        string RawJson,
        AdReferral? Ad = null);

    /// <summary>
    /// Clave de dedup de un mid de IG en conversation_messages.whatsapp_message_id (128 chars): los
    /// mid de IG son largos, así que va hasheado.
    /// </summary>
    private static string? IgMessageKey(string? mid)
    {
        if (string.IsNullOrWhiteSpace(mid)) return null;
        var hash = System.Security.Cryptography.SHA1.HashData(System.Text.Encoding.UTF8.GetBytes(mid));
        return "igm:" + Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>
    /// DM entrante a una cuenta propia de Instagram (webhook oficial, Messenger Platform). Mismo
    /// recorrido que un chat de WhatsApp de anuncio: crea/matchea el lead (por IGSID, app según la
    /// cuenta que recibió), guarda el mensaje con la "línea" <c>ig:{cuenta}</c> y dispara el pitch.
    /// La respuesta la genera el ConversationAgent y sale por la Graph API (SellerLineSender).
    /// </summary>
    public async Task<bool> HandleInstagramDmAsync(InstagramDm dm, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(dm.Text)) return false;
        var acc = _ig.FindAccount(dm.AccountIgId);
        if (acc is null)
        {
            _log.LogWarning("DM de IG para una cuenta no configurada ({Ig}) — ignorado", dm.AccountIgId);
            return false;
        }
        var extKey = IgMessageKey(dm.MessageId);
        if (extKey is not null && await _db.ConversationMessages.AnyAsync(m => m.WhatsappMessageId == extKey, ct))
            return true;

        var now = DateTimeOffset.UtcNow;
        var lead = await _db.Leads
            .Where(l => l.InstagramScopedId == dm.ContactIgsid)
            .OrderByDescending(l => l.CreatedAt)
            .FirstOrDefaultAsync(ct);
        // Contesta un DM en frío que le mandamos desde el navegador: el lead ya existe con su
        // @usuario (lo cargó la captura de Maps) pero todavía sin IGSID → se engancha a ese.
        lead ??= await LinkColdDmLeadAsync(acc, dm.ContactIgsid, ct);
        var createdNow = false;
        if (lead is null)
        {
            var product = await _db.Products.FirstOrDefaultAsync(p => p.Active && p.ProductKey == acc.ProductKey, ct);
            if (product is null)
            {
                _log.LogWarning("DM de IG a @{User}: la app {Product} no existe o está inactiva — ignorado", acc.Username, acc.ProductKey);
                return false;
            }
            var (username, name) = await _ig.GetProfileAsync(acc.IgUserId, dm.ContactIgsid, ct);
            // Pre-calificación con pase a persona: el lead queda a nombre de la responsable de la app.
            var owner = await _db.OnboardingConfigs.AsNoTracking()
                    .Where(c => c.ProductKey == product.ProductKey && c.Enabled && c.HandoffSellerId != null
                        && _db.Sellers.Any(x => x.Id == c.HandoffSellerId && x.IsActive))
                    .Select(c => c.HandoffSellerId).FirstOrDefaultAsync(ct)
                ?? await _assigner.PickOwnerAsync(product.ProductKey, now, ct);
            lead = new Lead
            {
                Id = Guid.NewGuid(),
                ProductKey = product.ProductKey,
                Source = LeadSource.InstagramDm,
                Name = Trunc(name ?? (username is null ? null : "@" + username), 256) ?? "Contacto Instagram",
                InstagramHandle = Trunc(username, 128),
                InstagramScopedId = dm.ContactIgsid,
                SellerId = owner,
                AssignedAt = owner is null ? null : now,
                Status = LeadStatus.Replied,        // arrancó escribiendo él
                FirstReplyAt = dm.Timestamp,
                // Cuenta con el bot apagado por config: entra a Conversaciones para un humano.
                BotMutedAt = acc.BotEnabled ? null : now,
                CreatedAt = now,
                UpdatedAt = now,
            };
            _db.Leads.Add(lead);
            createdNow = true;
            _log.LogInformation("Lead de DM de IG creado: {Lead} app={Product} @{User} igsid={Igsid}",
                lead.Id, lead.ProductKey, username ?? "-", dm.ContactIgsid);
        }

        if (dm.Ad is not null && string.IsNullOrWhiteSpace(lead.AdId))
        {
            lead.AdId = dm.Ad.SourceId;
            lead.AdTitle = Trunc(dm.Ad.Title ?? dm.Ad.Body, 256);
            lead.AdSourceUrl = Trunc(dm.Ad.SourceUrl, 512);
        }
        if (lead.LastInboundAt is null || lead.LastInboundAt < dm.Timestamp) lead.LastInboundAt = dm.Timestamp;
        lead.ConversationClosedAt = null;

        _db.ConversationMessages.Add(new ConversationMessage
        {
            Id = Guid.NewGuid(),
            LeadId = lead.Id,
            SellerId = lead.SellerId,
            Direction = MessageDirection.Inbound,
            Status = MessageDeliveryStatus.Received,
            Text = dm.Text,
            WhatsappMessageId = extKey,
            EvolutionInstance = InstagramMessengerClient.LineName(acc.IgUserId),
            Timestamp = dm.Timestamp,
            IsRead = false,
            RawJson = dm.RawJson,
        });

        var isFirstReply = lead.FirstReplyAt is null;
        if (isFirstReply) lead.FirstReplyAt = dm.Timestamp;
        if (lead.Status is LeadStatus.Sent or LeadStatus.Queued or LeadStatus.Assigned)
            lead.Status = LeadStatus.Replied;
        lead.UpdatedAt = now;
        // Mensaje nuevo → la sugerencia (o el "evaluado sin respuesta") quedó vieja; el agente regenera.
        lead.AiSuggestedReply = null;
        lead.AiSuggestedReplyAt = null;
        if (isFirstReply)
        {
            var pending = await _db.Outbox
                .Where(o => o.LeadId == lead.Id && o.Status == OutboxStatus.Scheduled)
                .ToListAsync(ct);
            foreach (var o in pending) o.Status = OutboxStatus.Cancelled;
        }

        await _db.SaveChangesAsync(ct);
        _log.LogInformation("DM de IG guardado: lead={Lead} cuenta=@{User}", lead.Id, acc.Username ?? acc.IgUserId);

        if (createdNow)
        {
            // Mismo aviso al maestro que un lead nuevo de WhatsApp (por el alerter: acá no hay línea de WA).
            var app = await _db.Products.AsNoTracking().Where(p => p.ProductKey == lead.ProductKey)
                .Select(p => p.DisplayName).FirstOrDefaultAsync(ct) ?? lead.ProductKey;
            var preview = dm.Text.Length > 200 ? dm.Text[..200] + "…" : dm.Text;
            try
            {
                await _alerter.AlertAsync(
                    $"lead nuevo en {app} por DM de instagram (@{acc.Username ?? acc.IgUserId}): {lead.Name}\n" +
                    $"escribio: \"{preview}\"\n" +
                    (lead.BotMutedAt is null ? "el bot ya lo esta atendiendo" : "el bot esta apagado para esta cuenta: contestale a mano"), ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogWarning(ex, "Aviso de lead nuevo de IG al maestro falló (lead {Lead})", lead.Id);
            }
        }

        try { await _pitch.OnInboundAsync(lead, dm.Text, dm.Ad, createdNow, ct); }
        catch (Exception ex) { _log.LogError(ex, "Pitch hook (DM de IG) falló para lead {Lead}", lead.Id); }
        return true;
    }

    /// <summary>
    /// Eco (is_echo) de un mensaje que salió de una cuenta propia de IG. Si es uno que mandó el
    /// sistema (bot/UI: quedó registrado como Outbound antes de mandarlo) se ignora. Si no, lo
    /// escribió una persona desde la app de Instagram: se registra en el hilo y se mutea el bot
    /// para ese lead (takeover humano, igual que en WhatsApp).
    /// </summary>
    public async Task<bool> HandleInstagramEchoAsync(InstagramDm echo, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(echo.Text)) return false;
        var extKey = IgMessageKey(echo.MessageId);
        if (extKey is not null && await _db.ConversationMessages.AnyAsync(m => m.WhatsappMessageId == extKey, ct))
            return true;
        var lead = await _db.Leads
            .Where(l => l.InstagramScopedId == echo.ContactIgsid)
            .OrderByDescending(l => l.CreatedAt)
            .FirstOrDefaultAsync(ct);
        // Eco del DM en frío que salió del navegador: engancha el IGSID al lead por @usuario.
        if (lead is null)
        {
            var acc = _ig.FindAccount(echo.AccountIgId);
            if (acc is not null) lead = await LinkColdDmLeadAsync(acc, echo.ContactIgsid, ct);
        }
        if (lead is null) return false;

        var since = DateTimeOffset.UtcNow.AddMinutes(-10);
        var text = echo.Text;
        var isOwnEcho = await _db.ConversationMessages.AnyAsync(m =>
            m.LeadId == lead.Id
            && m.Direction == MessageDirection.Outbound
            && m.Timestamp >= since
            && (m.Text == text || m.Text.Contains(text)), ct);
        if (isOwnEcho) return false;

        _db.ConversationMessages.Add(new ConversationMessage
        {
            Id = Guid.NewGuid(),
            LeadId = lead.Id,
            SellerId = lead.SellerId,
            Direction = MessageDirection.Outbound,
            Status = MessageDeliveryStatus.Sent,
            Text = text,
            WhatsappMessageId = extKey,
            EvolutionInstance = InstagramMessengerClient.LineName(echo.AccountIgId),
            Timestamp = echo.Timestamp,
            IsRead = true,
            RawJson = echo.RawJson,
        });
        lead.BotMutedAt ??= DateTimeOffset.UtcNow;
        lead.AiSuggestedReply = null;
        lead.AiSuggestedReplyAt = null;
        lead.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);
        _log.LogInformation("Takeover IG: mensaje manual desde la app de Instagram en lead {Lead} → bot muteado", lead.Id);
        return true;
    }

    /// <summary>
    /// Lead de esta app al que le mandamos un DM en frío (IgDmSentAt) y que todavía no tiene IGSID:
    /// se resuelve el @usuario del contacto por la Graph API y se le guarda el IGSID. null si no hay.
    /// </summary>
    private async Task<Lead?> LinkColdDmLeadAsync(InstagramMessagingOptions.Account acc, string igsid, CancellationToken ct)
    {
        var (username, _) = await _ig.GetProfileAsync(acc.IgUserId, igsid, ct);
        if (string.IsNullOrWhiteSpace(username)) return null;
        var norm = username.ToLower();
        var lead = await _db.Leads
            .Where(l => l.ProductKey == acc.ProductKey && l.InstagramScopedId == null && l.IgDmSentAt != null
                && l.InstagramHandle != null && l.InstagramHandle.ToLower().Replace("@", "") == norm)
            .OrderByDescending(l => l.IgDmSentAt)
            .FirstOrDefaultAsync(ct);
        if (lead is null) return null;
        lead.InstagramScopedId = igsid;
        lead.UpdatedAt = DateTimeOffset.UtcNow;
        _log.LogInformation("DM de IG enganchado al lead del DM en frío {Lead} (@{User})", lead.Id, username);
        return lead;
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

    /// <summary>
    /// Called when the UI sends a reply manually. Does NOT go through the humanized outbox.
    /// <paramref name="sellerId"/> es sólo quién apretó "enviar": la bandeja es compartida,
    /// así que cualquiera puede contestar cualquier chat y el mensaje SIEMPRE sale por la
    /// línea donde vive la conversación, nunca por la del que escribe.
    /// </summary>
    /// <param name="viaInstance">Línea forzada (iniciar conversación desde una línea elegida).</param>
    public async Task<ConversationMessage?> SendReplyAsync(Guid sellerId, Guid leadId, string text, CancellationToken ct, string? viaInstance = null)
    {
        // Charla que vive en un DM de Instagram: se contesta por la misma cuenta de IG.
        if (viaInstance is null)
        {
            var (handled, igMsg) = await TrySendInstagramReplyAsync(leadId, text, ct);
            if (handled) return igMsg;
        }
        return await SendOutboundAsync(leadId, text, (instance, phone) => _evo.SendTextAsync(instance, phone, text, ct), ct, viaInstance);
    }

    private async Task<(bool Handled, ConversationMessage? Msg)> TrySendInstagramReplyAsync(Guid leadId, string text, CancellationToken ct)
    {
        var lead = await _db.Leads.FirstOrDefaultAsync(l => l.Id == leadId, ct);
        if (lead is null || string.IsNullOrWhiteSpace(lead.InstagramScopedId)) return (false, null);
        var line = await _lineSender.ResolveForLeadAsync(lead, ct);
        if (line is not { IsInstagram: true }) return (false, null);
        var entry = new ConversationMessage
        {
            Id = Guid.NewGuid(),
            LeadId = lead.Id,
            SellerId = lead.SellerId,
            Direction = MessageDirection.Outbound,
            Status = MessageDeliveryStatus.Sent,
            Text = text,
            EvolutionInstance = line.RecordName,
            Timestamp = DateTimeOffset.UtcNow,
            IsRead = true
        };
        // Persistir ANTES de mandar: el eco (is_echo) del webhook se matchea contra esto.
        _db.ConversationMessages.Add(entry);
        lead.AiSuggestedReply = null;
        lead.AiSuggestedReplyAt = null;
        lead.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);
        if (!await _lineSender.SendToLeadAsync(line, string.Empty, text, ct))
        {
            entry.Status = MessageDeliveryStatus.Failed;
            await _db.SaveChangesAsync(ct);
        }
        return (true, entry);
    }

    /// <summary>
    /// Igual que <see cref="SendReplyAsync"/> pero con una imagen o video (y caption opcional).
    /// El chat guarda "[imagen] caption" / "[video] caption" para que se vea qué se mandó.
    /// </summary>
    public Task<ConversationMessage?> SendMediaReplyAsync(Guid sellerId, Guid leadId, byte[] content, string mimeType,
        string fileName, string? caption, CancellationToken ct)
    {
        var kind = mimeType.StartsWith("video/", StringComparison.OrdinalIgnoreCase) ? "[video]" : "[imagen]";
        var text = string.IsNullOrWhiteSpace(caption) ? kind : $"{kind} {caption.Trim()}";
        return SendOutboundAsync(leadId, text,
            (instance, phone) => _evo.SendMediaAsync(instance, phone, content, mimeType, fileName,
                string.IsNullOrWhiteSpace(caption) ? null : caption.Trim(), ct), ct);
    }

    private async Task<ConversationMessage?> SendOutboundAsync(Guid leadId, string text,
        Func<string, string, Task<bool>> send, CancellationToken ct, string? viaInstance = null)
    {
        var lead = await _db.Leads
            .Include(l => l.Seller).ThenInclude(s => s!.EvolutionInstance)
            .FirstOrDefaultAsync(l => l.Id == leadId, ct);
        if (lead is null) return null;
        if (string.IsNullOrWhiteSpace(lead.WhatsappPhone)) return null;
        if (lead.Seller is null)
            throw new InvalidOperationException("El lead no tiene línea asignada: asignale un vendedor para poder contestarle.");

        var seller = lead.Seller!;

        // Responder por la línea donde VIVE la conversación: si el último mensaje entró por
        // una línea de APP (ej. app_playcrew), contestar por la del seller sería escribirle
        // desde otro número. Si esa línea de app está conectada, se usa; si no, cae a la
        // del seller como siempre.
        string? sendInstanceName = viaInstance;
        var lastInstanceName = sendInstanceName is not null ? null : await _db.ConversationMessages
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

        var ok = await send(sendInstanceName, lead.WhatsappPhone);
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

    /// <summary>
    /// Si un número DESCONOCIDO viene de un anuncio, crea el lead taggeado como
    /// <see cref="LeadSource.WhatsAppAd"/>, con el producto del anuncio y asignado al seller de
    /// la instancia (el dueño del número que recibió el ad). "Viene de un anuncio" = trae el
    /// referral de Meta, o el mensaje CONTIENE el texto pre-armado de un anuncio sincronizado
    /// (<see cref="MetaAdsSyncService.MatchAsync"/>). Nada más: antes alcanzaba con palabras como
    /// "quiero" o "informacion", y el bot le tiraba el pitch a un cliente que escribia por otra
    /// cosa (13/10/2026). Devuelve null si no es de anuncio (no creamos nada; lo crea
    /// <see cref="TryCreateInboundLeadAsync"/> con el bot apagado).
    /// </summary>
    private async Task<Lead?> TryCreateAdLeadAsync(IncomingMessage incoming, string phone, EvolutionInstance instance, CancellationToken ct)
    {
        var hasReferral = incoming.Ad is not null;
        // El anuncio exacto (por referral o por su texto pre-armado): trae app, rubro y ángulo.
        var metaAd = await _metaAds.MatchAsync(incoming.Ad?.SourceId, incoming.Text, ct, phone);
        if (!hasReferral && metaAd is null) return null;

        // Resuelve entre las apps que atiende ESTA línea (puede ser más de una); el anuncio manda.
        var product = metaAd?.ProductKey is { } adProduct
            ? await _db.Products.FirstOrDefaultAsync(p => p.Active && p.ProductKey == adProduct, ct)
            : null;
        product ??= await DetectProductForLineAsync(incoming.Text, instance, ct);
        // Con referral: el id del anuncio puede estar asignado a un pitch → ese producto gana.
        if (hasReferral && !string.IsNullOrWhiteSpace(incoming.Ad!.SourceId))
        {
            var adId = incoming.Ad.SourceId!;
            var byPitch = await _db.Pitches.AsNoTracking()
                .Where(p => p.Active && p.AdIds.Contains(adId))
                .Select(p => p.ProductKey).FirstOrDefaultAsync(ct);
            if (byPitch is not null)
                product = await _db.Products.FirstOrDefaultAsync(p => p.Active && p.ProductKey == byPitch, ct) ?? product;
        }
        if (product is null) return null; // no sabemos de qué app → no creamos un lead mal taggeado
        var prequalOwner = IsHistory(incoming) ? null : await PrequalOwnerAsync(instance, product.ProductKey, ct);
        var guest = prequalOwner is null && !IsHistory(incoming) && IsGuestOnAppLine(instance, product);

        var lead = new Lead
        {
            Id = Guid.NewGuid(),
            ProductKey = product.ProductKey,
            Source = LeadSource.WhatsAppAd,
            AdId = metaAd?.AdId,
            AdTitle = metaAd is null ? null : Trunc(metaAd.Name, 256),
            Name = ExtractPushName(incoming.RawJson) ?? "Lead de anuncio",
            WhatsappPhone = phone,
            WhatsappValidated = true,
            SellerId = instance.SellerId ?? prequalOwner ?? await _assigner.PickOwnerAsync(product.ProductKey, CreatedAtFor(incoming), ct),
            AssignedAt = DateTimeOffset.UtcNow,
            Status = LeadStatus.Replied,        // ya escribió ellos
            FirstReplyAt = incoming.Timestamp,
            // Línea de APP: el bot atiende solo si la app tiene pre-calificación (contesta por la
            // misma línea, ver SellerLineSender.ResolveForLeadAsync); si no, la maneja un humano.
            // Historia: tampoco (sería contestarle a un anuncio de hace meses).
            // Anuncio de otra app que usa esta línea prestada: también contesta el bot (ver IsGuestOnAppLine).
            BotMutedAt = (instance.SellerId is null && prequalOwner is null && !guest) || IsHistory(incoming) ? DateTimeOffset.UtcNow : null,
            CreatedAt = CreatedAtFor(incoming),
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        _db.Leads.Add(lead);
        return lead;
    }

    /// <summary>
    /// Anuncio de OTRA app que sale con el número de una línea de app (ej. "EFCloud web" por el
    /// número de GymHero mientras EFCloud no tiene WhatsApp Business propio). Esa app no tiene
    /// guion de alta en esta línea: el bot le contesta con la IA y el playbook de SU producto,
    /// por la misma línea. Hace falta que la app tenga piloto automático y playbook cargado.
    /// </summary>
    private static bool IsGuestOnAppLine(EvolutionInstance instance, Product product) =>
        instance.SellerId is null && instance.ProductKey is not null && !instance.ListenOnly
        && !LineProductKeys(instance).Contains(product.ProductKey, StringComparer.OrdinalIgnoreCase)
        && product.AutoPilot && !string.IsNullOrWhiteSpace(product.AiSalesPlaybook);

    /// <summary>
    /// PROSPECTO de remarketing: un contacto del historial de un teléfono escaneado que todavía
    /// no está en el sistema. Nace listo para LLAMAR desde el CRM (teléfono validado, con dueño
    /// y en una etapa del tablero), nunca para que le salga un mensaje solo:
    /// - bot muteado y origen <see cref="LeadSource.Remarketing"/>, que
    ///   <see cref="OutboxEnqueueHelper"/> no encola;
    /// - <c>ManualAssignedAt</c> puesto, porque el dueño lo eligió quien escaneó el teléfono:
    ///   ni el rebalanceo ni "Reasignar todo" se lo sacan a Rosario o a Eric;
    /// - fecha de creación = la de su primer mensaje, para no inflar los "nuevos de hoy".
    /// Devuelve null si el teléfono no tiene prospectos prendidos (flujo viejo).
    /// </summary>
    private async Task<Lead?> TryCreateProspectLeadAsync(IncomingMessage incoming, string phone, EvolutionInstance instance, CancellationToken ct)
    {
        if (incoming.ProspectOwners is not { Count: > 0 } owners) return null;
        // Chat LID sin teléfono real: los dígitos del LID no son un número al que llamar.
        if (incoming.FromJid.EndsWith("@lid", StringComparison.Ordinal) && incoming.FromPhone is null) return null;

        var product = await DetectProductForLineAsync(incoming.Text, instance, ct);
        if (product is null) return null;

        // El pushName de un mensaje NUESTRO es el nuestro, no el del contacto: ahí sólo sirve
        // el nombre del chat (la agenda del teléfono).
        var name = Trunc(incoming.ContactName, 120)
            ?? (incoming.FromMe ? null : ExtractPushName(incoming.RawJson))
            ?? "Contacto WhatsApp";

        // Si nos escribió, ya respondió (etapa "Respondieron"); si el chat lo arrancamos
        // nosotros y nunca contestó, queda en "Nuevos". Las dos entran al modo llamadas.
        var lead = BuildProspect(product, name, phone, owners, CreatedAtFor(incoming),
            firstReplyAt: incoming.FromMe ? null : incoming.Timestamp);
        _db.Leads.Add(lead);
        _log.LogInformation("Prospecto de remarketing creado: {Lead} app={Product} tel={Phone} teléfono={I}",
            lead.Id, lead.ProductKey, phone, instance.InstanceName);
        return lead;
    }

    /// <summary>Qué pasó con el contacto de un chat del historial.</summary>
    public enum ProspectOutcome { None, Created, Adopted }

    /// <summary>
    /// Da de alta el contacto de un chat del historial como prospecto AUNQUE WhatsApp no haya
    /// sincronizado ni un mensaje de esa charla: para llamarlo alcanza con el número, y el
    /// dispositivo recién vinculado conoce todos sus chats mucho antes de recibir su contenido.
    /// </summary>
    /// <param name="adoptExisting">
    /// Qué hacer con un número que YA es lead. Con false no se toca (comportamiento viejo). Con
    /// true se lo pasa a remarketing: se le guarda el origen anterior como etiqueta
    /// (<c>origen:MetaLeadAd</c>), pasa a origen Remarketing, se lo asigna a quien toma los
    /// prospectos del teléfono y se le cancela lo que tuviera encolado — un lead de remarketing
    /// se trabaja LLAMANDO, no puede salirle un mensaje automático por atrás.
    /// </param>
    public async Task<ProspectOutcome> EnsureProspectAsync(string instanceName, string remoteJid, string? contactName,
        IReadOnlyList<Guid> owners, DateTimeOffset? lastActivity, CancellationToken ct, bool adoptExisting = false)
    {
        if (owners.Count == 0) return ProspectOutcome.None;
        // Chat LID: los dígitos del LID no son un número. Sin un mensaje que traiga el teléfono
        // real no hay a quién llamar — si después llega uno, lo crea el camino de mensajes.
        if (remoteJid.EndsWith("@lid", StringComparison.Ordinal)) return ProspectOutcome.None;
        var phone = ExtractPhone(remoteJid);
        if (phone is null || phone.Length < 6) return ProspectOutcome.None;

        var instance = await _db.EvolutionInstances
            .Include(i => i.Seller)
            .FirstOrDefaultAsync(i => i.InstanceName == instanceName, ct);
        if (instance is null) return ProspectOutcome.None;

        var suffix = phone.Length >= 8 ? phone[^8..] : phone;
        var existing = await MatchLeadByPhoneAsync(null, suffix, ct, instance.ProductKey)
            ?? await MatchLeadByPhoneAsync(null, suffix, ct);
        if (existing is not null)
            return adoptExisting ? await AdoptAsProspectAsync(existing, owners, phone, ct) : ProspectOutcome.None;

        var product = await ProductForInstanceAsync(instance, ct);
        if (product is null) return ProspectOutcome.None;

        // Sin mensajes no sabemos quién escribió primero: queda en "Nuevos" para llamar. La
        // fecha es la de la última actividad del chat, así no infla los "nuevos de hoy".
        var now = DateTimeOffset.UtcNow;
        var lead = BuildProspect(product, Trunc(contactName, 120) ?? "Contacto WhatsApp", phone, owners,
            createdAt: lastActivity is not null && lastActivity < now ? lastActivity.Value : now,
            firstReplyAt: null);
        _db.Leads.Add(lead);
        await _db.SaveChangesAsync(ct);
        return ProspectOutcome.Created;
    }

    /// <summary>
    /// Un lead que YA existía y apareció en el celular: pasa a remarketing para llamarlo, sin
    /// perder de dónde había venido (queda como etiqueta) y sin que le salga nada por atrás.
    /// </summary>
    private async Task<ProspectOutcome> AdoptAsProspectAsync(Lead lead, IReadOnlyList<Guid> owners, string phone, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        if (lead.Source != LeadSource.Remarketing)
        {
            var tag = $"origen:{lead.Source}";
            if (!lead.Tags.Contains(tag, StringComparer.OrdinalIgnoreCase))
                lead.Tags = lead.Tags.Append(tag).ToList();   // lista nueva: el change tracker no ve los Add in-place
            lead.Source = LeadSource.Remarketing;
        }
        lead.SellerId = PickProspectOwner(owners, phone);
        lead.AssignedAt ??= now;
        // Lo eligió quien escaneó el teléfono: ni el rebalanceo ni "Reasignar todo" se lo sacan.
        lead.ManualAssignedAt = now;
        lead.UpdatedAt = now;

        // Lo que tuviera encolado se cancela: pasa a trabajarse por llamada.
        var pending = await _db.Outbox
            .Where(o => o.LeadId == lead.Id && o.Status == OutboxStatus.Scheduled)
            .ToListAsync(ct);
        foreach (var o in pending) o.Status = OutboxStatus.Cancelled;

        await _db.SaveChangesAsync(ct);
        return ProspectOutcome.Adopted;
    }

    /// <summary>
    /// El prospecto tal como tiene que nacer para que se pueda LLAMAR y no le salga nada solo:
    /// teléfono validado, dueño fijo (ManualAssignedAt, que el rebalanceo respeta), bot muteado
    /// y origen Remarketing, que OutboxEnqueueHelper no encola.
    /// </summary>
    private static Lead BuildProspect(Product product, string name, string phone, IReadOnlyList<Guid> owners,
        DateTimeOffset createdAt, DateTimeOffset? firstReplyAt)
    {
        var now = DateTimeOffset.UtcNow;
        return new Lead
        {
            Id = Guid.NewGuid(),
            ProductKey = product.ProductKey,
            Source = LeadSource.Remarketing,
            Name = name,
            WhatsappPhone = phone,
            WhatsappValidated = true,
            SellerId = PickProspectOwner(owners, phone),
            AssignedAt = now,
            ManualAssignedAt = now,
            Status = firstReplyAt is null ? LeadStatus.Assigned : LeadStatus.Replied,
            FirstReplyAt = firstReplyAt,
            BotMutedAt = now,
            CreatedAt = createdAt,
            UpdatedAt = now,
        };
    }

    /// <summary>
    /// Reparto de los prospectos entre los vendedores elegidos. Por suma de dígitos del teléfono
    /// y no por azar ni por carga: así el reparto es estable (las dos pasadas de la importación y
    /// un re-escaneo dejan cada contacto en el mismo vendedor) y parejo.
    /// </summary>
    private static Guid PickProspectOwner(IReadOnlyList<Guid> owners, string phone)
    {
        if (owners.Count == 1) return owners[0];
        var sum = phone.Where(char.IsDigit).Sum(c => c - '0');
        return owners[sum % owners.Count];
    }

    /// <summary>
    /// "lead nuevo en TurnosPro: …" al número maestro, por la misma línea que acaba de recibir el mensaje
    /// (está viva). Best-effort: si falla (o la línea es solo-escucha) no frena el alta del lead.
    /// </summary>
    private async Task NotifyMasterNewLeadAsync(Lead lead, IncomingMessage incoming, CancellationToken ct)
    {
        try
        {
            var master = PersonHandoffService.WhatsAppNumber(
                await _db.InspirationSettings.AsNoTracking().Select(x => x.MasterPhone).FirstOrDefaultAsync(ct));
            if (master is null || master == PersonHandoffService.WhatsAppNumber(lead.WhatsappPhone)) return;
            var app = await _db.Products.AsNoTracking().Where(p => p.ProductKey == lead.ProductKey)
                .Select(p => p.DisplayName).FirstOrDefaultAsync(ct) ?? lead.ProductKey;
            var seller = lead.SellerId is null ? null : await _db.Sellers.AsNoTracking()
                .Where(s => s.Id == lead.SellerId).Select(s => s.DisplayName).FirstOrDefaultAsync(ct);
            var text = incoming.Text.Length > 200 ? incoming.Text[..200] + "…" : incoming.Text;
            var msg = $"lead nuevo en {app}: {lead.Name} (+{lead.WhatsappPhone})\n" +
                      (string.IsNullOrWhiteSpace(lead.AdTitle) ? "sin anuncio reconocido\n" : $"anuncio: {lead.AdTitle}\n") +
                      $"escribio: \"{text}\"\n" +
                      (lead.BotMutedAt is null
                          ? $"el bot ya lo esta atendiendo (pasa a {seller ?? "la persona"})"
                          : "nadie lo atiende todavia: contestale a mano");
            await _evo.SendTextAsync(incoming.InstanceName, master, msg, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex, "Aviso de lead nuevo al maestro falló (lead {Lead})", lead.Id);
        }
    }

    /// <summary>
    /// Línea de APP (sin vendedor) cuya app tiene pre-calificación con pase a persona: el bot
    /// atiende al número nuevo y el lead queda a nombre de esa persona (la responsable de la app,
    /// configurada en /onboarding-apps). Null = la línea no pre-califica.
    /// </summary>
    private async Task<Guid?> PrequalOwnerAsync(EvolutionInstance instance, string productKey, CancellationToken ct)
    {
        if (instance.SellerId is not null || instance.ProductKey is null || instance.ListenOnly) return null;
        return await _db.OnboardingConfigs.AsNoTracking()
            .Where(c => c.ProductKey == productKey && c.Enabled && c.HandoffSellerId != null
                && _db.Sellers.Any(s => s.Id == c.HandoffSellerId && s.IsActive))
            .Select(c => c.HandoffSellerId)
            .FirstOrDefaultAsync(ct);
    }

    /// <summary>
    /// Número desconocido con texto que NO es de anuncio, en una línea vinculada: se crea
    /// igual (origen WhatsAppInbound) para que TODOS los chats queden en Conversaciones.
    /// El bot arranca MUTEADO siempre, también en las líneas de app con pre-calificación: el
    /// bot solo le contesta a quien viene de un anuncio. Es un chat orgánico/soporte, lo maneja
    /// un humano salvo que lo reactiven con "+" o desde la UI. Producto: por mención en el texto → línea de
    /// app → whitelist del seller de la línea. Si no se puede determinar, no se crea.
    /// </summary>
    private async Task<Lead?> TryCreateInboundLeadAsync(IncomingMessage incoming, string phone, EvolutionInstance instance, CancellationToken ct)
    {
        var product = await DetectProductForLineAsync(incoming.Text, instance, ct);
        if (product is null && instance.Seller?.VerticalsWhitelist is { Count: > 0 } wl)
        {
            var key = wl[0];
            product = await _db.Products.FirstOrDefaultAsync(p => p.Active && p.ProductKey == key, ct);
        }
        if (product is null) return null;
        var prequalOwner = IsHistory(incoming) ? null : await PrequalOwnerAsync(instance, product.ProductKey, ct);

        var lead = new Lead
        {
            Id = Guid.NewGuid(),
            ProductKey = product.ProductKey,
            Source = LeadSource.WhatsAppInbound,
            Name = ExtractPushName(incoming.RawJson) ?? "Contacto WhatsApp",
            WhatsappPhone = phone,
            WhatsappValidated = true,
            SellerId = instance.SellerId ?? prequalOwner ?? await _assigner.PickOwnerAsync(product.ProductKey, CreatedAtFor(incoming), ct),
            AssignedAt = DateTimeOffset.UtcNow,
            Status = LeadStatus.Replied,        // arrancó escribiendo él
            FirstReplyAt = incoming.Timestamp,
            // Orgánico (no vino de un anuncio): sin bot ni cadencias.
            BotMutedAt = DateTimeOffset.UtcNow,
            CreatedAt = CreatedAtFor(incoming),
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
        var products = await _db.Products.Where(p => p.Active).ToListAsync(ct);
        return MatchProduct(products, text);
    }

    /// <summary>
    /// A qué app pertenece un chat que entra por una línea. Un mismo número suele atender
    /// varias apps, así que primero se busca entre LAS DE ESA LÍNEA (si el mensaje nombra
    /// una, esa gana), después entre todas, y recién al final cae a la app principal de la
    /// línea. El orden importa: sin el primer paso, un celu de gymhero+turnospro taggeaba
    /// como playcrew un mensaje que mencionara playcrew de pasada.
    /// </summary>
    private async Task<Product?> DetectProductForLineAsync(string text, EvolutionInstance instance, CancellationToken ct)
    {
        var products = await _db.Products.Where(p => p.Active).ToListAsync(ct);
        var lineKeys = LineProductKeys(instance);

        if (lineKeys.Count > 0)
        {
            var ofLine = products.Where(p => lineKeys.Contains(p.ProductKey, StringComparer.OrdinalIgnoreCase)).ToList();
            var hit = MatchProduct(ofLine, text);
            if (hit is not null) return hit;
        }

        return MatchProduct(products, text) ?? await ProductForInstanceAsync(instance, ct);
    }

    /// <summary>App principal + las otras que atiende el mismo número.</summary>
    private static List<string> LineProductKeys(EvolutionInstance instance)
    {
        var keys = new List<string>();
        if (!string.IsNullOrWhiteSpace(instance.ProductKey)) keys.Add(instance.ProductKey!);
        keys.AddRange(instance.ExtraProductKeys.Where(k => !string.IsNullOrWhiteSpace(k)));
        return keys.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static Product? MatchProduct(IEnumerable<Product> products, string text)
    {
        var lower = (text ?? "").ToLowerInvariant();
        var compact = lower.Replace(" ", "");
        // OJO: guardas de longitud mínima. Un producto con ProductKey vacío hace que
        // compact.Contains("") sea SIEMPRE true → matchearía primero y dejaría el lead sin
        // producto (rompía el onboarding). Exigimos key/displayname con largo real.
        return products.FirstOrDefault(p =>
            (p.ProductKey.Length >= 2 && compact.Contains(p.ProductKey.ToLowerInvariant()))
            || (p.DisplayName.Length >= 3 && lower.Contains(p.DisplayName.ToLowerInvariant())));
    }

    /// <summary>App principal de la línea (fallback), o null si es línea de seller.</summary>
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
