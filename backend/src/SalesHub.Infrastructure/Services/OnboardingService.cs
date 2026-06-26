using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SalesHub.Core.Domain.Entities;
using SalesHub.Core.Domain.Enums;
using SalesHub.Infrastructure.Persistence;

namespace SalesHub.Infrastructure.Services;

/// <summary>Resultado de procesar un turno de onboarding. PendingQuestion (solo en off-script
/// durante el alta) es la pregunta del guion a reenviar después de la respuesta de la IA.</summary>
public record OnboardingResult(string? Reply, bool OffScript, bool Provisioned, string? PendingQuestion = null, bool WithPitchAudio = false);

/// <summary>
/// Motor de onboarding de ads GENÉRICO y multi-app. Lee la <see cref="OnboardingConfig"/> de cada
/// producto (intro, preguntas, endpoint de provisión, mensaje de éxito) y corre la máquina de
/// pasos: alta → N preguntas (la 1ª es el nombre del negocio) → email → provisiona → manda link.
/// Off-script (precio/info/etc.) → la IA contesta corto sin avanzar. El separador [NUEVO_MENSAJE]
/// lo splittea el sender.
/// </summary>
public class OnboardingService
{
    private readonly ApplicationDbContext _db;
    private readonly IOnboardingProvisionClient _provision;
    private readonly ILogger<OnboardingService> _log;

    public OnboardingService(ApplicationDbContext db, IOnboardingProvisionClient provision, ILogger<OnboardingService> log)
    {
        _db = db; _provision = provision; _log = log;
    }

    private static readonly Regex KeywordRx = new(
        @"\b(precio|costo|cuesta|cu[aá]nto sale|cu[aá]nto vale|valor|tarifa|presupuesto|info|informaci[oó]n|qu[eé] es|c[oó]mo funciona|para qu[eé] sirve|qu[eé] hace|demo|prueba|gratis|trial|probar|funcion(a|es)|features|caracter[ií]sticas|qu[eé] incluye)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex EmailRx = new(@"[^\s@]+@[^\s@]+\.[^\s@]+", RegexOptions.Compiled);
    private const string NL = "[NUEVO_MENSAJE]";

    public async Task<OnboardingResult> ProcessAsync(Lead lead, string lastMessage, OnboardingConfig cfg, CancellationToken ct)
    {
        var n = cfg.Questions.Count;
        if (n == 0) return new OnboardingResult(null, OffScript: true, Provisioned: false); // sin preguntas → IA libre

        var ob = await _db.Set<LeadOnboarding>().FirstOrDefaultAsync(o => o.LeadId == lead.Id, ct);
        if (ob is null)
        {
            ob = new LeadOnboarding { LeadId = lead.Id, Step = 0, ContactName = lead.Name };
            _db.Add(ob);
        }

        var msg = (lastMessage ?? string.Empty).Trim();
        var lower = msg.ToLowerInvariant();

        // Hasta qué paso se juntan datos: autoservicio tiene paso de mail (n+1); asistida termina en la última pregunta (n).
        var maxCollect = cfg.SelfServe ? n + 1 : n;

        // Off-script mientras junta datos → la IA responde, no avanza.
        if (ob.Step >= 1 && ob.Step <= maxCollect && KeywordRx.IsMatch(lower))
            return new OnboardingResult(null, OffScript: true, Provisioned: false, PendingQuestion: PendingQuestion(ob.Step, n, cfg));

        string reply;
        var withAudio = false;
        var provisioned = false;
        if (ob.Step == 0)
        {
            reply = Join(cfg.Intro, cfg.Questions[0]);
            ob.Step = 1;
        }
        else if (ob.Step >= 1 && ob.Step <= n)
        {
            var k = ob.Step; // el lead respondió la pregunta Questions[k-1]
            if (k == 1 && IsBusinessNameSuspicious(msg) && ob.GymRetries < 1)
            {
                reply = "jaja dale, en serio 🙏 " + LastSegment(cfg.Questions[0]);
                ob.GymRetries++;
            }
            else
            {
                if (k == 1)
                {
                    ob.GymName = Trunc(msg, 160);
                    if (!string.IsNullOrWhiteSpace(ob.GymName)) lead.Name = ob.GymName!; // el negocio es el lead
                }
                if (k < n) { reply = cfg.Questions[k]; ob.Step = k + 1; }
                else if (cfg.SelfServe) // última pregunta → audio del pitch (si hay) + pide mail
                {
                    reply = cfg.EmailPrompt; ob.Step = n + 1; withAudio = cfg.UsePitchAudio;
                }
                else
                {
                    // venta asistida: audio del pitch (si hay) o cierre por texto, y handoff a demo.
                    reply = cfg.ClosingMessage;
                    lead.Status = LeadStatus.Interested;
                    ob.Step = n + 2; // terminado; el vendedor coordina la demo
                    withAudio = cfg.UsePitchAudio;
                }
            }
        }
        else if (ob.Step == n + 1)
        {
            var email = EmailRx.Match(msg);
            if (!email.Success)
            {
                reply = "Necesito un mail válido para crear la cuenta. Me lo pasás de nuevo?";
            }
            else
            {
                ob.Email = email.Value;
                var url = await _provision.RegisterAsync(cfg.ProvisionUrl, cfg.ProvisionNameField,
                    ob.GymName ?? lead.Name, ob.Email, ob.ContactName, cfg.ProductKey, ct);
                if (string.IsNullOrWhiteSpace(url))
                {
                    reply = "Uy, no pude crear la cuenta con ese mail. ¿Me lo confirmás escribiéndolo de nuevo?";
                }
                else
                {
                    ob.AccessUrl = url;
                    ob.ProvisionedAt = DateTimeOffset.UtcNow;
                    ob.Step = n + 2;
                    provisioned = true;
                    lead.Status = LeadStatus.Closed; // cuenta creada = venta cerrada
                    lead.ClosedAt ??= DateTimeOffset.UtcNow;
                    reply = (cfg.SuccessMessage ?? string.Empty).Replace("{accessUrl}", url);
                    _log.LogInformation("Onboarding {Product} provisionado: lead={Lead}", cfg.ProductKey, lead.Id);
                }
            }
        }
        else
        {
            return new OnboardingResult(null, OffScript: true, Provisioned: false); // ya terminó → IA libre
        }

        ob.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);
        return new OnboardingResult(reply, OffScript: false, Provisioned: provisioned, WithPitchAudio: withAudio);
    }

    private static string Join(string a, string b) =>
        string.IsNullOrWhiteSpace(a) ? b : (string.IsNullOrWhiteSpace(b) ? a : a + NL + b);

    /// <summary>La pregunta pendiente a reenviar tras un off-script — solo la pregunta, sin el ack previo.</summary>
    private static string? PendingQuestion(int step, int n, OnboardingConfig cfg)
    {
        var raw = step <= n ? cfg.Questions[step - 1] : (step == n + 1 ? cfg.EmailPrompt : null);
        return raw is null ? null : LastSegment(raw);
    }

    private static string LastSegment(string s)
    {
        var idx = s.LastIndexOf(NL, StringComparison.Ordinal);
        return idx >= 0 ? s[(idx + NL.Length)..] : s;
    }

    private static string? Trunc(string s, int n) => string.IsNullOrEmpty(s) ? s : (s.Length > n ? s[..n] : s);

    /// <summary>Heurística (sin IA) para detectar nombres de negocio "truchos" — réplica del nodo de n8n.</summary>
    private static bool IsBusinessNameSuspicious(string raw)
    {
        var t = (raw ?? string.Empty).Trim();
        var low = t.ToLowerInvariant();
        if (t.Length < 2) return true;
        if (!Regex.IsMatch(low, "[a-záéíóúñ0-9]", RegexOptions.IgnoreCase)) return true;
        if (Regex.IsMatch(t, @"^[\d\s.,-]+$")) return true;
        if (low.EndsWith("?")) return true;
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
