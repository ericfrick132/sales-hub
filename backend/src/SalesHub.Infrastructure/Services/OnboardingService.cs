using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SalesHub.Core.Abstractions;
using SalesHub.Core.Domain.Entities;
using SalesHub.Core.Domain.Enums;
using SalesHub.Infrastructure.Persistence;

namespace SalesHub.Infrastructure.Services;

/// <summary>Resultado de procesar un turno de onboarding. PendingQuestion (solo en off-script
/// durante el alta) es la pregunta del guion a reenviar después de la respuesta de la IA.
/// MediaAssetIds + PostMediaText (solo reenganche): adjuntos a mandar DESPUÉS de Reply y el
/// texto que va después de los adjuntos (la 1ª pregunta) — orden reacción → media → pregunta.</summary>
public record OnboardingResult(string? Reply, bool OffScript, bool Provisioned, string? PendingQuestion = null, bool WithPitchAudio = false,
    List<Guid>? MediaAssetIds = null, string? PostMediaText = null, List<string>? MediaCaptions = null);

/// <summary>
/// Motor de onboarding de ads GENÉRICO y multi-app. Lee la <see cref="OnboardingConfig"/> de cada
/// producto (intro, preguntas, endpoint de provisión, mensaje de éxito) y corre la máquina de
/// pasos: alta → N preguntas (la 1ª es el nombre del negocio) → email → provisiona → manda link.
/// Off-script (precio/info/etc.) → la IA contesta corto sin avanzar. El separador [NUEVO_MENSAJE]
/// lo splittea el sender.
/// </summary>
public class OnboardingService
{
    /// <summary>
    /// Step centinela: un humano llevó la conversación y la devolvió con "+". El guion NO debe
    /// re-arrancar (nada de intro/preguntas): ProcessAsync cae al else final → IA libre, que
    /// continúa la charla con todo el contexto del hilo.
    /// </summary>
    public const int StepHumanHandoff = -9;

    private readonly ApplicationDbContext _db;
    private readonly IOnboardingProvisionClient _provision;
    private readonly IEmailSender _email;
    private readonly ILogger<OnboardingService> _log;

    public OnboardingService(ApplicationDbContext db, IOnboardingProvisionClient provision, IEmailSender email, ILogger<OnboardingService> log)
    {
        _db = db; _provision = provision; _email = email; _log = log;
    }

    private static readonly Regex KeywordRx = new(
        @"\b(precio|costo|cuesta|cu[aá]nto sale|cu[aá]nto vale|valor|tarifa|presupuesto|info|informaci[oó]n|qu[eé] es|c[oó]mo funciona|para qu[eé] sirve|qu[eé] hace|demo|prueba|gratis|trial|probar|funcion(a|es)|features|caracter[ií]sticas|qu[eé] incluye)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex EmailRx = new(@"[^\s@]+@[^\s@]+\.[^\s@]+", RegexOptions.Compiled);
    // Señales de que el lead está preguntando/dudando en vez de contestar. En el paso del mail
    // esto evita el "pasame el mail" robótico: primero resolvemos la duda, después pedimos el mail.
    private static readonly Regex DoubtRx = new(
        @"(\?|no s[eé]\b|no estoy segur|no entiend|(duda|consulta|pregunta)s?\b|y si\b|se puede|puedo\b|es seguro|no me convence|desconf[ií])",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private const string NL = "[NUEVO_MENSAJE]";

    // Typos clásicos de dominio tipeados a mano en WhatsApp. Se corrigen en silencio antes de
    // provisionar (un "gmial.com" crea una cuenta con mail muerto y el cliente nunca recibe nada).
    private static readonly Dictionary<string, string> DomainFixes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["gmial.com"] = "gmail.com", ["gmai.com"] = "gmail.com", ["gmal.com"] = "gmail.com",
        ["gamil.com"] = "gmail.com", ["gemail.com"] = "gmail.com", ["gmail.co"] = "gmail.com",
        ["gmail.con"] = "gmail.com", ["gmail.cm"] = "gmail.com", ["gmaill.com"] = "gmail.com",
        ["hotmial.com"] = "hotmail.com", ["hotmal.com"] = "hotmail.com", ["hotmil.com"] = "hotmail.com",
        ["hotmail.con"] = "hotmail.com", ["hotmail.co"] = "hotmail.com", ["hormail.com"] = "hotmail.com",
        ["outlok.com"] = "outlook.com", ["outlook.con"] = "outlook.com",
        ["yaho.com"] = "yahoo.com", ["yahooo.com"] = "yahoo.com", ["yahoo.con"] = "yahoo.com",
        ["icloud.con"] = "icloud.com", ["iclod.com"] = "icloud.com",
    };

    /// <summary>Corrige typos obvios del dominio ("juan@gmial.con" → "juan@gmail.com"). También
    /// arregla el TLD ".con"/".cm" pegado a dominios conocidos. Devuelve el mail final.</summary>
    private static string FixEmailTypos(string email)
    {
        var at = email.LastIndexOf('@');
        if (at <= 0 || at == email.Length - 1) return email;
        var local = email[..at];
        var domain = email[(at + 1)..].Trim().TrimEnd('.');
        if (DomainFixes.TryGetValue(domain, out var fixedDomain)) return local + "@" + fixedDomain;
        // TLD ".con"/".cm" es SIEMPRE typo de ".com"
        if (domain.EndsWith(".con", StringComparison.OrdinalIgnoreCase)) return local + "@" + domain[..^4] + ".com";
        if (domain.EndsWith(".cm", StringComparison.OrdinalIgnoreCase) && !domain.EndsWith("cameroon.cm", StringComparison.OrdinalIgnoreCase))
            return local + "@" + domain[..^3] + ".com";
        return local + "@" + domain;
    }

    public async Task<OnboardingResult> ProcessAsync(Lead lead, string lastMessage, OnboardingConfig cfg, CancellationToken ct, string? recentBurst = null)
    {
        var ob = await _db.Set<LeadOnboarding>().FirstOrDefaultAsync(o => o.LeadId == lead.Id, ct);
        if (ob is null)
        {
            ob = new LeadOnboarding { LeadId = lead.Id, Step = 0, ContactName = lead.Name };
            _db.Add(ob);
        }

        var msg = (lastMessage ?? string.Empty).Trim();
        var lower = msg.ToLowerInvariant();
        // El mail puede venir en un mensaje y el lead seguir escribiendo (ráfaga). Buscamos el
        // mail en TODA la ráfaga desde nuestra última respuesta, no solo en el último mensaje.
        var burst = string.IsNullOrWhiteSpace(recentBurst) ? msg : recentBurst;

        // ── Reenganche: el lead YA recibió el opener (que se presenta y promete precios) ──
        // El guion NO se re-presenta: ReengageIntro reacciona y cumple la promesa, y las
        // ReengageQuestions suelen ser más cortas (el opener ya hizo la pregunta calificadora).
        var reengage = lead.Source == LeadSource.ProductReengage;
        var hasReengageIntro = reengage && !string.IsNullOrWhiteSpace(cfg.ReengageIntro);
        var effectiveIntro = hasReengageIntro ? cfg.ReengageIntro : cfg.Intro;

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
                return new OnboardingResult(Join(effectiveIntro, cfg.PersonaQuestion), OffScript: false, Provisioned: false);
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
        var questions = persona?.Questions
            ?? (reengage && cfg.ReengageQuestions.Count > 0 ? cfg.ReengageQuestions : cfg.Questions);
        var emailPrompt = persona is null ? cfg.EmailPrompt : persona.EmailPrompt;
        var provisionUrl = persona is null ? cfg.ProvisionUrl : persona.ProvisionUrl;
        var provisionNameField = persona is null ? cfg.ProvisionNameField : persona.ProvisionNameField;
        var successMessage = persona is null ? cfg.SuccessMessage : persona.SuccessMessage;
        var provisionExtra = persona?.ProvisionExtra;
        var intro = effectiveIntro;

        var n = questions.Count;
        if (n == 0) return new OnboardingResult(null, OffScript: true, Provisioned: false); // sin preguntas → IA libre

        // Hasta qué paso se juntan datos: autoservicio tiene paso de mail (n+1); asistida termina en la última pregunta (n).
        var maxCollect = cfg.SelfServe ? n + 1 : n;

        // Off-script mientras junta datos → la IA responde, no avanza.
        //  - En las preguntas (1..n): keyword de precio/info → la IA contesta y el sistema RE-PREGUNTA.
        //  - En el paso del mail (n+1): si NO viene un mail y el lead pregunta/duda → la IA contesta
        //    ORGÁNICO y NO reenvía el pedido de mail (primero la duda; el mail se pide después, sin
        //    insistir). Si el mensaje trae un mail NO es off-script aunque matchee keywords (ej.
        //    "info@migym.com") → lo dejamos provisionar.
        var atEmailStep = ob.Step == n + 1;
        var inCollect = ob.Step >= 1 && ob.Step <= maxCollect;
        // En el paso del mail: si el mail está EN LA RÁFAGA (aunque no en el último mensaje),
        // NO es duda → provisionamos. Solo es duda si NO hay mail en toda la ráfaga y pregunta.
        // OJO: la duda se busca en TODA la ráfaga, no solo el último mensaje — el lead suele
        // preguntar en 2-3 mensajes y cerrar con un "es así" corto; mirando solo el último,
        // las preguntas se ignoraban y el bot re-pedía el mail (caso real 2026-07-07).
        var burstLower = burst.ToLowerInvariant();
        var isDoubt = atEmailStep
            ? (!EmailRx.IsMatch(burst) && (KeywordRx.IsMatch(burstLower) || DoubtRx.IsMatch(burst)))
            : KeywordRx.IsMatch(burstLower);
        if (inCollect && isDoubt)
            return new OnboardingResult(null, OffScript: true, Provisioned: false,
                PendingQuestion: atEmailStep ? null : PendingQuestion(ob.Step, n, questions, emailPrompt));

        string reply;
        var withAudio = false;
        var provisioned = false;
        if (ob.Step == 0)
        {
            // Solo apps de una sola persona (las multi-persona ya pasaron por la selección).
            if (hasReengageIntro)
            {
                // Cumplir la promesa del opener como la conversación real que convirtió:
                // reacción + precios (+ video/imagen si hay) y RECIÉN después la 1ª pregunta.
                ob.Step = 1;
                ob.UpdatedAt = DateTimeOffset.UtcNow;
                await _db.SaveChangesAsync(ct);
                return new OnboardingResult(intro, OffScript: false, Provisioned: false,
                    MediaAssetIds: cfg.ReengageMediaAssetIds.Count > 0 ? cfg.ReengageMediaAssetIds : null,
                    PostMediaText: questions[0],
                    MediaCaptions: cfg.ReengageMediaCaptions.Count > 0 ? cfg.ReengageMediaCaptions : null);
            }
            reply = Join(intro, questions[0]);
            ob.Step = 1;
        }
        else if (ob.Step >= 1 && ob.Step <= n)
        {
            var k = ob.Step; // el lead respondió la pregunta questions[k-1]
            if (k == 1 && IsBusinessNameSuspicious(msg) && ob.GymRetries < 1)
            {
                // El nombre del negocio se convierte en el subdominio del tenant (la app lo
                // auto-deriva, no lo pide). Si el lead manda una frase/oración en vez del nombre,
                // el subdominio sale impresentable → re-preguntamos por el nombre solo.
                reply = "jaja perdón 🙏 decime solo el nombre del negocio";
                ob.GymRetries++;
            }
            else
            {
                if (k == 1)
                {
                    ob.GymName = Trunc(msg, 160);
                    // Solo propagar al lead un nombre PRESENTABLE: si tras el re-ask siguió
                    // mandando "hola"/frases, el nombre viejo del lead es mejor que la basura
                    // (el renderer usa lead.Name en los mensajes: "Hola hola!" es el caso real).
                    if (!string.IsNullOrWhiteSpace(ob.GymName) && !IsBusinessNameSuspicious(ob.GymName!))
                        lead.Name = ob.GymName!; // el negocio/persona es el lead
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
            // El mail puede estar en cualquier mensaje de la ráfaga, no solo el último.
            var email = EmailRx.Match(burst);
            if (!email.Success)
            {
                // Las dudas ya se resolvieron arriba (off-script). Acá el lead no mandó mail ni
                // pregunta → recordatorio SUAVE, sin insistir ni sonar a bot. El mail sale cuando quiera.
                reply = "cuando quieras me pasás tu mail y te dejo la cuenta lista al toque 🙌";
            }
            else
            {
                ob.Email = FixEmailTypos(email.Value.Trim());
                var url = await _provision.RegisterAsync(provisionUrl, provisionNameField,
                    PresentableBusinessName(ob.GymName, lead.Name), ob.Email, ob.ContactName, cfg.ProductKey, ct, provisionExtra);
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
                    // Anti-spam de links: con SMTP configurado el link viaja por MAIL y por
                    // WhatsApp solo se avisa. Sin SMTP (o si el mail falla) sale como siempre.
                    var appName = lead.Product?.DisplayName ?? cfg.ProductKey;
                    var mailed = _email.IsConfigured && await _email.SendAsync(ob.Email!,
                        $"Tu acceso a {appName}",
                        $"<p>Hola! Tu cuenta de <b>{appName}</b> ya está lista.</p>" +
                        $"<p><a href=\"{url}\">Entrar a mi cuenta</a></p>" +
                        $"<p>Es un acceso directo, sin usuario ni contraseña. Cualquier cosa respondé el WhatsApp.</p>", ct);
                    reply = (successMessage ?? string.Empty).Replace("{accessUrl}",
                        mailed ? "te acabo de mandar el link de acceso por mail (mirá también promociones/spam)" : url);
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

    /// <summary>
    /// SOPORTE REAL: regenera el acceso del lead contra la app (bot-register es idempotente:
    /// para cuentas existentes devuelve un link fresco de auto-login). Actualiza AccessUrl.
    /// Devuelve null si no hay config/email o si la app falló — el caller decide el fallback.
    /// Caso real que motiva esto: lead con cuenta creada pero link roto/viejo al que el bot
    /// le "inventaba" instrucciones en vez de darle un acceso que funcione.
    /// </summary>
    public async Task<string?> RegenerateAccessAsync(Lead lead, LeadOnboarding ob, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(ob.Email)) return null;
        var cfg = await _db.OnboardingConfigs.AsNoTracking()
            .FirstOrDefaultAsync(c => c.ProductKey == lead.ProductKey, ct);
        if (cfg is null) return null;
        OnboardingPersona? persona = null;
        if (!string.IsNullOrWhiteSpace(ob.PersonaKey))
            persona = await _db.Set<OnboardingPersona>().AsNoTracking()
                .FirstOrDefaultAsync(p => p.ProductKey == lead.ProductKey && p.Key == ob.PersonaKey, ct);
        var provisionUrl = persona is null ? cfg.ProvisionUrl : persona.ProvisionUrl;
        var nameField = persona is null ? cfg.ProvisionNameField : persona.ProvisionNameField;
        var url = await _provision.RegisterAsync(provisionUrl, nameField,
            PresentableBusinessName(ob.GymName, lead.Name), ob.Email!, ob.ContactName, cfg.ProductKey, ct, persona?.ProvisionExtra);
        if (string.IsNullOrWhiteSpace(url)) return null;
        ob.AccessUrl = url;
        ob.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);
        _log.LogInformation("Soporte: acceso regenerado para lead {Lead} ({Product})", lead.Id, cfg.ProductKey);
        return url;
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

    /// <summary>
    /// Nombre de negocio PRESENTABLE para crear la cuenta: en las apps es el nombre del tenant
    /// Y la base del subdominio. Si el lead tipeó un saludo/frase ("hola" es el caso real),
    /// caemos al nombre del lead si sirve; último recurso, un neutro que las apps renombran
    /// en su onboarding. Nunca un tenant/subdominio "hola".
    /// </summary>
    private static string PresentableBusinessName(string? gymName, string? leadName)
    {
        if (!string.IsNullOrWhiteSpace(gymName) && !IsBusinessNameSuspicious(gymName!)) return gymName!.Trim();
        if (!string.IsNullOrWhiteSpace(leadName) && !IsBusinessNameSuspicious(leadName!)) return leadName!.Trim();
        return "Mi negocio";
    }

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
        // Respuesta que es una FRASE/oración en vez de un nombre (ej. "te comento estoy por
        // abrir el lugar de estética y engrese en el links…"). Los nombres reales son cortos
        // (1-5 palabras); si el lead se pone a CONTAR en vez de NOMBRAR, el nombre se guarda
        // sucio y el subdominio del tenant sale impresentable. Re-preguntamos por el nombre solo.
        var words = low.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
        if (words > 8 || t.Length > 60) return true;
        // Marcadores de que están describiendo/contando (no nombrando). Solo si ya es más largo
        // que un nombre corto, para no falsear con un nombre real que use una de estas palabras.
        if (words >= 4 && Regex.IsMatch(low,
            @"\b(estoy|quiero|quer[ií]a|tengo|necesito|me\s+gustar[ií]a|todav[ií]a|reci[eé]n|voy\s+a|estamos|te\s+comento|a[uú]n\s+no|no\s+tengo|abrir\s+(el|un|una)|estoy\s+por|por\s+abrir|pensando\s+en)\b"))
            return true;
        return false;
    }
}
