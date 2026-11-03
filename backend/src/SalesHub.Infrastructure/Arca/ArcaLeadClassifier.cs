using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SalesHub.Infrastructure.Persistence;

namespace SalesHub.Infrastructure.Arca;

/// <summary>
/// Le pone a cada lead su rubro del nomenclador de ARCA (código CLAE F.883) a partir de lo que ya
/// sabemos del negocio, sin llamar a nadie de afuera. Mira, en este orden, y se queda con la
/// primera regla que matchea:
///   1. la categoría con la que se lo buscó (<c>SearchCategory</c>, ej. "pádel"),
///   2. los tipos de Google Maps (<c>Types</c>, ej. "beauty_salon"),
///   3. el nombre del negocio,
///   4. el texto de la búsqueda (<c>SearchQuery</c>),
///   5. el rubro por defecto del producto, solo en los productos de un único rubro.
/// Si nada matchea queda sin código con regla "sin-match". Los elegidos a mano ("manual") y los que
/// se capturaron buscando un rubro ARCA desde el userscript ("captura:&lt;código&gt;") no se tocan.
///
/// Las reglas van de lo más específico a lo más general (barbería antes que bar, gimnasia artística
/// antes que gimnasio). Cada palabra clave matchea al comienzo de una palabra ("gimnas" ⇒ gimnasio,
/// gimnasios); con "=" adelante tiene que ser la palabra entera ("=bar" no matchea "barbería").
/// Al cambiar las reglas, subir <see cref="RulesVersion"/>: el worker reclasifica todo lo no manual.
/// </summary>
public static class ArcaLeadClassifier
{
    public const string RulesVersion = "2026-11-02";
    public const string ManualRule = "manual";
    public const string NoMatchRule = "sin-match";
    /// <summary>Rubro elegido al capturar en Maps desde el panel del userscript ("captura:931050"): no se reclasifica.</summary>
    public const string CaptureRulePrefix = "captura:";

    private sealed record Rule(string Code, string[] Keywords);

    private static readonly Rule[] Rules =
    {
        // ── Belleza y cuidado personal (antes que "bar" y que salud) ──
        new("960201", new[] { "barberi", "barber", "peluqueri", "peluquer", "hair care", "hair salon", "hairdresser", "estilista", "coiffeur" }),
        new("960202", new[] { "unas", "manicur", "pedicur", "nail", "pestan", "lashes", "cejas", "depilac", "maquillaj", "cosmetolog", "beauty salon", "salon de belleza", "belleza" }),
        new("960910", new[] { "estetica", "=spa", "masaje", "massage", "sauna", "solarium", "beauty" }),
        new("960990", new[] { "tatuaje", "tattoo", "piercing" }),

        // ── Salud ──
        new("862200", new[] { "odontolog", "dentist", "dental", "ortodonc", "implantes dentales" }),
        new("869010", new[] { "kinesiolog", "kinesio", "fisioterap", "physiotherap", "rehabilitacion", "fisiatr" }),
        new("869090", new[] { "psicolog", "psychologist", "nutricion", "nutritionist", "fonoaudiolog", "psicopedagog", "terapia ocupacional", "podolog", "enfermer" }),
        new("750000", new[] { "veterinari", "veterinary" }),
        new("862110", new[] { "consultorio", "centro medico", "clinica", "medico", "doctor", "pediatr", "dermatolog", "ginecolog", "traumatolog" }),

        // ── Deporte: enseñanza y clubes antes que gimnasios y canchas ──
        new("854960", new[] { "danza", "baile", "ballet", "tango", "escuela de musica", "teatro" }),
        new("854950", new[] { "gimnasia artistica", "escuela de futbol", "escuelita de futbol", "natacion", "swimming", "artes marciales", "taekwondo", "karate", "judo", "boxeo", "kickboxing", "jiu jitsu", "jiujitsu", "=mma", "patin", "escuela deportiva" }),
        new("931010", new[] { "club de padel", "club de tenis", "club de futbol", "club deportivo", "club atletico", "sports club", "=club" }),
        new("931020", new[] { "padel", "tenis", "tennis", "squash", "futbol 5", "futbol", "cancha", "complejo deportivo", "sports complex", "stadium", "pickleball" }),
        new("931050", new[] { "gimnas", "=gym", "gyms", "crossfit", "funcional", "fitness", "pilates", "yoga", "musculacion", "entrenamiento", "personal trainer", "entrenador", "coach", "=box", "spinning", "cycling", "calistenia", "running" }),

        // ── Construcción y servicios profesionales ──
        new("711001", new[] { "arquitect", "architect", "ingenieri", "estudio de arquitectura" }),
        new("410011", new[] { "constructora", "construccion", "construction", "general contractor", "contractor", "empresa de construccion", "reformas", "=obra", "=obras" }),
        new("682091", new[] { "inmobiliari", "real estate", "bienes raices" }),
        new("691001", new[] { "abogad", "estudio juridico", "lawyer", "legal" }),
        new("692000", new[] { "contador", "contable", "contabilidad", "accounting", "asesoria fiscal", "impositiv" }),
        new("620100", new[] { "software", "informatic", "desarrollo web", "programacion" }),

        // ── Comercio ──
        new("469090", new[] { "mayorista", "distribuidora", "distribuidor", "importador", "importadora", "wholesale", "al por mayor" }),
        new("479101", new[] { "e commerce", "ecommerce", "tienda online", "tienda virtual", "venta online" }),
        new("477140", new[] { "indumentaria deportiva", "ropa deportiva" }),
        new("477220", new[] { "zapateri", "calzado", "shoe store" }),
        new("477190", new[] { "tienda de ropa", "ropa", "indumentaria", "boutique", "clothing store", "moda" }),
        new("475230", new[] { "ferreteri", "hardware store" }),
        new("477310", new[] { "farmacia", "pharmacy", "drugstore" }),
        new("477410", new[] { "optica", "optician" }),
        new("477470", new[] { "pet shop", "pet store", "forrajeri", "veterinaria y pet" }),
        new("472120", new[] { "dietetica", "almacen natural" }),
        new("472171", new[] { "panaderi", "bakery" }),
        new("472130", new[] { "carniceri", "butcher" }),
        new("472160", new[] { "verduleri", "fruteri" }),
        new("471120", new[] { "supermercado", "supermarket" }),
        new("471130", new[] { "minimercado", "autoservicio", "almacen", "grocery" }),
        new("471190", new[] { "kiosco", "kiosko", "polirrubro", "convenience store" }),

        // ── Gastronomía y alojamiento ──
        new("561030", new[] { "heladeri", "ice cream" }),
        new("561013", new[] { "hamburgues", "fast food", "burger" }),
        new("561020", new[] { "pizzeri", "rotiseri", "empanada", "meal takeaway", "comida para llevar", "delivery" }),
        new("561014", new[] { "=bar", "=bares", "cafe", "cafeteri", "cerveceri", "=pub", "coffee" }),
        new("561011", new[] { "restaurant", "restaurante", "parrilla", "=resto", "bodegon" }),
        new("551022", new[] { "hotel", "hostel", "hosteri", "lodging", "apart hotel", "cabanas" }),

        // ── Educación ──
        new("854910", new[] { "idiomas", "instituto de ingles", "ingles", "english" }),
        new("854990", new[] { "apoyo escolar", "clases particulares", "academia" }),
    };

    /// <summary>
    /// Rubro por defecto, solo para productos de un único rubro (TurnosPro, por ejemplo, cubre
    /// peluquerías, estética, salud y más: sin otra pista, mejor dejarlo sin código).
    /// </summary>
    private static readonly Dictionary<string, string> ProductDefaults = new(StringComparer.OrdinalIgnoreCase)
    {
        ["gymhero"] = "931050",
        ["bunker"] = "931050",
        ["playcrew"] = "931020",
        ["construction"] = "410011",
        ["unistock"] = "469090",
    };

    /// <summary>Todos los códigos que usan las reglas (para validar contra el nomenclador cargado).</summary>
    public static IReadOnlyCollection<string> AllCodes =>
        Rules.Select(r => r.Code).Concat(ProductDefaults.Values).Distinct().ToList();

    public sealed record Input(string ProductKey, string? SearchCategory, IReadOnlyList<string>? Types, string? Name, string? SearchQuery);
    public sealed record Result(string? Code, string Rule);

    public static Result Classify(Input lead)
    {
        var sources = new (string Label, string? Text)[]
        {
            ("categoria", lead.SearchCategory),
            ("tipo", lead.Types is { Count: > 0 } ? string.Join(' ', lead.Types) : null),
            ("nombre", lead.Name),
            ("busqueda", lead.SearchQuery),
        };

        foreach (var (label, text) in sources)
        {
            if (string.IsNullOrWhiteSpace(text)) continue;
            var norm = Normalize(text);
            foreach (var rule in Rules)
            foreach (var kw in rule.Keywords)
            {
                if (Matches(norm, kw)) return new Result(rule.Code, Trim($"{label}:{kw.TrimStart('=')}"));
            }
        }

        if (ProductDefaults.TryGetValue(lead.ProductKey ?? "", out var def))
            return new Result(def, $"producto:{lead.ProductKey}");

        return new Result(null, NoMatchRule);
    }

    /// <summary>
    /// Clasifica los leads que nunca se clasificaron o que se clasificaron con reglas viejas
    /// (salvo los manuales). Lee solo las columnas que necesita y actualiza en lote por resultado.
    /// </summary>
    public static async Task<int> ClassifyPendingAsync(ApplicationDbContext db, ILogger log, int batchSize = 2000, CancellationToken ct = default)
    {
        var total = 0;
        while (!ct.IsCancellationRequested)
        {
            var batch = await db.Leads.AsNoTracking()
                .Where(l => (l.ArcaRulesVersion == null || l.ArcaRulesVersion != RulesVersion)
                            && (l.ArcaActivityRule == null
                                || (l.ArcaActivityRule != ManualRule && !l.ArcaActivityRule.StartsWith(CaptureRulePrefix))))
                .OrderBy(l => l.Id)
                .Select(l => new { l.Id, l.ProductKey, l.SearchCategory, l.Types, l.Name, l.SearchQuery })
                .Take(batchSize)
                .ToListAsync(ct);
            if (batch.Count == 0) break;

            var groups = batch
                .Select(l => (l.Id, R: Classify(new Input(l.ProductKey, l.SearchCategory, l.Types, l.Name, l.SearchQuery))))
                .GroupBy(x => x.R);

            foreach (var g in groups)
            {
                var ids = g.Select(x => x.Id).ToList();
                var code = g.Key.Code;
                var rule = g.Key.Rule;
                await db.Leads.Where(l => ids.Contains(l.Id))
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(l => l.ArcaActivityCode, code)
                        .SetProperty(l => l.ArcaActivityRule, rule)
                        .SetProperty(l => l.ArcaRulesVersion, RulesVersion), ct);
            }

            total += batch.Count;
            if (batch.Count < batchSize) break;
        }

        if (total > 0) log.LogInformation("ArcaClassifier: {Count} leads clasificados con reglas {Version}", total, RulesVersion);
        return total;
    }

    internal static string Normalize(string text)
    {
        var decomposed = text.ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length + 2);
        sb.Append(' ');
        var lastSpace = true;
        foreach (var ch in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark) continue;
            if (char.IsLetterOrDigit(ch)) { sb.Append(ch); lastSpace = false; }
            else if (!lastSpace) { sb.Append(' '); lastSpace = true; }
        }
        if (!lastSpace) sb.Append(' ');
        return sb.ToString();
    }

    private static bool Matches(string normalizedText, string keyword)
    {
        var exact = keyword.StartsWith('=');
        var kw = Normalize(exact ? keyword[1..] : keyword).Trim();
        if (kw.Length == 0) return false;
        return exact
            ? normalizedText.Contains($" {kw} ", StringComparison.Ordinal)
            : normalizedText.Contains($" {kw}", StringComparison.Ordinal);
    }

    private static string Trim(string s) => s.Length <= 128 ? s : s[..128];
}
