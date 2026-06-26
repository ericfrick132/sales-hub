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
        var ob = await _db.Set<LeadOnboarding>().FirstOrDefaultAsync(o => o.LeadId == lead.Id, ct);
        if (ob is null)
        {
            ob = new LeadOnboarding { LeadId = lead.Id, Step = 0, ContactName = lead.Name };
            _db.Add(ob);
        }

        var msg = (lastMessage ?? string.Empty).Trim();
        var lower = msg.ToLowerInvariant();

        // ── Apps multi-perfil: antes de las preguntas, elegir la persona ───────────────
        var hasPersonas = !string.IsNullOrWhiteSpace(cfg.PersonaQuestion);
        var personas = hasPersonas
            ? await _db.Set<OnboardingPersona>().Where(p => p.ProductKey == cfg.ProductKey).OrderBy(p => p.SortOrder).ToListAsync(ct)
            : new List<OnboardingPersona>();
        if (personas.Count == 0) hasPersonas = false;
        var persona = ob.PersonaKey is null ? null : personas.FirstOrDefault(p => p.Key == ob.PersonaKey);

        if (hasPersonas && persona is null)
        {
            // Step 0 = recién llegó el ad → intro + pregunta de persona; -1 = esperando que elija.
            if (ob.Step == 0)
            {
                ob.Step = -1;
                ob.UpdatedAt = DateTimeOffset.UtcNow;
                await _db.SaveChangesAsync(ct);
                return new OnboardingResult(Join(cfg.Intro, cfg.PersonaQuestion), OffScript: false, Provisioned: false);
            }
            var picked = DetectPersona(personas, lower);
            if (picked is null && ob.GymRetries < 1)
            {
                ob.GymRetries++; // re-preguntamos una vez (el contador se resetea al fijar la persona)
                ob.UpdatedAt = DateTimeOffset.UtcNow;
                await _db.SaveChangesAsync(ct);
                return new OnboardingResult("perdón, no te seguí 🙏 " + LastSegment(cfg.PersonaQuestion), OffScript: false, Provisioned: false);
            }
            persona = picked ?? personas[0]; // tras el re-ask, default a la primera
            ob.PersonaKey = persona.Key;
            ob.GymRetries = 0;
            ob.Step = 1;
            ob.UpdatedAt = DateTimeOffset.UtcNow;
            await _db.SaveChangesAsync(ct);
            return new OnboardingResult(persona.Questions.Count > 0 ? persona.Questions[0] : persona.EmailPrompt,
                OffScript: false, Provisioned: false);
        }

        // ── Settings activos: de la persona elegida, o del config si la app es de una sola persona ──
        var questions = persona?.Questions ?? cfg.Questions;
        var emailPrompt = persona is null ? cfg.EmailPrompt : persona.EmailPrompt;
        var provisionUrl = persona is null ? cfg.ProvisionUrl : persona.ProvisionUrl;
        var provisionNameField = persona is null ? cfg.ProvisionNameField : persona.ProvisionNameField;
        var successMessage = persona is null ? cfg.SuccessMessage : persona.SuccessMessage;
        var provisionExtra = persona?.ProvisionExtra;
        var intro = cfg.Intro;

        var n = questions.Count;
        if (n == 0) return new OnboardingResult(null, OffScript: true, Provisioned: false); // sin preguntas → IA libre

        // Hasta qué paso se juntan datos: autoservicio tiene paso de mail (n+1); asistida termina en la última pregunta (n).
        var maxCollect = cfg.SelfServe ? n + 1 : n;

        // Off-script mientras junta datos → la IA responde, no avanza.
        if (ob.Step >= 1 && ob.Step <= maxCollect && KeywordRx.IsMatch(lower))
            return new OnboardingResult(null, OffScript: true, Provisioned: false, PendingQuestion: PendingQuestion(ob.Step, n, questions, emailPrompt));

        string reply;
        var withAudio = false;
        var provisioned = false;
        if (ob.Step == 0)
        {
            // Solo apps de una sola persona (las multi-persona ya pasaron por la selección).
            reply = Join(intro, questions[0]);
            ob.Step = 1;
        }
        else if (ob.Step >= 1 && ob.Step <= n)
        {
            var k = ob.Step; // el lead respondió la pregunta questions[k-1]
            if (k == 1 && IsBusinessNameSuspicious(msg) && ob.GymRetries < 1)
            {
                reply = "jaja dale, en serio 🙏 " + LastSegment(questions[0]);
                ob.GymRetries++;
            }
            else
            {
                if (k == 1)
                {
                    ob.GymName = Trunc(msg, 160);
                    if (!string.IsNullOrWhiteSpace(ob.GymName)) lead.Name = ob.GymName!; // el negocio/persona es el lead
                }
                if (k < n) { reply = questions[k]; ob.Step = k + 1; }
                else if (cfg.SelfServe) // última pregunta → audio del pitch (si hay) + pide mail
                {
                    reply = emailPrompt; ob.Step = n + 1; withAudio = cfg.UsePitchAudio;
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
                var url = await _provision.RegisterAsync(provisionUrl, provisionNameField,
                    ob.GymName ?? lead.Name, ob.Email, ob.ContactName, cfg.ProductKey, ct, provisionExtra);
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
                    reply = (successMessage ?? string.Empty).Replace("{accessUrl}", url);
                    _log.LogInformation("Onboarding {Product} provisionado: lead={Lead} persona={Persona}", cfg.ProductKey, lead.Id, ob.PersonaKey ?? "-");
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
    private static string? PendingQuestion(int step, int n, List<string> questions, string emailPrompt)
    {
        var raw = step <= n ? questions[step - 1] : (step == n + 1 ? emailPrompt : null);
        return raw is null ? null : LastSegment(raw);
    }

    /// <summary>Detecta la persona elegida por el lead: por número ("1"/"2"), o por keywords de cada persona.</summary>
    private static OnboardingPersona? DetectPersona(List<OnboardingPersona> personas, string lower)
    {
        var t = lower.Trim();
        for (int i = 0; i < personas.Count; i++)
        {
            var num = (i + 1).ToString();
            if (t == num || t.StartsWith(num + ")") || t.StartsWith(num + ".") || t.StartsWith(num + " ") || t.StartsWith(num + "-"))
                return personas[i];
        }
        foreach (var p in personas)
            foreach (var kw in p.Keywords)
                if (!string.IsNullOrWhiteSpace(kw) && t.Contains(kw.Trim().ToLowerInvariant()))
                    return p;
        return null;
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
