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
        => RenderTemplate(product.MessageTemplate ?? string.Empty, lead, product, seller);

    public string? RenderOpener(Lead lead, Product product, Seller? seller = null)
    {
        if (string.IsNullOrWhiteSpace(product.OpenerTemplate)) return null;
        var rendered = RenderTemplate(product.OpenerTemplate, lead, product, seller);
        return string.IsNullOrWhiteSpace(rendered) ? null : rendered;
    }

    private static string RenderTemplate(string template, Lead lead, Product product, Seller? seller)
    {
        // Spin-text first so placeholder substitution still works.
        // Seed estable por lead+template para que opener y main no terminen siempre con el mismo
        // saludo (cambiamos un bit con el hash del template).
        var seed = lead.Id.GetHashCode() ^ template.GetHashCode();
        var rnd = new Random(seed);
        while (true)
        {
            var m = SpinRx.Match(template);
            if (!m.Success) break;
            var options = m.Groups[1].Value.Split('|', StringSplitOptions.TrimEntries);
            var choice = options[rnd.Next(options.Length)];
            template = template.Substring(0, m.Index) + choice + template.Substring(m.Index + m.Length);
        }

        // {category} = la categoría/búsqueda que originó el lead (ej. "arquitecta",
        // "constructora", "gimnasio"). Útil para personalizar: "Vi tu {category}".
        // Cae a la primera categoría del producto si el lead no tiene nada.
        var category = !string.IsNullOrWhiteSpace(lead.SearchCategory)
            ? lead.SearchCategory!
            : (product.Categories?.FirstOrDefault() ?? "negocio");

        template = template
            .Replace("{name}", lead.Name)
            .Replace("{city}", string.IsNullOrWhiteSpace(lead.City) ? "tu ciudad" : lead.City)
            .Replace("{province}", lead.Province ?? string.Empty)
            .Replace("{category}", category)
            .Replace("{search_query}", lead.SearchQuery ?? string.Empty)
            .Replace("{price}", product.PriceDisplay ?? string.Empty)
            .Replace("{checkout_url}", product.CheckoutUrl ?? string.Empty)
            .Replace("{seller}", seller?.DisplayName ?? "Eric")
            .Replace("\\n", "\n");

        return template.Trim();
    }
}
