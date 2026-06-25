using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SalesHub.Core.Domain.Entities;
using SalesHub.Core.Domain.Enums;
using SalesHub.Infrastructure.Persistence;

namespace SalesHub.Infrastructure.Services;

/// <summary>Resultado de procesar un turno de onboarding.</summary>
public record OnboardingResult(string? Reply, bool OffScript, bool Provisioned);

/// <summary>
/// Onboarding scripteado de los leads de anuncio de GymHero — réplica fiel del chatbot de
/// n8n. 5 pasos: alta → nombre del gym → socios → cobros → email → crea la cuenta (bot-register)
/// y manda el link + descuento. Si el lead se sale del guion (pregunta precio/info/etc.), lo
/// marca OffScript para que la IA conteste sin avanzar el paso. El separador [NUEVO_MENSAJE]
/// lo splittea el sender en varios mensajes (como n8n).
/// </summary>
public class GymHeroOnboardingService
{
    private readonly ApplicationDbContext _db;
    private readonly IGymHeroProvisionClient _provision;
    private readonly ILogger<GymHeroOnboardingService> _log;

    public GymHeroOnboardingService(ApplicationDbContext db, IGymHeroProvisionClient provision, ILogger<GymHeroOnboardingService> log)
    {
        _db = db; _provision = provision; _log = log;
    }

    private static readonly Regex KeywordRx = new(
        @"\b(precio|costo|cuesta|cu[aá]nto sale|cu[aá]nto vale|valor|tarifa|presupuesto|info|informaci[oó]n|qu[eé] es|c[oó]mo funciona|para qu[eé] sirve|qu[eé] hace|demo|prueba|gratis|trial|probar|funcion(a|es)|features|caracter[ií]sticas|qu[eé] incluye)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex EmailRx = new(@"[^\s@]+@[^\s@]+\.[^\s@]+", RegexOptions.Compiled);

    public async Task<OnboardingResult> ProcessAsync(Lead lead, string lastMessage, CancellationToken ct)
    {
        var ob = await _db.Set<LeadOnboarding>().FirstOrDefaultAsync(o => o.LeadId == lead.Id, ct);
        if (ob is null)
        {
            ob = new LeadOnboarding { LeadId = lead.Id, Step = 0, ContactName = lead.Name };
            _db.Add(ob);
        }

        var msg = (lastMessage ?? string.Empty).Trim();
        var lower = msg.ToLowerInvariant();

        // Off-script: del paso 1 en adelante, si pregunta precio/info/etc. → la IA responde, no avanza.
        if (ob.Step >= 1 && ob.Step <= 4 && KeywordRx.IsMatch(lower))
            return new OnboardingResult(null, OffScript: true, Provisioned: false);

        string reply;
        switch (ob.Step)
        {
            case 0:
                reply = "buenas! soy Eric de GymHero." +
                    "[NUEVO_MENSAJE]Te hago 3 preguntas rapidas para conocer tu centro deportivo y ayudarte mejor" +
                    "[NUEVO_MENSAJE]Pregunta 1/3: cómo se llama tu gimnasio o centro deportivo?";
                ob.Step = 1;
                break;

            case 1:
                if (IsGymSuspicious(msg) && ob.GymRetries < 1)
                {
                    reply = "jaja dale, en serio: ¿cómo se llama tu gimnasio o centro deportivo?";
                    ob.GymRetries++;
                }
                else
                {
                    ob.GymName = Trunc(msg, 160);
                    if (!string.IsNullOrWhiteSpace(ob.GymName)) lead.Name = ob.GymName!; // el gym es el lead
                    reply = "dale, anotado" +
                        "[NUEVO_MENSAJE]Pregunta 2/3: cuantos socios tenés más o menos? (no cobramos por socio, es solo para conocerte)";
                    ob.Step = 2;
                }
                break;

            case 2:
                ob.MemberCount = Trunc(msg, 120);
                reply = "perfecto!" +
                    "[NUEVO_MENSAJE]Pregunta 3/3: como llevás hoy los cobros y las cuotas? (Excel, papel, algún sistema)";
                ob.Step = 3;
                break;

            case 3:
                ob.PaymentMethod = Trunc(msg, 200);
                reply = "Pasame tu mail y con eso te creo la cuenta ahora mismo. De paso te mando un código de descuento.";
                ob.Step = 4;
                break;

            case 4:
                var email = EmailRx.Match(msg);
                if (!email.Success)
                {
                    reply = "Necesito un mail válido para crear la cuenta. Me lo pasás de nuevo?";
                    break;
                }
                ob.Email = email.Value;
                var accessUrl = await _provision.RegisterAsync(
                    ob.GymName ?? lead.Name, ob.Email, ob.ContactName, ct);
                if (string.IsNullOrWhiteSpace(accessUrl))
                {
                    reply = "Uy, no pude crear la cuenta con ese mail. ¿Me lo confirmás escribiéndolo de nuevo?";
                    break;
                }
                ob.AccessUrl = accessUrl;
                ob.ProvisionedAt = DateTimeOffset.UtcNow;
                ob.Step = 5;
                // Cuenta creada = venta cerrada → cuenta para el % de cierre del funnel.
                lead.Status = LeadStatus.Closed;
                lead.ClosedAt ??= DateTimeOffset.UtcNow;
                reply = SuccessMessage(accessUrl!);
                _log.LogInformation("Onboarding GymHero provisionado: lead={Lead} gym={Gym}", lead.Id, ob.GymName);
                break;

            default:
                // ya terminó → conversación libre con la IA
                return new OnboardingResult(null, OffScript: true, Provisioned: false);
        }

        ob.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);
        return new OnboardingResult(reply, OffScript: false, Provisioned: ob.Step == 5);
    }

    private static string SuccessMessage(string accessUrl) =>
        "¡Listo! Te dejé la cuenta creada y lista para usar." +
        "[NUEVO_MENSAJE]Entrá directo desde este link, ya quedás adentro (sin formularios):" +
        "[NUEVO_MENSAJE]" + accessUrl +
        "[NUEVO_MENSAJE]Te dejo un video de 5 minutos con GymHero funcionando:" +
        "[NUEVO_MENSAJE]https://www.loom.com/share/02d97a7ccc804d0189f7bf9cf1af8fbf" +
        "[NUEVO_MENSAJE]Tenés 7 días gratis para probar todo." +
        "[NUEVO_MENSAJE]Después es el plan Starter: $50.000/mes, todo incluido (socios, clases e instructores ilimitados)." +
        "[NUEVO_MENSAJE]Y te dejo un código: ARRANCA20 — 20% OFF el primer mes. Lo cargás cuando actives el plan.";

    private static string? Trunc(string s, int n) => string.IsNullOrEmpty(s) ? s : (s.Length > n ? s[..n] : s);

    /// <summary>Heurística (sin IA) para detectar nombres de gym "truchos" — réplica del nodo de n8n.</summary>
    private static bool IsGymSuspicious(string raw)
    {
        var t = (raw ?? string.Empty).Trim();
        var low = t.ToLowerInvariant();
        if (t.Length < 2) return true;
        if (!Regex.IsMatch(low, "[a-záéíóúñ0-9]", RegexOptions.IgnoreCase)) return true; // solo emojis/signos
        if (Regex.IsMatch(t, @"^[\d\s.,-]+$")) return true; // solo números
        if (low.EndsWith("?")) return true; // es una pregunta
        string[] saludos = { "hola", "buenas", "buen dia", "buenos dias", "buenas tardes", "buenas noches", "que tal", "hey", "holis", "ola" };
        if (saludos.Contains(low)) return true;
        string[] basura = { "no se", "no sé", "no tengo", "ninguno", "ninguna", "nada", "no quiero", "por que", "por qué",
            "para que", "para qué", "xq", "jaja", "jeje", "jajaja", "asd", "asdasd", "aaa", "xd", "xddd", "???", "cualquiera", "y vos", "y tu" };
        foreach (var b in basura)
        {
            if (low == b) return true;
            if (Regex.IsMatch(low, "(^|\\s)" + Regex.Escape(b) + "($|\\s)")) return true;
        }
        return false;
    }
}
