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

    // Estado en memoria por instancia (el relay es scoped; la sesión sobrevive al request).
    private static readonly ConcurrentDictionary<string, Session> Sessions = new();

    /// <summary>
    /// Banco de guiones. Se va AMPLIANDO en cada iteración de fine-tuning — la sesión
    /// arranca en el primero sin tomas, así los nuevos se graban sin repetir los viejos.
    /// Cobertura: preguntas/coletillas, números y plata, marcas, energía alta, explicación
    /// calma con pausas, despedida, objeciones, agenda, soporte, dictado de datos.
    /// </summary>
    public static readonly (string Key, string Text)[] Scripts =
    {
        ("cal-01", "¿Qué hacés? ¿Todo bien? Che, ¿hoy cómo lo estás manejando? ¿Excel, papel, algún sistema? Cualquier cosa me escribís por acá, ¿dale?"),
        ("cal-02", "Sale treinta lucas por mes. El más completo está sesenta y cinco. Tenés siete días gratis, y son diez mil pesos de descuento si arrancás esta semana. Doscientos alumnos, quinientos, da igual."),
        ("cal-03", "La app se llama GymHero. También tenemos TurnosPro, ArchiCloud, PlayCrew y UniStock. Entrá a gymhero punto fitness y fijate. Te mando el link por WhatsApp."),
        ("cal-04", "¡Hola! ¿Qué hacés? Che, te escribo porque vi el gimnasio y me pareció buenísimo. La verdad, justo tengo algo que te puede servir un montón. Dale, escuchame dos minutos."),
        ("cal-05", "Mirá, básicamente funciona así... vos cargás tus alumnos una vez, y después la app hace todo sola. Te avisa quién debe, le manda el recordatorio... y el alumno paga desde el link. O sea, te olvidás de perseguir gente."),
        ("cal-06", "Dale, buenísimo entonces. Cualquier cosa que necesités me escribís por acá tranqui y lo vamos viendo, ¿dale? Un abrazo."),
        ("cal-07", "Sí, te entiendo, obviamente. Igual mirá, no es lo mismo, ¿eh? Justamente la diferencia está en los cobros automáticos. Probalo una semana y después me contás."),
        ("cal-08", "¿Y cuántos alumnos tenés, más o menos? ¿Cincuenta? ¿Cien? Te pregunto porque no cobramos por alumno... así que da igual la cantidad, es el mismo precio."),
        ("cal-09", "Decime qué horario te queda bien y hacemos una llamada. ¿Mañana a la mañana podés? Tipo diez, once... Como prefieras, nosotros hasta las seis estamos."),
        ("cal-10", "Listo, ya te creé la cuenta. Ahora te paso el link, entrás directo, sin formularios ni nada. Ahí adentro cargás tus servicios y tus horarios, y ya quedás andando."),
        ("cal-11", "Uy, no, esperá... me parece que te pasé mal el link. Ahí te mando el bueno. Fijate que se abre solo, no te pide contraseña ni nada. Avisame si entrás bien."),
        ("cal-12", "Nada, te decía que lo pienses tranquilo, no hay apuro. La cuenta te queda armada igual, y si la semana que viene querés arrancar, ya está todo listo."),
        ("cal-13", "El mail sería ventas arroba gymhero punto fitness. Y el teléfono es once, seis nueve tres siete, cero cero cinco cero. Anotalo y cualquier cosa me llamás."),
        ("cal-14", "¡No, ni hablar! Eso justamente es lo que la app te resuelve. ¿Sabés la cantidad de gente que perdía cuotas por eso? Un montón. Ahora no se les escapa ni una."),
        ("cal-15", "Bueno, dale, tranqui. Escuchame otra cosa... nosotros somos una empresa que hace sistemas de todo tipo, ¿viste? Así que si más adelante necesitás otra cosa, también te podemos ayudar."),
    };

    /// <summary>True si el mensaje fue consumido por la calibración (no sigue el flujo normal).</summary>
    public async Task<bool> TryHandleAsync(ConversationService.IncomingMessage incoming, CancellationToken ct)
    {
        // Solo self-chat: fromMe y el chat es el propio dueño de la línea.
        if (!incoming.FromMe || !IsSelfChat(incoming)) return false;

        var text = (incoming.Text ?? string.Empty).Trim();
        var lower = text.ToLowerInvariant();
        var isAudio = IsAudioMessage(incoming.RawJson);
        Sessions.TryGetValue(incoming.InstanceName, out var session);

        // Expiración perezosa de la sesión.
        if (session is not null && DateTimeOffset.UtcNow - session.LastActivity > SessionTtl)
        {
            Sessions.TryRemove(incoming.InstanceName, out _);
            session = null;
        }

        // Arranque: "calibrar" (con o sin sesión previa).
        if (!isAudio && (lower == "calibrar" || lower == "calibrar voz"))
        {
            var startIndex = await FirstPendingIndexAsync(ct);
            session = new Session { Index = startIndex };
            Sessions[incoming.InstanceName] = session;
            var total = Scripts.Length;
            await SendAsync(incoming,
                $"Calibración de voz: te voy mandando guiones y vos grabás cada uno como nota de voz acá mismo, natural, sin producir. " +
                $"Comandos: \"repetir\" (regrabar el último), \"saltar\", \"listo\" (terminar).", ct);
            await SendScriptAsync(incoming, session.Index, total, ct);
            return true;
        }

        if (session is null)
        {
            // Sin sesión: solo ignoramos los ecos de nuestros propios mensajes 🎙️.
            return !isAudio && text.StartsWith(Prefix.Trim(), StringComparison.Ordinal);
        }

        session.LastActivity = DateTimeOffset.UtcNow;

        // Ecos de nuestros propios guiones (fromMe con prefijo) → consumir sin procesar.
        if (!isAudio && text.StartsWith(Prefix.Trim(), StringComparison.Ordinal)) return true;

        if (isAudio)
        {
            var (key, script) = Scripts[session.Index];
            _db.Set<VoiceCalibrationTake>().Add(new VoiceCalibrationTake
            {
                Id = Guid.NewGuid(),
                InstanceName = incoming.InstanceName,
                ScriptKey = key,
                ScriptText = script,
                WhatsappMessageId = incoming.MessageId,
            });
            await _db.SaveChangesAsync(ct);
            _log.LogInformation("Calibración: toma guardada {Key} (msg {Msg})", key, incoming.MessageId);

            session.Index++;
            if (session.Index >= Scripts.Length)
            {
                Sessions.TryRemove(incoming.InstanceName, out _);
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
                Sessions.TryRemove(incoming.InstanceName, out _);
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
                    Sessions.TryRemove(incoming.InstanceName, out _);
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
