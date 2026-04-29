using System.Globalization;
using System.Text.RegularExpressions;

namespace SalesHub.Infrastructure.Services;

/// <summary>
/// Parsea el texto crudo que copia un usuario desde el listado de Google Maps
/// (ej. "crossfit recoleta" → resultados). Cada item se ve así:
///   ON FIT Barrio Norte
///   4,8(1084)
///   Centro de gimnasia · 2844 Avenida Santa Fe
///   Abierto · Cierra a las 11 p. m. · 011 2780-9274
/// El detector ancla en la línea de rating ("4,8(1084)" o "No hay reseñas") y
/// recoge nombre (línea anterior) + dirección/estado/teléfono (líneas siguientes).
/// </summary>
public static class MapsTextParser
{
    public record ParsedItem(
        string Name,
        string? Type,
        string? Address,
        string? Phone,
        double? Rating,
        int? TotalReviews,
        string? BusinessStatus);

    private static readonly Regex RatingRx = new(@"^(\d+),(\d+)\((\d+)\)$", RegexOptions.Compiled);
    private static readonly Regex PhoneRx = new(
        @"(\+?\d[\d\s\-]{7,}\d)",
        RegexOptions.Compiled);

    private static readonly HashSet<string> Skip = new(StringComparer.OrdinalIgnoreCase)
    {
        "Sitio web", "Cómo llegar", "Patrocinado", "Visitar sitio",
        "Reservar en línea", "Volver al principio", "Capas",
        "Resultados", "Compartir", "Buscar en esta zona",
        "Horario", "Todos los filtros", "Has llegado al final de la lista.",
        "Actualizar los resultados al mover el mapa",
        "Click to enable keyboard move mode."
    };

    public static List<ParsedItem> Parse(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return new();

        var lines = input.Replace("\r\n", "\n")
            .Split('\n')
            .Select(l => l.Trim())
            .ToArray();
        var items = new List<ParsedItem>();
        var n = lines.Length;

        for (int i = 0; i < n; i++)
        {
            var line = lines[i];
            if (line.Length == 0) continue;

            var ratingM = RatingRx.Match(line);
            var noReviews = line.Equals("No hay reseñas", StringComparison.OrdinalIgnoreCase)
                            || line.Equals("Sin reseñas", StringComparison.OrdinalIgnoreCase);
            if (!ratingM.Success && !noReviews) continue;

            // Backtrack hasta encontrar el nombre — última línea no vacía, no skip,
            // no quote, no que parezca status/categoría con "·".
            string? name = null;
            for (int j = i - 1; j >= 0 && j >= i - 6; j--)
            {
                var p = lines[j];
                if (p.Length == 0) continue;
                if (Skip.Contains(p)) continue;
                if (StartsLikeQuote(p)) continue;
                if (LooksLikeRating(p)) continue;
                if (LooksLikeStatusLine(p)) continue;
                name = p;
                break;
            }
            if (name is null) continue;

            double? rating = null;
            int? reviews = null;
            if (ratingM.Success)
            {
                rating = double.Parse(
                    $"{ratingM.Groups[1].Value}.{ratingM.Groups[2].Value}",
                    CultureInfo.InvariantCulture);
                reviews = int.Parse(ratingM.Groups[3].Value);
            }

            string? type = null;
            string? address = null;
            string? phone = null;
            string? bizStatus = null;

            // Forward — recoger tipo/address y status hasta toparse con la siguiente
            // rating line (= nuevo item) o con muchas líneas vacías sin data.
            for (int j = i + 1; j < n && j <= i + 10; j++)
            {
                var next = lines[j];
                if (next.Length == 0) continue;
                if (Skip.Contains(next)) continue;
                if (StartsLikeQuote(next)) continue;
                if (LooksLikeRating(next) || next.Equals("No hay reseñas", StringComparison.OrdinalIgnoreCase))
                {
                    // arrancó otro item
                    break;
                }

                if (next.Contains(" · "))
                {
                    if (LooksLikeStatusLine(next))
                    {
                        bizStatus = ParseBusinessStatus(next);
                        var pm = PhoneRx.Match(next);
                        if (pm.Success)
                        {
                            var candidate = pm.Value.Trim();
                            if (CountDigits(candidate) >= 8) phone = candidate;
                        }
                    }
                    else
                    {
                        // Tipo · (dirección con · opcional)
                        var parts = next.Split(" · ").Select(p => p.Trim()).Where(p => p.Length > 0).ToList();
                        if (parts.Count > 0)
                        {
                            type ??= parts[0];
                            if (parts.Count > 1) address ??= parts[^1];
                        }
                    }
                }
            }

            items.Add(new ParsedItem(name, type, address, phone, rating, reviews, bizStatus));
        }

        return items;
    }

    private static bool LooksLikeRating(string s) => RatingRx.IsMatch(s);

    private static bool StartsLikeQuote(string s) =>
        s.Length > 0 && (s[0] == '"' || s[0] == '“' || s[0] == '«');

    private static bool LooksLikeStatusLine(string s) =>
        s.StartsWith("Abierto", StringComparison.OrdinalIgnoreCase)
        || s.StartsWith("Cerrado", StringComparison.OrdinalIgnoreCase)
        || s.StartsWith("Abre", StringComparison.OrdinalIgnoreCase);

    private static string? ParseBusinessStatus(string s)
    {
        if (s.Contains("Cerrado permanentemente", StringComparison.OrdinalIgnoreCase))
            return "permanently_closed";
        if (s.StartsWith("Cerrado", StringComparison.OrdinalIgnoreCase))
            return "closed_now";
        if (s.StartsWith("Abierto", StringComparison.OrdinalIgnoreCase))
            return "open_now";
        if (s.StartsWith("Abre", StringComparison.OrdinalIgnoreCase))
            return "opens_later";
        return null;
    }

    private static int CountDigits(string s)
    {
        var c = 0;
        foreach (var ch in s) if (char.IsDigit(ch)) c++;
        return c;
    }
}
