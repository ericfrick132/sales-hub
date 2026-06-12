using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using SalesHub.Core.Domain.Entities;

namespace SalesHub.Infrastructure.Services;

/// <summary>
/// Arma el prompt de venta y le pide a Claude la próxima respuesta para el lead.
/// Inyecta el precio REAL del producto (Product.PriceDisplay) y valida la salida
/// con un guardrail: si la IA menciona un precio que no coincide, reintenta y, si
/// insiste, descarta la sugerencia (no propagamos un precio inventado).
/// </summary>
public class AiSuggestionService
{
    private readonly ClaudeClient _claude;
    private readonly ILogger<AiSuggestionService> _log;

    public AiSuggestionService(ClaudeClient claude, ILogger<AiSuggestionService> log)
    {
        _claude = claude;
        _log = log;
    }

    /// <summary>True si Claude está configurado — el worker no sugiere si no.</summary>
    public bool IsConfigured => _claude.IsConfigured;

    public async Task<string?> SuggestReplyAsync(
        Lead lead, Product product, IReadOnlyList<ConversationMessage> thread, CancellationToken ct)
    {
        if (thread.Count == 0) return null;
        var system = BuildSystemPrompt(product);
        var conversation = BuildConversation(lead, thread);

        var reply = await _claude.CompleteAsync(system, conversation, ct);
        if (string.IsNullOrWhiteSpace(reply)) return null;

        // Guardrail de precio: si menciona un precio que no es el real, reintentar.
        if (MentionsWrongPrice(reply, product.PriceDisplay))
        {
            _log.LogWarning(
                "IA mencionó un precio que no coincide con '{Price}' para {Product}; reintentando.",
                product.PriceDisplay, product.DisplayName);

            var corrected = conversation +
                $"\n\nIMPORTANTE: el precio EXACTO es \"{product.PriceDisplay}\". " +
                "No menciones NINGÚN otro número de precio. Regenerá la respuesta:";
            reply = await _claude.CompleteAsync(system, corrected, ct);

            if (string.IsNullOrWhiteSpace(reply) || MentionsWrongPrice(reply, product.PriceDisplay))
            {
                _log.LogWarning(
                    "IA insistió con un precio incorrecto para {Product}; se descarta la sugerencia.",
                    product.DisplayName);
                return null;
            }
        }

        return reply;
    }

    /// <summary>Parte estática (se cachea): instrucciones base + contexto del producto.</summary>
    private static string BuildSystemPrompt(Product product)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Sos un vendedor argentino real escribiendo por WhatsApp (NO un bot). Generás SOLO");
        sb.AppendLine("el texto de la próxima respuesta del vendedor — sin comillas, sin prefijos tipo");
        sb.AppendLine("\"Vendedor:\", sin explicaciones.");
        sb.AppendLine();
        sb.AppendLine("CÓMO ESCRIBÍS (que no suene robot):");
        sb.AppendLine("- Cortito, como un WhatsApp real: 1-2 líneas. Nada de párrafos largos ni formales.");
        sb.AppendLine("- Tono argentino, de vos, cercano. Contracciones y muletillas naturales (dale, mirá, bárbaro).");
        sb.AppendLine("- Cero corporativo: nada de \"estimado\", \"no dude en\", \"quedamos a su disposición\".");
        sb.AppendLine("- Espejá el tono del lead: si es seco sé directo, si es relajado relajate.");
        sb.AppendLine("- Variá las aperturas, no repitas siempre la misma frase. Emojis con moderación (0-1).");
        sb.AppendLine("- Una idea por mensaje; si hace falta más, mejor preguntá algo y seguí la charla.");
        sb.AppendLine("- No inventes datos que no estén en el contexto. Si no sabés algo, no lo afirmes.");
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
        if (!string.IsNullOrWhiteSpace(product.AiSalesPlaybook))
        {
            sb.AppendLine();
            sb.AppendLine("REGLAS Y PLAYBOOK DE ESTE PRODUCTO (prioritarias, seguilas al pie):");
            sb.AppendLine(product.AiSalesPlaybook);
        }
        return sb.ToString();
    }

    /// <summary>Parte variable: el hilo de la conversación.</summary>
    private static string BuildConversation(Lead lead, IReadOnlyList<ConversationMessage> thread)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"LEAD: {lead.Name}");
        sb.AppendLine();
        sb.AppendLine("CONVERSACIÓN:");
        foreach (var m in thread.OrderBy(m => m.Timestamp))
        {
            var who = m.Direction == MessageDirection.Outbound ? "VENDEDOR" : "LEAD";
            sb.AppendLine($"{who}: {m.Text}");
        }
        sb.AppendLine();
        sb.AppendLine("Generá la próxima respuesta del vendedor:");
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
