using System.Text.RegularExpressions;
using SalesHub.Core.Domain.Entities;

namespace SalesHub.Infrastructure.Services;

/// <summary>
/// Decide qué haría el bot con un mensaje del lead según el diccionario. Lo usa la simulación en
/// vivo (se guarda al lado del mensaje, no se manda nada) y, cuando se prenda "responde solo", el
/// envío real: la misma lógica en los dos lados, así lo que se midió es lo que sale.
/// </summary>
public static class IntentReplyPlanner
{
    public const string NoReply = "sin_respuesta";
    public const string Reply = "respuesta";
    public const string ToHuman = "ia_humano";

    private static readonly Regex MissingData = new(@"\{(link|precio|alias|video|h1|h2|horario)\}", RegexOptions.Compiled);
    private static readonly Regex Spin = new(@"\{([^{}]*\|[^{}]*)\}", RegexOptions.Compiled);

    // Reglas que salieron de comparar la simulación con lo que respondió de verdad un vendedor
    // sobre 8.074 mensajes (2026-09-30). Todas sobre texto normalizado (IntentText).
    /// <summary>Nuestro último mensaje cerró la charla: recién ahí un "ok/gracias" no se contesta.</summary>
    private static readonly Regex Closing = new(@"cualquier (cosa|duda)|(que )?andes bien|suerte|abrazo|exitos|a disposicion|estamos (aca|en contacto)|cuando lo necesites|buen (dia|finde|fin de semana)|saludos|nos hablamos|hablamos", RegexOptions.Compiled);
    /// <summary>Nuestro último mensaje le pedía algo (mail, datos): un "sí" ahí es un sí a eso, no un cierre.</summary>
    private static readonly Regex AsksForSomething = new(@"(me )?pasas|pasame|dejame|decime|contame|mandame|tu mail|el mail|tu nombre|el nombre", RegexOptions.Compiled);
    /// <summary>Ya dio una fecha u horario: no se le vuelve a preguntar "para cuándo".</summary>
    private static readonly Regex HasWhen = new(@"\bN\b|manana|hoy|pasado|lunes|martes|miercoles|jueves|viernes|sabado|domingo|semana|enero|febrero|marzo|abril|mayo|junio|julio|agosto|septiembre|setiembre|octubre|noviembre|diciembre|fin de mes", RegexOptions.Compiled);
    /// <summary>Amenaza o insulto: no se contesta nada.</summary>
    private static readonly Regex Hostile = new(@"denunci|bloque|culo|mierda|forro|pelotud|boludo|la concha|spam", RegexOptions.Compiled);
    /// <summary>Pregunta si es un bot / persona: lo contesta un humano (el bot no dice que es una persona).</summary>
    private static readonly Regex AsksIfBot = new(@"\bbot\b|robot|\bia\b|inteligencia artificial|virtual|humano|persona real|sos real", RegexOptions.Compiled);
    /// <summary>Pregunta algo puntual de la prueba (desde dónde, con qué equipo): la respuesta fija no alcanza.</summary>
    private static readonly Regex TrialSpecific = new(@"desde (la |el |una |un )?(pc|compu|celu|computadora|tablet|web|app)|en la (pc|compu)|con (la |el )?(pc|compu|celu)", RegexOptions.Compiled);

    /// <summary>
    /// Casos donde la respuesta del tipo no tiene sentido en ESE momento de la charla (aunque el tipo
    /// esté bien): devuelve el plan corregido o null si no aplica ninguna regla.
    /// </summary>
    private static Plan? ContextOverride(string key, string normText, string? normPrev)
    {
        switch (key)
        {
            case "ack":
                // En medio de la charla el vendedor sigue con la próxima pregunta: callarse solo si ya se despidió.
                if (normPrev is null || !Closing.IsMatch(normPrev) || AsksForSomething.IsMatch(normPrev))
                    return new(ToHuman, null, "la charla sigue: hay que continuar el guion");
                return new(NoReply, null, "confirmación después de un cierre");
            case "dato_calificacion":
                return new(ToHuman, null, "hay que seguir con la próxima pregunta del guion");
            case "anuncio":
            case "email":
                return new(ToHuman, null, "lo maneja el bot de alta (onboarding)");
            case "horario":
                if (normPrev is null || !IntentMatcher.OfferedCall.IsMatch(normPrev)) return new(ToHuman, null, "no le habíamos ofrecido una llamada");
                return null;
            case "mas_adelante":
                if (HasWhen.IsMatch(normText)) return new(ToHuman, null, "ya dijo cuándo");
                return null;
            case "no_audio":
                return new(ToHuman, null, "hay que escribirle el contenido del audio");
            case "rechazo":
                if (Hostile.IsMatch(normText)) return new(NoReply, null, "amenaza o insulto: no se contesta");
                return null;
            case "desconfianza":
                if (AsksIfBot.IsMatch(normText)) return new(ToHuman, null, "pregunta si es un bot: contesta una persona");
                return null;
            case "prueba":
                if (TrialSpecific.IsMatch(normText)) return new(ToHuman, null, "pregunta algo puntual de la prueba");
                return null;
            default:
                return null;
        }
    }

    public record Plan(string Action, string? Text, string Why);

    /// <param name="messageId">Semilla de la variante: se elige por mensaje, no por lead (no repite la misma frase).</param>
    /// <param name="sellerDisplayName">Vendedor dueño de la línea del lead ("Mateo" → "mateo").</param>
    public static Plan Decide(string key, bool confident, ReplyIntent? rule, string? previousOutbound,
        Guid messageId, string? sellerDisplayName, string? productKey, string? productName, string? messageText = null)
    {
        if (!confident) return new(ToHuman, null, "no es seguro");
        if (rule is null || !rule.Enabled) return new(ToHuman, null, "tipo apagado");
        var over = ContextOverride(key, IntentText.Normalize(messageText),
            previousOutbound is null ? null : IntentText.Normalize(previousOutbound));
        if (over is not null) return over;

        var reply = productKey is not null && rule.ReplyByProduct.TryGetValue(productKey, out var own) && !string.IsNullOrWhiteSpace(own)
            ? own
            : rule.Reply;

        if (string.IsNullOrWhiteSpace(reply))
        {
            if (!rule.Action.StartsWith("no_responder", StringComparison.Ordinal))
                return new(ToHuman, null, "sin respuesta cargada");
            // Un "dale/si" a una pregunta o propuesta nuestra no cierra nada: es una respuesta a seguir.
            if (previousOutbound is not null &&
                (previousOutbound.Contains('?') || IntentMatcher.OfferedCall.IsMatch(IntentText.Normalize(previousOutbound))))
                return new(ToHuman, null, "contesta a una pregunta nuestra");
            return new(NoReply, null, "confirmación suelta");
        }

        if (MissingData.IsMatch(reply)) return new(ToHuman, null, "la respuesta necesita un dato (precio, link…)");
        var vendedor = RemarketingService.SellerFirstName(sellerDisplayName);
        if (reply.Contains("{vendedor}") && vendedor is null) return new(ToHuman, null, "el vendedor no tiene nombre de pila");

        var rnd = new Random(messageId.GetHashCode());
        var text = Spin.Replace(reply, m =>
        {
            var options = m.Groups[1].Value.Split('|');
            return options[rnd.Next(options.Length)].Trim();
        });
        text = text.Replace("{vendedor}", vendedor ?? "").Replace("{producto}", (productName ?? "").ToLowerInvariant());
        if (text.Contains('{')) return new(ToHuman, null, "quedó un dato sin completar");
        return new(Reply, CopyStyle.Clean(text), "respuesta del diccionario");
    }
}
