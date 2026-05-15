using System.Text;
using SalesHub.Core.Domain.Entities;

namespace SalesHub.Infrastructure.Services;

/// <summary>
/// Arma el prompt de venta y le pide a Claude la próxima respuesta sugerida
/// para el vendedor. Modo asistido: devuelve texto, el vendedor decide.
/// </summary>
public class AiSuggestionService
{
    private readonly ClaudeClient _claude;

    public AiSuggestionService(ClaudeClient claude) { _claude = claude; }

    /// <summary>True si Claude está configurado — el worker no sugiere si no.</summary>
    public bool IsConfigured => _claude.IsConfigured;

    public async Task<string?> SuggestReplyAsync(
        Lead lead, Product product, IReadOnlyList<ConversationMessage> thread, CancellationToken ct)
    {
        if (thread.Count == 0) return null;
        var system = BuildSystemPrompt(product);
        var conversation = BuildConversation(lead, thread);
        return await _claude.CompleteAsync(system, conversation, ct);
    }

    /// <summary>Parte estática (se cachea): instrucciones base + contexto del producto.</summary>
    private static string BuildSystemPrompt(Product product)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Sos un asistente de ventas que ayuda a un vendedor a responder por WhatsApp.");
        sb.AppendLine("Te paso la conversación con un lead y vos generás SOLO el texto de la próxima");
        sb.AppendLine("respuesta del vendedor — sin comillas, sin prefijos tipo \"Vendedor:\", sin");
        sb.AppendLine("explicaciones. Tono argentino, cercano y breve, como un WhatsApp real. No");
        sb.AppendLine("inventes datos que no estén en el contexto. Si el lead pide algo que no podés");
        sb.AppendLine("resolver con la info dada, sugerí una respuesta que mantenga viva la charla.");
        sb.AppendLine();
        sb.AppendLine($"PRODUCTO: {product.DisplayName}");
        if (!string.IsNullOrWhiteSpace(product.PriceDisplay))
            sb.AppendLine($"PRECIO: {product.PriceDisplay}");
        if (!string.IsNullOrWhiteSpace(product.CheckoutUrl))
            sb.AppendLine($"LINK DE CHECKOUT: {product.CheckoutUrl}");
        if (!string.IsNullOrWhiteSpace(product.AiSalesPlaybook))
        {
            sb.AppendLine();
            sb.AppendLine("PLAYBOOK DE VENTA (seguí estas instrucciones):");
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
}
