using System.Text.RegularExpressions;
using SalesHub.Core.Abstractions;
using SalesHub.Core.Domain.Entities;

namespace SalesHub.Infrastructure.Services;

/// <summary>
/// Renders a product's message_template with placeholders and spin-text.
/// Placeholders: {name}, {city}, {province}, {price}, {checkout_url}, {seller}
/// Spin syntax: {opt1|opt2|opt3} -> random pick, stable per lead id.
/// </summary>
public class MessageRenderer : IMessageRenderer
{
    private static readonly Regex SpinRx = new(@"\{([^{}]*\|[^{}]*)\}", RegexOptions.Compiled);

    public string Render(Lead lead, Product product, Seller? seller = null)
    {
        var template = product.MessageTemplate ?? string.Empty;

        // Spin-text first so placeholder substitution still works
        var seed = lead.Id.GetHashCode();
        var rnd = new Random(seed);
        while (true)
        {
            var m = SpinRx.Match(template);
            if (!m.Success) break;
            var options = m.Groups[1].Value.Split('|', StringSplitOptions.TrimEntries);
            var choice = options[rnd.Next(options.Length)];
            template = template.Substring(0, m.Index) + choice + template.Substring(m.Index + m.Length);
        }

        // Placeholders
        template = template
            .Replace("{name}", lead.Name)
            .Replace("{city}", string.IsNullOrWhiteSpace(lead.City) ? "tu ciudad" : lead.City)
            .Replace("{province}", lead.Province ?? string.Empty)
            .Replace("{price}", product.PriceDisplay ?? string.Empty)
            .Replace("{checkout_url}", product.CheckoutUrl ?? string.Empty)
            .Replace("{seller}", seller?.DisplayName ?? "Eric")
            .Replace("\\n", "\n");

        return template.Trim();
    }
}
