using System.Text.RegularExpressions;

namespace SalesHub.Infrastructure.Arca;

/// <summary>
/// Qué escribir en Google Maps para encontrar negocios de un rubro ARCA (CLAE F.883), y a qué app
/// de efcloud le sirven. Lo usa el userscript de captura (SalesHub Maps Capture): el vendedor elige
/// un rubro del nomenclador, el panel muestra estos términos y cada uno abre una búsqueda en Maps.
/// Para los códigos sin términos cargados a mano se arma uno desde la descripción oficial.
/// </summary>
public static partial class ArcaSearchTerms
{
    private static readonly Dictionary<string, string[]> Curated = new()
    {
        // Deporte y fitness
        ["931050"] = new[] { "gimnasio", "pilates", "yoga", "crossfit", "entrenamiento funcional", "box de crossfit", "spinning" },
        ["854950"] = new[] { "escuela de natación", "artes marciales", "taekwondo", "karate", "boxeo", "escuela de fútbol", "gimnasia artística" },
        ["854960"] = new[] { "escuela de danza", "estudio de danza", "escuela de música", "academia de baile" },
        ["931010"] = new[] { "club deportivo", "club de pádel", "club de tenis", "club de fútbol" },
        ["931020"] = new[] { "cancha de pádel", "cancha de fútbol 5", "complejo deportivo", "cancha de tenis", "cancha de squash" },
        ["931042"] = new[] { "personal trainer", "entrenador personal" },
        ["931090"] = new[] { "escuela de surf", "escalada", "centro deportivo" },

        // Belleza y cuidado personal
        ["960201"] = new[] { "peluquería", "barbería", "peluquería unisex" },
        ["960202"] = new[] { "salón de belleza", "manicuría", "uñas", "pestañas", "depilación", "cosmetología" },
        ["960910"] = new[] { "centro de estética", "spa", "masajes", "estética corporal" },
        ["960990"] = new[] { "estudio de tatuajes", "piercing" },

        // Salud
        ["862200"] = new[] { "odontólogo", "consultorio odontológico", "clínica dental", "ortodoncia" },
        ["869010"] = new[] { "kinesiólogo", "kinesiología", "fisioterapia", "rehabilitación" },
        ["869090"] = new[] { "psicólogo", "nutricionista", "fonoaudiólogo", "podólogo", "psicopedagoga" },
        ["862110"] = new[] { "consultorio médico", "centro médico", "dermatólogo", "pediatra" },
        ["750000"] = new[] { "veterinaria" },

        // Construcción y profesionales
        ["711001"] = new[] { "estudio de arquitectura", "arquitecto", "ingeniero civil" },
        ["711009"] = new[] { "estudio de ingeniería" },
        ["410011"] = new[] { "constructora", "empresa constructora", "reformas" },
        ["410021"] = new[] { "constructora de obras comerciales" },
        ["682091"] = new[] { "inmobiliaria" },
        ["691001"] = new[] { "estudio jurídico", "abogado" },
        ["692000"] = new[] { "estudio contable", "contador" },

        // Comercio
        ["469090"] = new[] { "distribuidora", "mayorista", "importadora" },
        ["479101"] = new[] { "tienda online" },
        ["477190"] = new[] { "tienda de ropa", "indumentaria", "boutique" },
        ["477140"] = new[] { "indumentaria deportiva" },
        ["477220"] = new[] { "zapatería" },
        ["475230"] = new[] { "ferretería" },
        ["477310"] = new[] { "farmacia" },
        ["477410"] = new[] { "óptica" },
        ["477470"] = new[] { "pet shop", "forrajería" },
        ["472120"] = new[] { "dietética" },
        ["472171"] = new[] { "panadería" },
        ["472130"] = new[] { "carnicería" },
        ["472160"] = new[] { "verdulería" },
        ["471120"] = new[] { "supermercado" },
        ["471130"] = new[] { "almacén", "autoservicio", "minimercado" },
        ["471190"] = new[] { "kiosco", "polirrubro" },

        // Gastronomía y alojamiento
        ["561011"] = new[] { "restaurante", "parrilla", "bodegón" },
        ["561013"] = new[] { "hamburguesería", "comida rápida" },
        ["561014"] = new[] { "bar", "cafetería", "cervecería" },
        ["561020"] = new[] { "pizzería", "rotisería", "casa de empanadas" },
        ["561030"] = new[] { "heladería" },
        ["551022"] = new[] { "hotel", "hostería", "apart hotel" },

        // Educación
        ["854910"] = new[] { "instituto de inglés", "escuela de idiomas" },
        ["854990"] = new[] { "apoyo escolar", "academia" },
    };

    /// <summary>App de efcloud que le sirve a cada rubro (por código exacto o por prefijo de grupo/división).</summary>
    private static readonly (string Prefix, string Product)[] ProductByPrefix =
    {
        ("931050", "gymhero"), ("931041", "gymhero"), ("931042", "gymhero"), ("854950", "gymhero"), ("854960", "gymhero"),
        ("931010", "playcrew"), ("931020", "playcrew"), ("931090", "playcrew"),
        ("9602", "turnospro"), ("9609", "turnospro"), ("862", "turnospro"), ("869", "turnospro"), ("750000", "turnospro"),
        ("7110", "construction"), ("410", "construction"),
        ("46", "unistock"), ("47", "unistock"),
    };

    public static string? SuggestedProduct(string code) =>
        ProductByPrefix.Where(p => code.StartsWith(p.Prefix, StringComparison.Ordinal))
            .OrderByDescending(p => p.Prefix.Length)
            .Select(p => p.Product)
            .FirstOrDefault();

    public static IReadOnlyList<string> For(string code, string description)
    {
        if (Curated.TryGetValue(code, out var terms)) return terms;
        var derived = FromDescription(description);
        return string.IsNullOrWhiteSpace(derived) ? Array.Empty<string>() : new[] { derived };
    }

    /// <summary>
    /// "Venta al por menor de artículos de librería y papelería (Incluye ...)" → "artículos de librería y papelería".
    /// Saca el paréntesis, los "n.c.p." y los encabezados genéricos ("Servicios de", "Venta al por menor de", …).
    /// </summary>
    internal static string FromDescription(string description)
    {
        var s = description;
        var paren = s.IndexOf('(');
        if (paren > 0) s = s[..paren];
        s = NcpRegex().Replace(s, " ");
        s = PrefixRegex().Replace(s.Trim(), "");
        s = WhitespaceRegex().Replace(s, " ").Trim().TrimEnd(',', '.', ';');
        return s.Length == 0 ? s : char.ToLowerInvariant(s[0]) + s[1..];
    }

    [GeneratedRegex(@"\bn\.\s*c\.\s*p\.?", RegexOptions.IgnoreCase)]
    private static partial Regex NcpRegex();

    [GeneratedRegex(@"^(servicios?\s+(de\s+|para\s+(la\s+)?|relacionados\s+con\s+(la\s+|el\s+)?)?|venta\s+al\s+por\s+(menor|mayor)\s+(de\s+|en\s+)?|enseñanza\s+(de\s+)?|explotación\s+de\s+|fabricación\s+de\s+)", RegexOptions.IgnoreCase)]
    private static partial Regex PrefixRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();
}
