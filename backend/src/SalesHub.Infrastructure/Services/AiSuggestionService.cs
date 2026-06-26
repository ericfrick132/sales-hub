using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using SalesHub.Core.Domain.Entities;

namespace SalesHub.Infrastructure.Services;

/// <summary>
/// Arma el prompt de venta y le pide a Claude la próxima respuesta para el lead.
/// Inyecta el precio REAL del producto (Product.PriceDisplay) y valida la salida
/// con un guardrail: si la IA menciona un precio que no coincide, reintenta y, si
/// insiste, descarta la respuesta (no propagamos un precio inventado).
/// </summary>
public class AiSuggestionService
{
    private readonly ClaudeClient _claude;
    private readonly AiRulesProvider _rules;
    private readonly ILogger<AiSuggestionService> _log;

    public AiSuggestionService(ClaudeClient claude, AiRulesProvider rules, ILogger<AiSuggestionService> log)
    {
        _claude = claude;
        _rules = rules;
        _log = log;
    }

    // Cap del hilo enviado a la IA: en estas charlas el contexto lejano no se usa (los hilos
    // largos son follow-ups repetidos, no diálogo acumulado). Con las últimas 8 interacciones
    // alcanza para no repetir preguntas, y le pone techo al costo por llamada.
    private const int MaxThreadMessages = 8;

    /// <summary>True si Claude está configurado — el worker no sugiere si no.</summary>
    public bool IsConfigured => _claude.IsConfigured;

    /// <summary>Próxima respuesta cuando el último mensaje es del lead.</summary>
    public async Task<string?> SuggestReplyAsync(
        Lead lead, Product product, IReadOnlyList<ConversationMessage> thread, CancellationToken ct)
    {
        if (thread.Count == 0) return null;
        var rulesBlock = await _rules.GetBlockAsync(null, ct);
        var system = BuildSystemPrompt(product, rulesBlock);
        var conversation = BuildConversation(lead, thread, instruction: null);
        return await CompleteWithPriceGuardrailAsync(system, conversation, product, ct);
    }

    /// <summary>
    /// Mensaje de re-enganche cuando el lead venía hablando y se quedó callado.
    /// </summary>
    public async Task<string?> SuggestReengagementAsync(
        Lead lead, Product product, IReadOnlyList<ConversationMessage> thread, TimeSpan silentFor, CancellationToken ct)
    {
        if (thread.Count == 0) return null;
        var rulesBlock = await _rules.GetBlockAsync(null, ct);
        var system = BuildSystemPrompt(product, rulesBlock);
        var hrs = Math.Max(1, (int)Math.Round(silentFor.TotalHours));
        var instruction =
            $"El lead venía hablando y se quedó callado hace ~{hrs} horas. Escribí un mensaje CORTO y " +
            "natural para retomar la charla, sin sonar desesperado ni repetir lo ya dicho. Si tiene sentido, " +
            "movelo al próximo paso (una demo, o el link de checkout). Si ya le mandaste seguimientos antes, " +
            "variá el enfoque. Si no hay nada nuevo para aportar, mejor algo liviano que reabra la charla.";
        var conversation = BuildConversation(lead, thread, instruction);
        return await CompleteWithPriceGuardrailAsync(system, conversation, product, ct);
    }

    /// <summary>
    /// Clasifica el ESTADO del prospecto analizando toda la conversación. Devuelve una
    /// intención (interested / not_interested / scheduled / won / needs_human / unknown)
    /// que el ConversationAgent mapea a LeadStatus. No genera respuesta — solo analiza.
    /// </summary>
    public async Task<LeadIntent> ClassifyLeadAsync(
        Lead lead, Product product, IReadOnlyList<ConversationMessage> thread, CancellationToken ct)
    {
        if (!_claude.IsConfigured || thread.Count == 0) return LeadIntent.Unknown;

        var system =
            "Sos un analista de ventas. Te paso una conversación entre un vendedor y un prospecto.\n" +
            "Clasificá el ESTADO del prospecto con UNA sola palabra de esta lista, sin explicar nada:\n" +
            "- interested: muestra interés, hace preguntas, pide info o precio, sigue la charla.\n" +
            "- not_interested: dice claramente que no le interesa, que no, que ya compró en otro lado, o pide que no le escriban.\n" +
            "- scheduled: acordó una demo, llamada o reunión.\n" +
            "- won: ya compró, cerró o pagó.\n" +
            "- needs_human: situación delicada o reclamo que requiere una persona.\n" +
            "- unknown: todavía no hay señal clara.\n" +
            "Ante la duda entre interested y not_interested, elegí interested (no cerramos por las dudas).\n" +
            "Respondé SOLO con una de esas palabras, en minúscula, sin nada más.";

        var conversation = BuildConversation(lead, thread,
            instruction: "Clasificá el estado del prospecto según la conversación. Respondé con UNA sola palabra de la lista.");

        var raw = await _claude.CompleteAsync(system, conversation, "conversacion", ct);
        return ParseIntent(raw);
    }

    private static LeadIntent ParseIntent(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return LeadIntent.Unknown;
        var t = raw.Trim().ToLowerInvariant();
        // Orden importa: "not_interested" contiene "interested".
        if (t.Contains("not_interested") || t.Contains("not interested") || t.Contains("no_interes")) return LeadIntent.NotInterested;
        if (t.Contains("needs_human") || t.Contains("human")) return LeadIntent.NeedsHuman;
        if (t.Contains("scheduled") || t.Contains("agend")) return LeadIntent.Scheduled;
        if (t.Contains("won")) return LeadIntent.Won;
        if (t.Contains("interested") || t.Contains("interes")) return LeadIntent.Interested;
        return LeadIntent.Unknown;
    }

    // ---- Camino económico: una sola llamada (clasifica + responde) -------------

    /// <summary>
    /// UNA llamada a Claude que clasifica el estado del prospecto Y genera la respuesta
    /// (en vez de dos llamadas separadas). Devuelve intención + si conviene responder +
    /// la respuesta (ya con guardrail de precio). Es el camino para la charla real.
    /// </summary>
    public async Task<(LeadIntent intent, bool shouldReply, string? reply)> SuggestReplyWithIntentAsync(
        Lead lead, Product product, IReadOnlyList<ConversationMessage> thread, CancellationToken ct)
    {
        if (!_claude.IsConfigured || thread.Count == 0) return (LeadIntent.Unknown, false, null);

        var rulesBlock = await _rules.GetBlockAsync(null, ct);
        var system = BuildSystemPrompt(product, rulesBlock) + MergedTaskInstruction;
        var conversation = BuildConversation(lead, thread, instruction: null);

        var raw = await _claude.CompleteAsync(system, conversation, "conversacion", ct);
        var (intent, shouldReply, reply) = SplitIntentReply(raw);

        if (shouldReply && !string.IsNullOrWhiteSpace(reply) && MentionsWrongPrice(reply!, product.PriceDisplay))
        {
            var corrected = conversation +
                $"\n\nIMPORTANTE: el precio EXACTO es \"{product.PriceDisplay}\". No menciones NINGÚN otro número de precio. " +
                "Regenerá respetando el formato (línea 1 `estado=...`, después el mensaje).";
            var (i2, s2, r2) = SplitIntentReply(await _claude.CompleteAsync(system, corrected, "conversacion", ct));
            if (i2 != LeadIntent.Unknown) intent = i2;
            shouldReply = s2; reply = r2;
            if (shouldReply && (string.IsNullOrWhiteSpace(reply) || MentionsWrongPrice(reply!, product.PriceDisplay)))
                return (intent, false, null);
        }

        return (intent, shouldReply, reply);
    }

    /// <summary>
    /// Responde una pregunta puntual de un lead que está EN MEDIO del alta automática (off-script).
    /// Respuesta corta, solo lo que preguntó, SIN follow-up ni cierre — el sistema reenvía la
    /// pregunta pendiente del alta justo después.
    /// </summary>
    public async Task<string?> AnswerOnboardingAsideAsync(
        Lead lead, Product product, IReadOnlyList<ConversationMessage> thread, CancellationToken ct)
    {
        if (!_claude.IsConfigured || thread.Count == 0) return null;

        var rulesBlock = await _rules.GetBlockAsync(null, ct);
        var system = BuildSystemPrompt(product, rulesBlock) + OnboardingAsideInstruction;
        var conversation = BuildConversation(lead, thread, instruction: null);

        var reply = await _claude.CompleteAsync(system, conversation, "onboarding", ct);
        if (!string.IsNullOrWhiteSpace(reply) && MentionsWrongPrice(reply!, product.PriceDisplay))
        {
            var corrected = conversation +
                $"\n\nIMPORTANTE: el precio EXACTO es \"{product.PriceDisplay}\". No menciones NINGÚN otro número de precio.";
            reply = await _claude.CompleteAsync(system, corrected, "onboarding", ct);
            if (!string.IsNullOrWhiteSpace(reply) && MentionsWrongPrice(reply!, product.PriceDisplay)) return null;
        }
        return string.IsNullOrWhiteSpace(reply) ? null : reply!.Trim();
    }

    private const string OnboardingAsideInstruction =
        "\n\nCONTEXTO CRÍTICO: el lead está en el ALTA AUTOMÁTICA (un flujo guiado de preguntas) y te hizo una " +
        "pregunta puntual fuera del guion. Respondé SOLO esa pregunta, en 1 frase corta y directa. " +
        "PROHIBIDO TERMINANTE: hacer otra pregunta, agregar cierre/CTA, invitar a avanzar o despedirte. " +
        "Inmediatamente DESPUÉS de tu respuesta el sistema reenvía la pregunta pendiente del alta, así que tu " +
        "mensaje tiene que ser SOLO el dato que te pidió, nada más. No repitas vos la pregunta del alta.";

    private const string MergedTaskInstruction =
        "\n\nTAREA DOBLE: además de responder, clasificá al prospecto. Tu salida tiene EXACTAMENTE este formato:\n" +
        "primera línea: estado=<una de: interesado, no_interesado, agendo, compro, derivar, indefinido>\n" +
        "de la segunda línea en adelante: el mensaje para el lead, en el estilo de siempre (minúscula, sin puntuación).\n" +
        "si el prospecto ya cerró en contra (no_interesado) o ya compró (compro), NO escribas mensaje: poné solo `(sin respuesta)`.\n" +
        "ante la duda entre interesado y no_interesado, elegí interesado. una objeción tipo \"ya tengo un sistema\" NO es no_interesado: es interesado, hay que repreguntar.";

    private static (LeadIntent intent, bool shouldReply, string? reply) SplitIntentReply(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return (LeadIntent.Unknown, false, null);
        var lines = raw.Replace("\r\n", "\n").Split('\n');
        var intent = LeadIntent.Unknown;
        var start = 0;
        for (var i = 0; i < lines.Length && i < 2; i++)
        {
            var m = Regex.Match(lines[i], @"estado\s*[:=]\s*([a-záéíóú_]+)", RegexOptions.IgnoreCase);
            if (m.Success) { intent = MapEstado(m.Groups[1].Value); start = i + 1; break; }
        }
        var reply = string.Join("\n", lines.Skip(start)).Trim();
        var bare = reply.Replace("(", "").Replace(")", "").Trim();
        if (reply.Length == 0 || bare.StartsWith("sin respuesta", StringComparison.OrdinalIgnoreCase))
            return (intent, false, null);
        return (intent, true, reply);
    }

    private static LeadIntent MapEstado(string s) => s.ToLowerInvariant() switch
    {
        "no_interesado" => LeadIntent.NotInterested,
        "agendo" or "agendó" => LeadIntent.Scheduled,
        "compro" or "compró" => LeadIntent.Won,
        "derivar" => LeadIntent.NeedsHuman,
        "interesado" => LeadIntent.Interested,
        _ => LeadIntent.Unknown,
    };

    // ---- Heurísticos SIN IA (sacados de los chats reales de gymhero) -----------

    private static readonly Regex AutoResponderRx = new(
        @"gracias por (comunicarte|contactarte|tu mensaje|escribir)|en este momento no (estamos|podemos|puedo)|no (escuchamos|podemos escuchar|reproducir|recibimos) (audios|llamadas)|horario de atenci|asistente virtual|tu mensaje fue recib|a la brevedad|enseguida (te|estaremos)|nuevo n[uú]mero|deshabilitad|bienvenid",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex HardStopRx = new(
        @"(dej[aá]|deje[ns]?|par[aá]) de (mandar|enviar|escribir)|no me (escrib|mand|contact)|dame de baja|me das de baja|no (quiero|deseo) (recibir|que me)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex FirmRejectRx = new(
        @"no me interesa|no estoy interesad|no,? gracias|no necesito|no necesitamos|no me sirve",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex WrongNumberRx = new(
        @"(te |se )?(confundiste|equivocaste)|n[uú]mero equivocado|equivocad|no (tengo|somos|es|tenemos) (un |una )?(gim|gimnasio)|no soy de (un |una )?(gim|gimnasio)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static bool IsAutoResponder(string? t) => !string.IsNullOrWhiteSpace(t) && AutoResponderRx.IsMatch(t);
    public static bool IsHardStop(string? t) => !string.IsNullOrWhiteSpace(t) && HardStopRx.IsMatch(t);
    public static bool IsFirmReject(string? t) => !string.IsNullOrWhiteSpace(t) && FirmRejectRx.IsMatch(t);
    public static bool IsWrongNumber(string? t) => !string.IsNullOrWhiteSpace(t) && WrongNumberRx.IsMatch(t);

    /// <summary>Respuesta scripteada (sin IA) a un auto-responder de gimnasio: re-pitch por texto.</summary>
    public static string ScriptedAutoResponderReply()
        => "dale sin problema te escribo por acá\ncomo vienen cobrando las cuotas de los socios hoy";

    /// <summary>Cierre cordial scripteado (sin IA) para rechazo firme o número equivocado.</summary>
    public static string ScriptedCordialClose(bool wrongNumber)
        => wrongNumber
            ? "ah disculpá la confusión entonces éxitos"
            : "dale sin drama cualquier cosa que necesites por acá ando";

    private async Task<string?> CompleteWithPriceGuardrailAsync(
        string system, string conversation, Product product, CancellationToken ct)
    {
        var reply = await _claude.CompleteAsync(system, conversation, "conversacion", ct);
        if (string.IsNullOrWhiteSpace(reply)) return null;

        if (MentionsWrongPrice(reply, product.PriceDisplay))
        {
            _log.LogWarning(
                "IA mencionó un precio que no coincide con '{Price}' para {Product}; reintentando.",
                product.PriceDisplay, product.DisplayName);

            var corrected = conversation +
                $"\n\nIMPORTANTE: el precio EXACTO es \"{product.PriceDisplay}\". " +
                "No menciones NINGÚN otro número de precio. Regenerá la respuesta:";
            reply = await _claude.CompleteAsync(system, corrected, "conversacion", ct);

            if (string.IsNullOrWhiteSpace(reply) || MentionsWrongPrice(reply, product.PriceDisplay))
            {
                _log.LogWarning(
                    "IA insistió con un precio incorrecto para {Product}; se descarta la respuesta.",
                    product.DisplayName);
                return null;
            }
        }

        return reply;
    }

    /// <summary>Parte estática (se cachea): instrucciones base + contexto del producto.</summary>
    private static string BuildSystemPrompt(Product product, string extraRules)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Sos un vendedor argentino real escribiendo por WhatsApp (NO un bot). Generás SOLO");
        sb.AppendLine("el texto de la próxima respuesta — sin comillas, sin prefijos tipo \"Vendedor:\", sin explicaciones.");
        sb.AppendLine();
        sb.AppendLine("ESTILO OBLIGATORIO (clave para que no parezca IA):");
        sb.AppendLine("- escribí TODO en minúscula, siempre. nunca mayúsculas, ni al empezar ni en nombres propios.");
        sb.AppendLine("- NO uses signos de puntuación: nada de puntos, comas, dos puntos, ni de pregunta o exclamación.");
        sb.AppendLine("  tampoco signos de apertura (¿ ¡). separá ideas con un salto de línea o un espacio, nunca con puntos.");
        sb.AppendLine("- argentino relajado, de vos. NO uses muletillas marcadas tipo che, capo, viste, etc.");
        sb.AppendLine("- PROHIBIDO usar la palabra \"boludo\" y sus variantes (boluda, boludos, boludas, bolu, boludez). es ofensiva. NUNCA la uses, ni en chiste ni aunque el lead la use primero.");
        sb.AppendLine("- cortísimo, como un wpp real entre dos personas: 1 o 2 renglones, no más.");
        sb.AppendLine("- NO termines siempre con una pregunta. muchas veces mejor tirá un dato y listo, o una afirmación corta.");
        sb.AppendLine("- variá las aperturas y el largo. no suenes perfecto ni armadito. nada de listas ni viñetas.");
        sb.AppendLine("- cero corporativo: nada de estimado, no dude en, quedo a disposición.");
        sb.AppendLine("- emojis casi nunca, como mucho uno y solo si el lead viene usando emojis.");
        sb.AppendLine("- espejá el tono del lead. no inventes datos, si no sabés algo no lo afirmes.");
        sb.AppendLine("- ÚNICA excepción a lo de los símbolos: el precio y el link van TAL CUAL te los doy.");
        sb.AppendLine();
        sb.AppendLine($"PRODUCTO: {product.DisplayName}");
        if (!string.IsNullOrWhiteSpace(product.PriceDisplay))
        {
            sb.AppendLine($"PRECIO (usá EXACTAMENTE esto, tal cual, sin redondear ni cambiar): {product.PriceDisplay}");
            sb.AppendLine("Nunca inventes otro número de precio. Si el precio dice algo tipo \"a confirmar\",");
            sb.AppendLine("decí que lo confirmás en breve — no tires un número inventado.");
        }
        else
        {
            sb.AppendLine("PRECIO: no lo tenés. NO inventes uno; si preguntan, decí que lo confirmás.");
        }
        if (!string.IsNullOrWhiteSpace(product.CheckoutUrl))
            sb.AppendLine($"LINK DE CHECKOUT (mandalo cuando tenga sentido cerrar): {product.CheckoutUrl}");
        sb.AppendLine();
        sb.AppendLine("TÉCNICA (lo que MEJOR convierte en este producto, seguilo):");
        sb.AppendLine("- si el lead dice que YA TIENE un sistema, NO te rindas: repreguntá si ese sistema le cobra automático por mercado pago o si sigue persiguiendo pagos a mano, y si ve en recepción quién está al día. ahí se abre la venta. una objeción tipo \"ya tengo\" es una oportunidad, no un no.");
        sb.AppendLine("- si dice que NO tiene morosos, encarálo como plata que se le escapa sin darse cuenta (cobra dos veces el mismo mes, se olvida de alguien), no como perseguir morosos.");
        sb.AppendLine("- si dice que no es el dueño, pedile el contacto o mail del dueño para mandarle la info directa.");
        sb.AppendLine("- ante un \"ya tengo otro\", ofrecé que le migrás los datos gratis y que se configura en 1 día.");
        sb.AppendLine("- si el lead dice que NO de forma firme, respondé UNA vez corto y cordial y cerrá. NUNCA insistas ni mandes otro mensaje: insistir sobre un no quema la marca.");
        sb.AppendLine("- nada de charla de cortesía interminable: si no hay avance comercial en 1 o 2 mensajes, cerrá amable.");
        if (!string.IsNullOrWhiteSpace(product.AiSalesPlaybook))
        {
            sb.AppendLine();
            sb.AppendLine("REGLAS Y PLAYBOOK DE ESTE PRODUCTO (prioritarias, seguilas al pie):");
            sb.AppendLine(product.AiSalesPlaybook);
        }
        if (!string.IsNullOrWhiteSpace(extraRules))
            sb.Append(extraRules);
        return sb.ToString();
    }

    /// <summary>Parte variable: el hilo de la conversación + la instrucción final.</summary>
    private static string BuildConversation(Lead lead, IReadOnlyList<ConversationMessage> thread, string? instruction)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"LEAD: {lead.Name}");
        sb.AppendLine();
        sb.AppendLine("CONVERSACIÓN:");
        var ordered = thread.OrderBy(m => m.Timestamp).ToList();
        var recent = ordered.Count <= MaxThreadMessages
            ? ordered
            : ordered.GetRange(ordered.Count - MaxThreadMessages, MaxThreadMessages);
        foreach (var m in recent)
        {
            var who = m.Direction == MessageDirection.Outbound ? "VENDEDOR" : "LEAD";
            sb.AppendLine($"{who}: {m.Text}");
        }
        sb.AppendLine();
        sb.AppendLine(instruction ?? "Generá la próxima respuesta del vendedor:");
        return sb.ToString();
    }

    // ---- Guardrail de precio --------------------------------------------------

    // Tokens tipo precio: "$20.000", "USD 49", "20.000 pesos", "ARS 1.500", "€10".
    private static readonly Regex PriceLike = new(
        @"(?:\$|us\$|usd|ars|€)\s?\d[\d.,]*|\d[\d.,]*\s?(?:usd|pesos|ars|dólares|dolares|€)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// True si la respuesta menciona algún monto de precio que no figura en el
    /// PriceDisplay real. Si PriceDisplay no tiene número (ej. "a confirmar"),
    /// cualquier monto de precio en la respuesta cuenta como inventado.
    /// </summary>
    private static bool MentionsWrongPrice(string reply, string? priceDisplay)
    {
        var allowed = AmountsIn(priceDisplay);
        foreach (Match m in PriceLike.Matches(reply))
        {
            var amount = OnlyDigits(m.Value);
            if (amount.Length > 0 && !allowed.Contains(amount))
                return true;
        }
        return false;
    }

    private static HashSet<string> AmountsIn(string? s) =>
        new(Regex.Matches(s ?? string.Empty, @"\d[\d.,]*")
            .Select(m => OnlyDigits(m.Value))
            .Where(v => v.Length > 0));

    private static string OnlyDigits(string s) => new(s.Where(char.IsDigit).ToArray());
}
