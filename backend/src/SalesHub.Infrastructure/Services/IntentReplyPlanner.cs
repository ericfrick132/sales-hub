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

    public record Plan(string Action, string? Text, string Why);

    /// <param name="messageId">Semilla de la variante: se elige por mensaje, no por lead (no repite la misma frase).</param>
    /// <param name="sellerDisplayName">Vendedor dueño de la línea del lead ("Mateo" → "mateo").</param>
    public static Plan Decide(string key, bool confident, ReplyIntent? rule, string? previousOutbound,
        Guid messageId, string? sellerDisplayName, string? productKey, string? productName)
    {
        if (!confident) return new(ToHuman, null, "no es seguro");
        if (rule is null || !rule.Enabled) return new(ToHuman, null, "tipo apagado");

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
