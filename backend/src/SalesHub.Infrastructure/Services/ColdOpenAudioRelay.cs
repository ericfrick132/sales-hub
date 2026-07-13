using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SalesHub.Core.Abstractions;
using SalesHub.Core.Domain.Entities;
using SalesHub.Infrastructure.Persistence;

namespace SalesHub.Infrastructure.Services;

/// <summary>
/// Colecta por WhatsApp los audios de COLD-OPEN (el pitch grabado por Eric), uno por
/// producto. Se arranca bot-iniciado con <see cref="StartAsync"/> (el bot te manda el
/// guion del primer producto) o mandando "grabar openers" desde el self-chat / un número
/// de la allowlist. Grabás cada guion como nota de voz y el bot guarda el audio en la
/// LIBRERÍA DE MEDIA de ese producto (MediaAsset) y te manda el siguiente. Comandos:
/// "repetir" / "saltar" / "listo". NO toca la cadencia — el enganche al step se hace aparte.
/// Modelado en <see cref="VoiceCalibrationRelay"/> (mismo patrón de sesión/dedup/self-chat).
/// </summary>
public class ColdOpenAudioRelay
{
    private readonly ApplicationDbContext _db;
    private readonly IEvolutionClient _evo;
    private readonly ILogger<ColdOpenAudioRelay> _log;

    public ColdOpenAudioRelay(ApplicationDbContext db, IEvolutionClient evo, ILogger<ColdOpenAudioRelay> log)
    {
        _db = db; _evo = evo; _log = log;
    }

    // Prefijo DISTINTO al de calibración (🎙️) para que los ecos de cada relay no se pisen.
    private const string Prefix = "🎤 ";
    private static readonly TimeSpan SessionTtl = TimeSpan.FromMinutes(30);

    private sealed class Session
    {
        public int Index;
        public DateTimeOffset LastActivity = DateTimeOffset.UtcNow;
    }

    private static readonly ConcurrentDictionary<string, Session> Sessions = new();
    private static readonly ConcurrentDictionary<string, DateTimeOffset> Recent = new();

    private static bool MarkProcessed(string? messageId)
    {
        if (string.IsNullOrWhiteSpace(messageId)) return true;
        var now = DateTimeOffset.UtcNow;
        foreach (var kv in Recent)
            if (now - kv.Value > TimeSpan.FromMinutes(10)) Recent.TryRemove(kv.Key, out _);
        return Recent.TryAdd(messageId!, now);
    }

    /// <summary>
    /// Guiones de cold-open, uno por producto. El ProductKey tiene que existir en Products
    /// (el audio se guarda como MediaAsset de ese producto).
    /// </summary>
    public static readonly (string ProductKey, string Label, string Text)[] Scripts =
    {
        ("gymhero", "GYMHERO (gimnasios)",
            "Che, te robo 20 segundos. Soy Eric, armo un agente de inteligencia artificial que le contesta solo por WhatsApp a los socios de tu gimnasio: horarios, precios, cómo pagar la cuota, les reserva la clase, todo sin que vos ni recepción toquen el teléfono. Contesta al toque, veinticuatro siete, con la onda de tu marca. ¿Te tiraría una mano tener eso atendiendo el WhatsApp del gimnasio? Decime y te muestro cómo queda con el tuyo."),
        ("turnospro", "TURNOSPRO (belleza / estética / consultorios)",
            "Che, te robo 20 segundos. Soy Eric, armo un agente de inteligencia artificial que te agenda los turnos solo por WhatsApp. El cliente escribe, el agente le ofrece los horarios libres, le agenda y hasta le cobra la seña, sin que vos cortes lo que estás haciendo. Trabaja veinticuatro siete y te llena la agenda mientras atendés. ¿Te interesaría que el WhatsApp agende solo? Contestame y te lo muestro andando con tu agenda."),
        ("playcrew", "PLAYCREW (pádel / tenis)",
            "Che, te robo 20 segundos. Soy Eric, armo un agente de inteligencia artificial que toma las reservas de tus canchas solo por WhatsApp. El jugador escribe, el agente le muestra los horarios libres, le reserva la cancha y le cobra la seña, sin que nadie del club esté pegado al teléfono. Cero canchas vacías por un WhatsApp sin contestar. ¿Te sumaría tenerlo en el club? Decime y te lo muestro con tus canchas."),
        ("bunker", "BUNKER (entrenadores / coaches)",
            "Che, te robo 20 segundos. Soy Eric, armé una plataforma para entrenadores que te arma los planes de entrenamiento en minutos con inteligencia artificial, y le responde las consultas a tus atletas por WhatsApp. Dejás de perder horas armando rutinas a mano y de contestar los mismos mensajes mil veces. ¿Entrenás gente y te gustaría sacarte esa carga de encima? Contestame y te muestro cómo funciona."),
        ("construction", "ARCHICLOUD / CONSTRUCTION (arquitectos / obras)",
            "Che, te robo 20 segundos. Soy Eric, armo un sistema para estudios de arquitectura que te lleva el control de las obras: presupuestos, gastos, la cuenta con el comitente, y tiene un agente de inteligencia artificial que te carga los gastos y te responde por WhatsApp desde la obra, sin abrir la compu. Dejás de perseguir números en mil planillas. ¿Manejás obras y te vendría bien tenerlo ordenado? Decime y te lo muestro."),
        ("unistock", "UNISTOCK / DASHFLOW (comercios / retail)",
            "Che, te robo 20 segundos. Soy Eric, armo un sistema de gestión para tu comercio: punto de venta con lector de barras, stock, caja y facturación con ARCA, todo en uno. Y tiene un agente de inteligencia artificial que te consulta stock y precios por WhatsApp al instante. Dejás de adivinar qué tenés y qué te falta. ¿Tenés un local y querés tenerlo todo bajo control? Contestame y te lo muestro con tu negocio."),
    };

    /// <summary>Arranque bot-iniciado: crea la sesión y manda el intro + el primer guion.</summary>
    public async Task StartAsync(string instanceName, string phone, CancellationToken ct)
    {
        phone = Digits(phone);
        if (phone.Length < 8) return;
        Sessions[phone[^8..]] = new Session { Index = 0 };
        await _evo.SendTextAsync(instanceName, phone, Prefix +
            $"Vamos a grabar los audios de cold-open, uno por app ({Scripts.Length} en total). Te mando el guion, lo grabás como NOTA DE VOZ (30-40s, natural, no leído) y me lo mandás acá mismo. " +
            "Comandos: \"repetir\" (regrabar el último), \"saltar\", \"listo\".", ct);
        await SendScriptAsync(instanceName, phone, 0, ct);
    }

    /// <summary>True si el mensaje fue consumido por este relay (no sigue el flujo normal).</summary>
    public async Task<bool> TryHandleAsync(ConversationService.IncomingMessage incoming, CancellationToken ct)
    {
        var text = (incoming.Text ?? string.Empty).Trim();

        // Eco de NUESTROS mensajes 🎤 (fromMe): consumir siempre para no mutear al bot por
        // un lead que matchee el número.
        if (incoming.FromMe && text.StartsWith(Prefix.Trim(), StringComparison.Ordinal)) return true;

        var selfChat = incoming.FromMe && IsSelfChat(incoming);
        if (incoming.FromMe && !selfChat) return false;
        var phone = Digits(incoming.FromPhone ?? incoming.FromJid);
        if (phone.Length < 8) return false;
        var sessionKey = phone[^8..];

        var lower = text.ToLowerInvariant();
        var isAudio = IsAudioMessage(incoming.RawJson);
        Sessions.TryGetValue(sessionKey, out var session);

        if (session is not null && DateTimeOffset.UtcNow - session.LastActivity > SessionTtl)
        {
            Sessions.TryRemove(sessionKey, out _);
            session = null;
        }

        // Arranque por texto (además del StartAsync bot-iniciado): self-chat o número allowlist.
        if (!isAudio && (lower == "grabar openers" || lower == "grabar audios"))
        {
            if (!selfChat && !await IsAllowedAsync(phone, ct)) return false;
            if (!MarkProcessed(incoming.MessageId)) return true;
            session = new Session { Index = 0 };
            Sessions[sessionKey] = session;
            await SendAsync(incoming,
                $"Dale, arrancamos con los audios de cold-open, uno por app ({Scripts.Length}). Grabá cada guion como nota de voz. Comandos: \"repetir\"/\"saltar\"/\"listo\".", ct);
            await SendScriptAsync(incoming.InstanceName, phone, 0, ct);
            return true;
        }

        if (session is null) return false;

        session.LastActivity = DateTimeOffset.UtcNow;
        if (!MarkProcessed(incoming.MessageId)) return true;

        if (isAudio)
        {
            byte[]? audio = null;
            try { audio = await _evo.GetMediaBase64Async(incoming.InstanceName, incoming.RawJson!, ct); }
            catch { /* best-effort */ }

            if (audio is null || audio.Length == 0)
            {
                await SendAsync(incoming, "No pude bajar ese audio 😖 mandámelo de nuevo (nota de voz).", ct);
                return true;
            }

            var (pkey, _, _) = Scripts[session.Index];
            _db.MediaAssets.Add(new MediaAsset
            {
                Id = Guid.NewGuid(),
                ProductKey = pkey,
                FileName = $"cold-open-{pkey}.ogg",
                MimeType = "audio/ogg",
                SizeBytes = audio.Length,
                Content = audio,
            });
            await _db.SaveChangesAsync(ct);
            _log.LogInformation("ColdOpen: audio guardado para {Product} ({Bytes} bytes)", pkey, audio.Length);

            session.Index++;
            if (session.Index >= Scripts.Length)
            {
                Sessions.TryRemove(sessionKey, out _);
                await SendAsync(incoming, $"✅ ¡Ese era el último! Guardé los {Scripts.Length} audios en la media de cada app. Ahora los engancho yo a la cadencia.", ct);
            }
            else
            {
                await SendScriptAsync(incoming.InstanceName, phone, session.Index, ct);
            }
            return true;
        }

        switch (lower)
        {
            case "listo" or "cancelar" or "chau":
                Sessions.TryRemove(sessionKey, out _);
                await SendAsync(incoming, "Listo, cerré. Los audios que grabaste quedaron guardados. 💪", ct);
                return true;
            case "repetir":
                session.Index = Math.Max(0, session.Index - 1);
                await SendAsync(incoming, "Dale, regrabá este:", ct);
                await SendScriptAsync(incoming.InstanceName, phone, session.Index, ct);
                return true;
            case "saltar":
                session.Index++;
                if (session.Index >= Scripts.Length)
                {
                    Sessions.TryRemove(sessionKey, out _);
                    await SendAsync(incoming, "✅ No quedan más. Mandá \"grabar openers\" cuando quieras otra pasada.", ct);
                }
                else
                {
                    await SendScriptAsync(incoming.InstanceName, phone, session.Index, ct);
                }
                return true;
            default:
                await SendAsync(incoming, "Estoy esperando la nota de voz del guion 👆 (o \"repetir\" / \"saltar\" / \"listo\").", ct);
                return true;
        }
    }

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

    private async Task SendScriptAsync(string instanceName, string phone, int index, CancellationToken ct)
    {
        var (_, label, text) = Scripts[index];
        await _evo.SendTextAsync(instanceName, phone, Prefix +
            $"Audio {index + 1}/{Scripts.Length} — {label}\nGrabalo como nota de voz:\n\n“{text}”", ct);
    }

    private async Task SendAsync(ConversationService.IncomingMessage incoming, string text, CancellationToken ct)
    {
        var phone = Digits(incoming.FromPhone ?? incoming.FromJid);
        if (string.IsNullOrEmpty(phone)) return;
        await _evo.SendTextAsync(incoming.InstanceName, phone, Prefix + text, ct);
    }

    private static bool IsSelfChat(ConversationService.IncomingMessage incoming)
    {
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
