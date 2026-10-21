using System.Text.RegularExpressions;

namespace SalesHub.Infrastructure.Services;

/// <summary>
/// ¿El último mensaje del lead cierra la charla? Un "gracias", "dale saludos", "ok quedo en espera" o un
/// "no gracias" no espera respuesta: sin esto la cola de "esperando respuesta" se llenaba de despedidas y
/// rechazos de hace días (21/10/2026: 8 de 11 chats "esperando" eran eso).
/// </summary>
public static class ClosingReply
{
    /// <summary>Palabras de cortesía/acuse: un mensaje corto hecho SOLO de estas es un cierre.</summary>
    private static readonly HashSet<string> Courtesy = new(StringComparer.Ordinal)
    {
        "gracias", "graciass", "muchas", "mil", "ok", "oka", "okey", "okay", "oki", "dale", "dalee", "saludos", "slds",
        "genial", "perfecto", "perfecta", "buenisimo", "buenisima", "barbaro", "listo", "joya", "bueno", "buena",
        "igualmente", "abrazo", "chau", "nos", "vemos", "de", "nada", "a", "vos", "te", "agradezco", "por", "ahora",
        "quedo", "en", "espera", "atento", "atenta", "si", "excelente", "re", "bien", "va", "lo", "hablamos",
        "un", "beso", "besos", "y", "hasta", "luego", "pronto", "super", "tranqui", "todo", "claro",
    };

    /// <summary>Frases de rechazo: en un mensaje corto, alcanzan aunque haya otras palabras.</summary>
    private static readonly string[] Rejections =
    {
        "no gracias", "no me interesa", "no nos interesa", "no estoy interesad", "no estamos interesad",
        "no lo vamos a querer", "no vamos a querer", "no lo voy a querer", "no lo necesito", "no lo necesitamos",
        "no por ahora", "por ahora no", "te agradezco por ahora", "no voy a contratar", "no vamos a contratar",
    };

    /// <summary>
    /// "no … querer/interesa/necesitamos/contratar" con hasta 3 palabras en el medio (con typos: "no ll vamos a querer").
    /// Sin "quiero" ("no quiero perder clientes" no es un rechazo) ni "no sé si…" (duda, no rechazo).
    /// </summary>
    private static readonly Regex NoWant = new(@"\bno(?! se\b)( \w+){0,3} (querer|queremos|interesa|necesit\w*|contrat\w*)\b",
        RegexOptions.Compiled, TimeSpan.FromMilliseconds(100));

    public static bool IsClosing(string? text, string? intentKey = null)
    {
        if (intentKey is "rechazo" or "no_interesado") return true;
        var norm = IntentText.Normalize(text);
        if (norm.Length == 0) return false;
        var words = norm.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length <= 12 && (Rejections.Any(r => norm.Contains(r, StringComparison.Ordinal)) || NoWant.IsMatch(norm))) return true;
        return words.Length <= 6 && words.All(Courtesy.Contains);
    }
}
