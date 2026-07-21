using System.Text.RegularExpressions;
using SalesHub.Core.Abstractions;
using SalesHub.Core.Domain.Entities;

namespace SalesHub.Infrastructure.Services;

/// <summary>
/// Renders a product's message_template with placeholders and spin-text.
/// Placeholders: {name}, {nombre}, {saludo}, {city}, {province}, {price}, {checkout_url}, {seller}
/// Spin syntax: {opt1|opt2|opt3} -> random pick, stable per lead id.
/// </summary>
public class MessageRenderer : IMessageRenderer
{
    private static readonly Regex SpinRx = new(@"\{([^{}]*\|[^{}]*)\}", RegexOptions.Compiled);
    private static readonly Regex MultiSpaceRx = new(@"[ \t]{2,}", RegexOptions.Compiled);

    // Nombres "basura" que NO son de una persona (leads de form/reengage sin nombre real).
    // Con estos {nombre} queda vacío para que el saludo lea bien ("buenos días" sin "Mi negocio").
    private static readonly HashSet<string> JunkNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "mi negocio", "negocio", "lead", "lead de anuncio", "contacto whatsapp", "contacto",
        "cliente", "sin nombre", "n/a", "na", "test", "prueba",
        // Tokens sueltos que delatan razón social y no nombre de pila ("Mi gimnasio" -> "Mi"
        // se colaba como nombre; caso real del canary 2026-07-20). También se chequean contra
        // el PRIMER token, así que un artículo/posesivo o rubro acá corta el saludo con nombre.
        "mi", "tu", "su", "el", "la", "los", "las", "un", "una", "nuevo", "nueva",
        "gym", "gimnasio", "club", "fitness", "centro", "estudio", "salon", "salón",
        "barberia", "barbería", "peluqueria", "peluquería", "estetica", "estética",
        "spa", "canchas", "padel", "pádel", "obra", "obras", "tienda", "local", "distribuidora"
    };

    /// <summary>Primer nombre presentable, o "" si el nombre es basura/negocio/vacío.
    /// "Santi Pérez" -> "Santi"; "Mi negocio" -> ""; "GYM NEW LIFE" -> "".</summary>
    private static string FirstName(string? raw)
    {
        var name = (raw ?? string.Empty).Trim();
        if (name.Length == 0 || JunkNames.Contains(name)) return string.Empty;
        var first = name.Split(new[] { ' ', ',', '.' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
        // Descartá tokens que no parecen nombre de pila: con dígitos, muy cortos, o TODO EN MAYÚS
        // (típico de razón social scrapeada: "ENERGYM", "GYM NEW LIFE").
        if (first.Length < 2 || first.Any(char.IsDigit) || first.All(c => !char.IsLetter(c) || char.IsUpper(c)))
            return string.Empty;
        if (JunkNames.Contains(first)) return string.Empty;
        return char.ToUpper(first[0]) + first[1..].ToLower();
    }

    /// <summary>Saludo por hora en la zona del vendedor: mañana/tarde/noche.</summary>
    private static string TimeGreeting(Seller? seller)
    {
        var tz = TimeZoneInfo.Utc;
        var id = seller?.Timezone;
        if (!string.IsNullOrWhiteSpace(id))
        {
            try { tz = TimeZoneInfo.FindSystemTimeZoneById(id); } catch { /* fallback UTC-3 abajo */ }
        }
        if (tz == TimeZoneInfo.Utc) tz = TimeZoneInfo.CreateCustomTimeZone("ar", TimeSpan.FromHours(-3), "AR", "AR");
        var h = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, tz).Hour;
        return h < 12 ? "buenos días" : h < 20 ? "buenas tardes" : "buenas noches";
    }

    public string Render(Lead lead, Product product, Seller? seller = null)
        => RenderTemplateInternal(product.MessageTemplate ?? string.Empty, lead, product, seller);

    public string? RenderOpener(Lead lead, Product product, Seller? seller = null)
    {
        if (string.IsNullOrWhiteSpace(product.OpenerTemplate)) return null;
        var rendered = RenderTemplateInternal(product.OpenerTemplate, lead, product, seller);
        return string.IsNullOrWhiteSpace(rendered) ? null : rendered;
    }

    public string RenderTemplate(string template, Lead lead, Product product, Seller? seller = null)
        => RenderTemplateInternal(template, lead, product, seller);

    private static string RenderTemplateInternal(string template, Lead lead, Product product, Seller? seller)
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
            .Replace("{nombre}", FirstName(lead.Name))
            .Replace("{saludo}", TimeGreeting(seller))
            .Replace("{city}", string.IsNullOrWhiteSpace(lead.City) ? "tu ciudad" : lead.City)
            .Replace("{province}", lead.Province ?? string.Empty)
            .Replace("{category}", category)
            .Replace("{search_query}", lead.SearchQuery ?? string.Empty)
            .Replace("{price}", product.PriceDisplay ?? string.Empty)
            .Replace("{checkout_url}", product.CheckoutUrl ?? string.Empty)
            .Replace("{seller}", seller?.DisplayName ?? "Eric")
            .Replace("\\n", "\n");

        // Limpieza para cuando {nombre} queda vacío: colapsá espacios dobles y arreglá
        // los artefactos " ," / " !" / " ?" para que "hola {nombre}, {saludo}" lea bien igual.
        template = MultiSpaceRx.Replace(template, " ")
            .Replace(" ,", ",").Replace(" .", ".").Replace(" !", "!").Replace(" ?", "?")
            .Replace(",,", ",");

        return template.Trim();
    }
}
