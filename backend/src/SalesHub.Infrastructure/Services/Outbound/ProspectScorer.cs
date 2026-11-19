using System.Globalization;
using System.Text;
using System.Text.Json;
using SalesHub.Core.Domain.Entities;

namespace SalesHub.Infrastructure.Services.Outbound;

/// <summary>
/// Puntaje de PROSPECTO (0-100): qué tan buen cliente de TurnosPro es o puede llegar a ser un negocio,
/// ANTES de hablarle. Existe para la línea outbound, que tiene pocos envíos por día (10): hay que
/// gastarlos en los mejores. Es una función pura sobre los datos del lead (lo que trae Google Maps + lo que
/// se le sacó de la web); el enriquecimiento lo hace <see cref="ProspectEnricher"/>.
///
/// Criterios (decididos con Eric el 19/11/2026):
///   Ubicación 25       zonas de plata de AMBA (CABA norte + zona norte) arriba de todo.
///   Reseñas Maps 25     un negocio activo con clientela (20-300 reseñas) y buena nota; cadenas enormes no.
///   WhatsApp 20         el número lo publica como WhatsApp (wa.me): "vi que toman los turnos por WhatsApp" es cierto.
///   Presencia web 15    web propia, Instagram, Facebook.
///   Rubro 15            uno de los rubros que buscamos, confirmado por el nombre.
///   Competencia −20     ya usa AgendaPro/Fresha/Booksy: resta (Eric: con 10 envíos, ir a los que anotan a mano).
///   Cadena −10          el mismo nombre en 3+ leads.
/// </summary>
public static class ProspectScorer
{
    public sealed record Rubro(string Key, string Label, string[] SearchTerms, string[] Keywords,
        string Plural, string Clientes, string Cobro, string Toman);

    /// <summary>
    /// Los rubros de la tanda (lista de Eric 19/11/2026). Keywords van sin tildes y con ñ→n (se compara
    /// normalizado). Plural/Clientes/Cobro/Toman completan el mensaje de apertura según el rubro.
    /// </summary>
    public static readonly IReadOnlyList<Rubro> Rubros = new[]
    {
        new Rubro("estetica", "Centro de estética", new[] { "centro de estética" },
            new[] { "estetic", "cosmetolog", "spa facial", "medicina estetica", "beauty", "skin" },
            "centros de estetica", "tus clientas", "cobras la seña si queres", "toman"),
        new Rubro("unas", "Uñas", new[] { "salón de uñas", "manicuría" },
            new[] { "unas", "manicur", "nail", "kapping", "esmaltado", "semipermanente", "pedicur" },
            "salones de uñas", "tus clientas", "cobras la seña si queres", "toman"),
        new Rubro("pestanas", "Pestañas", new[] { "extensiones de pestañas" },
            new[] { "pestan", "lash", "cejas", "brow", "lifting" },
            "estudios de pestañas", "tus clientas", "cobras la seña si queres", "toman"),
        new Rubro("depilacion", "Depilación", new[] { "depilación definitiva", "depilación" },
            new[] { "depila", "laser", "descartable", "wax" },
            "centros de depilacion", "tus clientas", "cobras la seña si queres", "toman"),
        new Rubro("psicologia", "Psicólogos", new[] { "psicólogo" },
            new[] { "psicolog", "psicoterap", "psicoanal", "terapia", "terapeuta" },
            "psicologos", "tus pacientes", "cobras la sesion por adelantado si queres", "das"),
        new Rubro("peluqueria", "Peluquería", new[] { "peluquería" },
            new[] { "peluquer", "hair", "estilista", "colorista", "salon de belleza", "peinado" },
            "peluquerias", "tus clientes", "cobras la seña si queres", "toman"),
    };

    public static Rubro? RubroByKey(string? key) => Rubros.FirstOrDefault(r => r.Key == key);

    public sealed record Zone(string Key, string Label, string SearchCity, string SearchProvince,
        double Lat, double Lng, double RadiusKm);

    /// <summary>
    /// Zonas de plata de AMBA (Eric 19/11/2026: CABA norte + zona norte). Son las que se buscan en Places
    /// y las que dan el máximo de ubicación. Centro + radio aproximado de cada barrio/partido.
    /// </summary>
    public static readonly IReadOnlyList<Zone> Zones = new[]
    {
        new Zone("palermo", "Palermo", "Palermo", "CABA", -34.5820, -58.4250, 2.4),
        new Zone("belgrano", "Belgrano", "Belgrano", "CABA", -34.5620, -58.4570, 1.9),
        new Zone("recoleta", "Recoleta", "Recoleta", "CABA", -34.5880, -58.3970, 1.4),
        new Zone("nunez", "Núñez", "Núñez", "CABA", -34.5450, -58.4610, 1.4),
        new Zone("caballito", "Caballito", "Caballito", "CABA", -34.6190, -58.4410, 1.8),
        new Zone("villa-urquiza", "Villa Urquiza", "Villa Urquiza", "CABA", -34.5730, -58.4870, 1.6),
        new Zone("vicente-lopez", "Vicente López", "Vicente López", "Buenos Aires", -34.5270, -58.4900, 3.2),
        new Zone("san-isidro", "San Isidro", "San Isidro", "Buenos Aires", -34.4790, -58.5230, 4.0),
        new Zone("nordelta", "Nordelta / Tigre", "Nordelta, Tigre", "Buenos Aires", -34.4100, -58.6300, 4.5),
        new Zone("pilar", "Pilar", "Pilar", "Buenos Aires", -34.4500, -58.9000, 7.0),
    };

    public static Zone? ZoneByKey(string? key) => Zones.FirstOrDefault(z => z.Key == key);

    private const double ObeliscoLat = -34.6037, ObeliscoLng = -58.3816;

    public sealed record Part(string Key, string Label, int Points, int Max, string Why);

    public sealed record Result(int Score, string? RubroKey, string? ZoneLabel, IReadOnlyList<Part> Parts);

    /// <summary>Lo que el puntaje necesita saber del resto de la base (no sale del lead solo).</summary>
    public sealed record Context(int SameNameCount);

    public static Result Score(Lead lead, Context ctx)
    {
        var parts = new List<Part>();

        // ── Ubicación (25) ─────────────────────────────────────────────────────────────
        var zone = ZoneFor(lead.Latitude, lead.Longitude) ?? ZoneByText(lead);
        if (zone is not null)
            parts.Add(new Part("ubicacion", "Ubicación", 25, 25, zone.Label));
        else if (IsCaba(lead))
            parts.Add(new Part("ubicacion", "Ubicación", 12, 25, "CABA (fuera de las zonas de plata)"));
        else if (lead.Latitude is { } la && lead.Longitude is { } lo && DistanceKm(la, lo, ObeliscoLat, ObeliscoLng) <= 45)
            parts.Add(new Part("ubicacion", "Ubicación", 5, 25, "AMBA"));
        else
            parts.Add(new Part("ubicacion", "Ubicación", 0, 25, string.IsNullOrWhiteSpace(lead.City) ? "sin ubicación" : lead.City!));

        // ── Reseñas de Google Maps (18 cantidad + 7 nota) ───────────────────────────────
        var n = lead.TotalReviews ?? 0;
        var (revPts, revWhy) = n switch
        {
            0 => (0, "sin reseñas"),
            < 5 => (3, $"{n} reseñas (muy nuevo o chico)"),
            < 20 => (9, $"{n} reseñas"),
            < 50 => (14, $"{n} reseñas"),
            < 300 => (18, $"{n} reseñas (activo, con clientela)"),
            < 800 => (12, $"{n} reseñas (grande)"),
            _ => (5, $"{n} reseñas (cadena o muy grande)"),
        };
        var r = lead.Rating;
        var ratePts = r switch { >= 4.7 => 7, >= 4.4 => 5, >= 4.0 => 2, _ => 0 };
        var rateWhy = r is null ? "sin nota" : $"nota {r.Value.ToString("0.0", CultureInfo.InvariantCulture)}";
        parts.Add(new Part("resenas", "Reseñas Maps", revPts + ratePts, 25, $"{revWhy}, {rateWhy}"));

        // ── WhatsApp / de dónde salió el número (20) ───────────────────────────────────
        var (waPts, waWhy) = lead.PhoneOrigin switch
        {
            _ when string.IsNullOrWhiteSpace(lead.WhatsappPhone) => (0, "sin teléfono"),
            "whatsapp-publicado" => (20, "lo publica como WhatsApp en su web (wa.me)"),
            "whatsapp-instagram" => (20, "toma turnos por WhatsApp según su Instagram"),
            "maps-celular" => (14, "celular de Google Maps"),
            "instagram" => (12, "número del Instagram"),
            "web" => (8, "número de su web"),
            "maps-fijo" => (4, "fijo de Google Maps"),
            _ => (6, "número sin origen claro"),
        };
        if (lead.WhatsappValidated && waPts < 12) { waPts = 12; waWhy += " (verificado en WhatsApp)"; }
        parts.Add(new Part("whatsapp", "WhatsApp", waPts, 20, waWhy));

        // ── Presencia digital (15) ─────────────────────────────────────────────────────
        var kind = WebsiteKind(lead.Website);
        var digital = 0;
        var why = new List<string>();
        if (kind == "propia") { digital += 7; why.Add("web propia"); }
        else if (kind == "link-en-bio") { digital += 3; why.Add("link en bio"); }
        if (!string.IsNullOrWhiteSpace(lead.InstagramHandle) || kind == "instagram")
        {
            digital += 5;
            var f = lead.InstagramFollowers;
            if (f is >= 1000) { digital += 3; why.Add($"Instagram ({Followers(f.Value)} seguidores)"); }
            else why.Add(f is null ? "Instagram" : $"Instagram ({f} seguidores)");
        }
        if (!string.IsNullOrWhiteSpace(lead.FacebookUrl) || kind == "facebook") { digital += 3; why.Add("Facebook"); }
        digital = Math.Min(15, digital);
        parts.Add(new Part("digital", "Presencia web", digital, 15, why.Count == 0 ? "nada" : string.Join(", ", why)));

        // ── Rubro (15) ─────────────────────────────────────────────────────────────────
        var (rubro, rubroPts, rubroWhy) = DetectRubro(lead);
        parts.Add(new Part("rubro", "Rubro", rubroPts, 15, rubroWhy));

        // ── Restas ─────────────────────────────────────────────────────────────────────
        if (!string.IsNullOrWhiteSpace(lead.BookingPlatform))
            parts.Add(new Part("competencia", "Usa otro sistema", -20, 0, $"ya usa {lead.BookingPlatform}"));
        if (ctx.SameNameCount >= 3)
            parts.Add(new Part("cadena", "Cadena", -10, 0, $"misma marca o cuenta de Instagram en {ctx.SameNameCount} leads"));

        var score = Math.Clamp(parts.Sum(p => p.Points), 0, 100);
        return new Result(score, rubro?.Key, zone?.Label, parts);
    }

    private static string Followers(int n) => n >= 1000
        ? (n / 1000.0).ToString(n >= 10_000 ? "0" : "0.#", CultureInfo.InvariantCulture) + "k"
        : n.ToString(CultureInfo.InvariantCulture);

    /// <summary>Los orígenes donde "toman turnos por WhatsApp" está verificado.</summary>
    public static readonly string[] VerifiedWhatsappOrigins = { "whatsapp-publicado", "whatsapp-instagram" };

    public static string DetailJson(Result r) => JsonSerializer.Serialize(new
    {
        rubro = r.RubroKey,
        zona = r.ZoneLabel,
        parts = r.Parts.Select(p => new { key = p.Key, label = p.Label, points = p.Points, max = p.Max, why = p.Why }),
    });

    /// <summary>Rubro del lead: primero la categoría con la que se buscó, después el nombre, al final los tipos de Google.</summary>
    public static (Rubro? Rubro, int Points, string Why) DetectRubro(Lead lead)
    {
        var cat = Norm(lead.SearchCategory);
        var name = Norm(lead.Name);
        var fromCat = Rubros.FirstOrDefault(r => Matches(cat, r));
        var fromName = Rubros.FirstOrDefault(r => Matches(name, r));
        if (fromCat is not null)
        {
            var confirmed = Matches(name, fromCat);
            return (fromCat, confirmed ? 15 : 10, confirmed ? $"{fromCat.Label} (el nombre lo confirma)" : fromCat.Label);
        }
        if (fromName is not null) return (fromName, 10, $"{fromName.Label} (por el nombre)");

        var types = lead.Types ?? new List<string>();
        Rubro? fromTypes = types.Contains("hair_care") ? RubroByKey("peluqueria")
            : types.Contains("spa") ? RubroByKey("estetica")
            : types.Contains("beauty_salon") ? RubroByKey("estetica")
            : null;
        if (fromTypes is not null) return (fromTypes, 6, $"{fromTypes.Label} (por la ficha de Google)");
        return (null, 0, string.IsNullOrWhiteSpace(lead.SearchCategory) ? "sin rubro" : $"{lead.SearchCategory} (no es de la tanda)");
    }

    private static bool Matches(string text, Rubro r) =>
        text.Length > 0 && (r.Keywords.Any(k => text.Contains(k, StringComparison.Ordinal))
                            || r.SearchTerms.Any(t => text.Contains(Norm(t), StringComparison.Ordinal)));

    /// <summary>"propia", "instagram", "facebook", "link-en-bio", "reservas" (plataforma de turnos) o null.</summary>
    public static string? WebsiteKind(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        var u = url.ToLowerInvariant();
        if (u.Contains("instagram.com")) return "instagram";
        if (u.Contains("facebook.com") || u.Contains("fb.com")) return "facebook";
        if (BookingPlatformIn(u) is not null) return "reservas";
        string[] bio = { "linktr.ee", "linktree", "beacons.ai", "taplink", "bio.link", "wa.link", "wa.me", "api.whatsapp", "linkin.bio", "lnk.bio", "campsite.bio", "agora.red", "canva.site" };
        if (bio.Any(u.Contains)) return "link-en-bio";
        return "propia";
    }

    /// <summary>
    /// Sistemas de turnos/reservas que se reconocen por su dominio (en la web del negocio o en sus links).
    /// TurnosPro también está: si su web ya usa nuestro link, es cliente.
    /// </summary>
    public static readonly IReadOnlyList<(string Host, string Name)> BookingPlatforms = new[]
    {
        ("turnos-pro.com", "TurnosPro (ya es cliente)"),
        ("agendapro", "AgendaPro"), ("fresha.com", "Fresha"), ("booksy.com", "Booksy"),
        ("reservio", "Reservio"), ("calendly.com", "Calendly"), ("simplybook", "SimplyBook"),
        ("setmore.com", "Setmore"), ("weibook", "Weibook"), ("turnito", "Turnito"),
        ("doctoralia", "Doctoralia"), ("topdoctors", "TopDoctors"), ("vagaro.com", "Vagaro"),
        ("treatwell", "Treatwell"), ("koibox", "Koibox"), ("bewe.", "Bewe"), ("appointy", "Appointy"),
        ("turnonet", "Turnonet"), ("sacarturno", "SacarTurno"), ("miturno", "MiTurno"), ("turnosweb", "TurnosWeb"),
        ("squareup.com/appointments", "Square"), ("mindbody", "Mindbody"), ("wonoma.com", "Wonoma"),
        ("turnau.app", "Turnau"), ("tuturno", "TuTurno"), ("agendaonline", "AgendaOnline"),
    };

    public static string? BookingPlatformIn(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var t = text.ToLowerInvariant();
        foreach (var (host, name) in BookingPlatforms)
            if (t.Contains(host, StringComparison.Ordinal)) return name;
        return null;
    }

    public static Zone? ZoneFor(double? lat, double? lng)
    {
        if (lat is null || lng is null) return null;
        return Zones
            .Select(z => (z, d: DistanceKm(lat.Value, lng.Value, z.Lat, z.Lng)))
            .Where(x => x.d <= x.z.RadiusKm)
            .OrderBy(x => x.d)
            .Select(x => x.z)
            .FirstOrDefault();
    }

    /// <summary>Sin coordenadas: el barrio/partido nombrado en la dirección (Places a veces no trae lat/lng).</summary>
    private static Zone? ZoneByText(Lead lead)
    {
        if (lead.Latitude is not null) return null; // con coordenadas manda la distancia
        var text = Norm($"{lead.Address} {lead.City}");
        return Zones.FirstOrDefault(z => text.Contains(Norm(z.Label.Split('/')[0].Trim()), StringComparison.Ordinal));
    }

    private static bool IsCaba(Lead lead)
    {
        var a = Norm($"{lead.Address} {lead.City} {lead.Province}");
        if (a.Contains("caba") || a.Contains("autonoma de buenos aires") || a.Contains("capital federal")) return true;
        if (System.Text.RegularExpressions.Regex.IsMatch(lead.Address ?? "", @"\bC1\d{3}")) return true;
        // Caja aproximada de la Ciudad.
        return lead.Latitude is >= -34.705 and <= -34.527 && lead.Longitude is >= -58.531 and <= -58.335;
    }

    public static double DistanceKm(double lat1, double lng1, double lat2, double lng2)
    {
        const double R = 6371;
        double Rad(double d) => d * Math.PI / 180;
        var dLat = Rad(lat2 - lat1);
        var dLng = Rad(lng2 - lng1);
        var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2)
              + Math.Cos(Rad(lat1)) * Math.Cos(Rad(lat2)) * Math.Sin(dLng / 2) * Math.Sin(dLng / 2);
        return 2 * R * Math.Asin(Math.Sqrt(a));
    }

    /// <summary>Minúsculas, sin tildes y ñ→n: como se compara en todo el puntaje.</summary>
    public static string Norm(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        var d = s.Trim().ToLowerInvariant().Normalize(NormalizationForm.FormD);
        return new string(d.Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark).ToArray());
    }

    /// <summary>Clave para detectar cadenas: las 2 primeras palabras significativas del nombre.</summary>
    public static string ChainKey(string? name)
    {
        var words = Norm(name).Split(new[] { ' ', '-', '|', '·', ',', '.', '(', ')' }, StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w.Length > 2 && w is not ("los" or "las" or "del" or "the" or "spa" or "salon" or "centro" or "estetica" or "studio" or "estudio"))
            .Take(2)
            .ToList();
        // Con una sola palabra ("nails", "lash") no se puede decir que sea la misma marca.
        return words.Count < 2 ? "" : string.Join(' ', words);
    }
}
