using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SalesHub.Core.Abstractions;
using SalesHub.Core.Domain.Entities;
using SalesHub.Infrastructure.Persistence;

namespace SalesHub.Infrastructure.Services;

/// <summary>
/// Calibración de voz guiada por WhatsApp, en el SELF-CHAT de la línea (cero fricción):
/// Eric manda "calibrar" a su propio chat → el bot le tira guiones de a uno; él graba
/// cada uno como nota de voz ahí mismo; el bot detecta el audio, guarda la toma apareada
/// con su guion (VoiceCalibrationTake) y manda el siguiente. Comandos: "repetir" (regrabar
/// el actual), "saltar", "listo"/"cancelar". Es acumulativo: cada sesión arranca en el
/// primer guion SIN tomas; si todos tienen, vuelve a empezar (más tomas = mejor clon).
/// Solo actúa en self-chat (fromMe y remitente == dueño de la línea) — no toca leads.
/// </summary>
public class VoiceCalibrationRelay
{
    private readonly ApplicationDbContext _db;
    private readonly IEvolutionClient _evo;
    private readonly ILogger<VoiceCalibrationRelay> _log;

    public VoiceCalibrationRelay(ApplicationDbContext db, IEvolutionClient evo, ILogger<VoiceCalibrationRelay> log)
    {
        _db = db; _evo = evo; _log = log;
    }

    private const string Prefix = "🎙️ ";
    private static readonly TimeSpan SessionTtl = TimeSpan.FromMinutes(30);

    private sealed class Session
    {
        public int Index;                      // índice en Scripts del guion actual
        public DateTimeOffset LastActivity = DateTimeOffset.UtcNow;
    }

    // Estado en memoria (el relay es scoped; la sesión sobrevive al request). La sesión se
    // keyea por NÚMERO (no por instancia): la misma línea de WhatsApp puede estar conectada
    // como varias instancias de Evolution y todas entregan los mismos mensajes.
    private static readonly ConcurrentDictionary<string, Session> Sessions = new();

    // Dedup de message-ids: Evolution re-entrega eventos y las instancias duplicadas mandan
    // el MISMO mensaje (mismo id) una vez cada una — sin esto, cada entrega re-dispara el
    // guion y se arma una tormenta de mensajes (pasó en el primer test real).
    private static readonly ConcurrentDictionary<string, DateTimeOffset> Recent = new();

    /// <summary>True si es la primera vez que vemos este message-id (y lo marca visto).</summary>
    private static bool MarkProcessed(string? messageId)
    {
        if (string.IsNullOrWhiteSpace(messageId)) return true;
        var now = DateTimeOffset.UtcNow;
        foreach (var kv in Recent)
            if (now - kv.Value > TimeSpan.FromMinutes(10)) Recent.TryRemove(kv.Key, out _);
        return Recent.TryAdd(messageId!, now);
    }

    /// <summary>
    /// Banco de guiones. Se va AMPLIANDO en cada iteración de fine-tuning — la sesión
    /// arranca en el primero sin tomas, así los nuevos se graban sin repetir los viejos.
    /// Cobertura: preguntas/coletillas, números y plata, marcas, energía alta, explicación
    /// calma con pausas, despedida, objeciones, agenda, soporte, dictado de datos.
    /// </summary>
    public static readonly (string Key, string Text)[] Scripts =
    {
        // Banco v2: fragmentos CASI TEXTUALES de los audios reales de Eric (transcripciones
        // Whisper limpiadas). Leer palabras propias evita el efecto "guion robótico" que
        // contaminaba las tomas del banco v1 (feedback del user 2026-07-07).
        ("cal-16", "Bueno, antes que nada felicidades, obviamente siempre es un emprendimiento importante. Así que nada, bienvenido al mundo del pádel. Tenés el sistema súper completo, después ahí podés vender turnos, viste, toda la parte de buffet también la soporta."),
        ("cal-17", "Tiene distintos planes, si querés arrancás con uno tranqui, como básico, y después podés ir subiendo y le agregás estas mejoras que la verdad, te digo, suman mucho al club. El bot de WhatsApp que contesta automático, reserva automático... bueno, nada, un montón de cosas."),
        ("cal-18", "Tenemos distintos planes, arrancan en treinta y cinco mil por mes, dependiendo obviamente de las funcionalidades. Y el más caro está sesenta y cinco, que te agrega algunas funcionalidades extras, como un detector de huella dactilar... los socios pueden entrar con huella, reconocimiento facial, etcétera."),
        ("cal-19", "Dale, no hay problema, cualquier cosita nos escribís. Nosotros tenemos acceso con huella dactilar, con un lector que en Mercado Libre vale ciento veinte mil pesos. Nuestro sistema está adaptado a ese lector, o sea, lo conectás y ya sale andando."),
        ("cal-20", "Decime qué horario te queda bien y hacemos una llamada. Si querés a la tarde... bueno, ahora justo está el partido a la una. Así que hoy, o si querés mañana, como prefieras. Nosotros hasta las seis estamos."),
        ("cal-21", "Escuchame, ¿mañana a qué horario te puedo llamar? Necesito que estés con la compu... y si no, te muestro por el celu también, no hay drama. Pero bueno, si estás con la compu mejor, o la tablet, viste. Avisame, decime en qué horario podés mañana."),
        ("cal-22", "Si querés pasame tu mail y ya te voy armando el club, por lo menos para ya tener eso básico configurado. Y después, nada, ahí te paso los planes y todo, así los ves. Si querés hacemos una demo rápida, te muestro más o menos cómo funciona el sistema."),
        ("cal-23", "Sí, por supuesto, te entiendo. Mirá, son muchos los que nos contactan por estos temas, así que cualquier cosa, si querés revisar un poco el sistema, si tenés alguna duda puntual, avisanos."),
        ("cal-24", "Y después tenés también la otra aplicación, para gimnasios propiamente dicho. Si querés se te puede armar un combo, que es lo que hacemos con varios que están en tu misma situación. Así que si querés armamos un combo de precio de las dos, no hay problema, yo eso lo he hecho."),
        ("cal-25", "Y lo hicieron también a pulmón, viste... empezaron con cuatro canchas y un bar, y después metieron cancha de fútbol. Pero te estoy hablando casi tres años después, o sea, nada es de la noche a la mañana. Por suerte a todos los clientes nuestros les está yendo muy bien, así que nada, espectacular."),
        ("cal-26", "¿Qué hacés? ¿Todo bien? No, ya no se paga más por mensaje, ahora es el plan. Si vas a mi suscripción, ahí ponés agregar el plan y ya está. Son diez mil pesos y son WhatsApp ilimitados."),
        ("cal-27", "Al final nos pareció hacerlo así mucho mejor, porque para tu caso tenés un montón de socios, de alumnos, entonces estaría carísimo. Entonces lo tenemos ilimitado, mandás la cantidad que quieras. Así que nada, activate eso y ya tenés mensajes."),
        ("cal-28", "Hola, ¿cómo estás? Todo bien. Fijate si no entrá de nuevo, seguro ahí te genera otro código, no hay problema. Si te crea una cuenta nueva, no hay ningún problema."),
        ("cal-29", "Escuchame otra cosa, mirá, nosotros somos una empresa que nos dedicamos a hacer sistemas de todo tipo. Tenemos uno de pádel, que es justamente PlayCrew, que se dedica a la gestión de canchas, de clases... vos podés cargar tus entrenadores, o sea los profes que tenés, los alumnos reservan la clase, reservan la cancha... bueno, nada, todo eso."),
        ("cal-30", "Nos metimos mucho con la huella digital, y la hicimos súper barata... creo que está cien lucas, ciento veinte por ahí. Y ahí tus socios entran con huella. Te paso nuestra página, y si querés pasame tu mail y te enviamos una cuenta demo con siete días gratis."),
    };

    /// <summary>True si el mensaje fue consumido por la calibración (no sigue el flujo normal).</summary>
    public async Task<bool> TryHandleAsync(ConversationService.IncomingMessage incoming, CancellationToken ct)
    {
        var text = (incoming.Text ?? string.Empty).Trim();

        // Guarda global: los ecos de NUESTROS mensajes 🎙️ (fromMe) se consumen siempre —
        // si cayeran al takeover, mutearían al bot para el lead que matchee ese número.
        if (incoming.FromMe && text.StartsWith(Prefix.Trim(), StringComparison.Ordinal)) return true;

        // Participante: en self-chat es el dueño de la línea (fromMe); si no, el remitente real.
        var selfChat = incoming.FromMe && IsSelfChat(incoming);
        if (incoming.FromMe && !selfChat) return false;
        var phone = Digits(incoming.FromPhone ?? incoming.FromJid);
        if (phone.Length < 8) return false;
        var sessionKey = phone[^8..];

        var lower = text.ToLowerInvariant();
        var isAudio = IsAudioMessage(incoming.RawJson);
        Sessions.TryGetValue(sessionKey, out var session);

        // Expiración perezosa de la sesión.
        if (session is not null && DateTimeOffset.UtcNow - session.LastActivity > SessionTtl)
        {
            Sessions.TryRemove(sessionKey, out _);
            session = null;
        }

        // Arranque: "calibrar" desde el self-chat O desde un número de la allowlist de
        // /transcripcion (números autorizados — ej. el celu personal de Eric).
        if (!isAudio && (lower == "calibrar" || lower == "calibrar voz"))
        {
            if (!selfChat && !await IsAllowedAsync(phone, ct)) return false;
            if (!MarkProcessed(incoming.MessageId)) return true; // entrega duplicada del webhook
            var startIndex = await FirstPendingIndexAsync(ct);
            session = new Session { Index = startIndex };
            Sessions[sessionKey] = session;
            var total = Scripts.Length;
            await SendAsync(incoming,
                $"Calibración de voz: te voy mandando guiones y vos grabás cada uno como nota de voz acá mismo, natural, sin producir. " +
                $"Comandos: \"repetir\" (regrabar el último), \"saltar\", \"listo\" (terminar).", ct);
            await SendScriptAsync(incoming, session.Index, total, ct);
            return true;
        }

        if (session is null) return false;

        session.LastActivity = DateTimeOffset.UtcNow;
        if (!MarkProcessed(incoming.MessageId)) return true; // entrega duplicada del webhook

        if (isAudio)
        {
            var (key, script) = Scripts[session.Index];
            // Bajar los bytes YA: los medios de WhatsApp expiran a ~3 semanas y las tomas
            // valen oro (van al pozo del Professional Voice Clone). Si falla, queda el id.
            byte[]? audio = null;
            try { audio = await _evo.GetMediaBase64Async(incoming.InstanceName, incoming.RawJson!, ct); }
            catch { /* la descarga es best-effort; el message-id queda como respaldo */ }
            _db.Set<VoiceCalibrationTake>().Add(new VoiceCalibrationTake
            {
                Id = Guid.NewGuid(),
                InstanceName = incoming.InstanceName,
                ScriptKey = key,
                ScriptText = script,
                WhatsappMessageId = incoming.MessageId,
                AudioBytes = audio,
            });
            await _db.SaveChangesAsync(ct);
            _log.LogInformation("Calibración: toma guardada {Key} (msg {Msg})", key, incoming.MessageId);

            session.Index++;
            if (session.Index >= Scripts.Length)
            {
                Sessions.TryRemove(sessionKey, out _);
                var n = await _db.Set<VoiceCalibrationTake>().CountAsync(ct);
                await SendAsync(incoming, $"✅ ¡Ese era el último! Quedaron {n} tomas guardadas en total. Cuando quieras sumar más, mandá \"calibrar\" de nuevo.", ct);
            }
            else
            {
                await SendScriptAsync(incoming, session.Index, Scripts.Length, ct);
            }
            return true;
        }

        switch (lower)
        {
            case "listo" or "cancelar" or "chau":
                Sessions.TryRemove(sessionKey, out _);
                await SendAsync(incoming, "Listo, cerré la sesión de calibración. Las tomas quedaron guardadas. 💪", ct);
                return true;
            case "repetir":
                // Volver al guion anterior: la próxima nota de voz lo regraba (la toma vieja queda,
                // el análisis se queda con la mejor).
                session.Index = Math.Max(0, session.Index - 1);
                await SendAsync(incoming, "Dale, regrabalo:", ct);
                await SendScriptAsync(incoming, session.Index, Scripts.Length, ct);
                return true;
            case "saltar":
                session.Index++;
                if (session.Index >= Scripts.Length)
                {
                    Sessions.TryRemove(sessionKey, out _);
                    await SendAsync(incoming, "✅ Listo, no quedan más guiones. Mandá \"calibrar\" cuando quieras otra pasada.", ct);
                }
                else
                {
                    await SendScriptAsync(incoming, session.Index, Scripts.Length, ct);
                }
                return true;
            default:
                // Cualquier otro texto durante la sesión: recordatorio suave, y lo consumimos
                // para que no caiga al flujo de leads/takeover.
                await SendAsync(incoming, "Estoy esperando la nota de voz del guion 👆 (o \"repetir\" / \"saltar\" / \"listo\").", ct);
                return true;
        }
    }

    /// <summary>El remitente está en la allowlist de números autorizados (/transcripcion).</summary>
    private async Task<bool> IsAllowedAsync(string phone, CancellationToken ct)
    {
        var suffix = phone[^8..];
        var phones = await _db.TranscriptionPhones.AsNoTracking().Select(p => p.Phone).ToListAsync(ct);
        return phones.Any(p =>
        {
            var d = Digits(p);
            return d.Length >= 8 && d[^8..] == suffix;
        });
    }

    /// <summary>Primer guion sin tomas; si todos tienen, arranca de nuevo en el primero.</summary>
    private async Task<int> FirstPendingIndexAsync(CancellationToken ct)
    {
        var withTakes = await _db.Set<VoiceCalibrationTake>()
            .Select(t => t.ScriptKey).Distinct().ToListAsync(ct);
        for (var i = 0; i < Scripts.Length; i++)
            if (!withTakes.Contains(Scripts[i].Key)) return i;
        return 0;
    }

    private async Task SendScriptAsync(ConversationService.IncomingMessage incoming, int index, int total, CancellationToken ct)
    {
        var (key, text) = Scripts[index];
        await SendAsync(incoming, $"Guion {index + 1}/{total} ({key}) — grabalo como nota de voz:\n\n“{text}”", ct);
    }

    private async Task SendAsync(ConversationService.IncomingMessage incoming, string text, CancellationToken ct)
    {
        var phone = Digits(incoming.FromPhone ?? incoming.FromJid);
        if (string.IsNullOrEmpty(phone)) return;
        await _evo.SendTextAsync(incoming.InstanceName, phone, Prefix + text, ct);
    }

    private static bool IsSelfChat(ConversationService.IncomingMessage incoming)
    {
        // Self-chat verdadero: el dueño de la línea (sender) y el chat (remoteJid) son el mismo número.
        var owner = Digits(incoming.SenderJid);
        var chat = Digits(incoming.FromJid);
        if (owner.Length < 8 || chat.Length < 8) return false;
        return owner[^8..] == chat[^8..];
    }

    private static bool IsAudioMessage(string? rawJson)
    {
        if (string.IsNullOrWhiteSpace(rawJson)) return false;
        try
        {
            using var doc = JsonDocument.Parse(rawJson);
            return doc.RootElement.TryGetProperty("message", out var m)
                && m.ValueKind == JsonValueKind.Object
                && m.TryGetProperty("audioMessage", out _);
        }
        catch { return false; }
    }

    private static string Digits(string? s) => new((s ?? string.Empty).TakeWhile(c => c != '@').Where(char.IsDigit).ToArray());
}
