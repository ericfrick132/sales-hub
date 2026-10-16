using System.Globalization;
using System.Text;

namespace SalesHub.Infrastructure.Services;

/// <summary>
/// Provincias argentinas con el nombre canónico del IGN (el mismo del GeoJSON del mapa). Meta las
/// devuelve con tildes inconsistentes ("Tucuman", "Entre Rios", "Neuquén"): todo se compara por
/// <see cref="Key"/> (minúsculas, sin tildes ni signos).
/// </summary>
public static class ArRegions
{
    public const string Caba = "Ciudad Autónoma de Buenos Aires";

    public static readonly IReadOnlyList<string> All = new[]
    {
        Caba, "Buenos Aires", "Catamarca", "Chaco", "Chubut", "Córdoba", "Corrientes", "Entre Ríos",
        "Formosa", "Jujuy", "La Pampa", "La Rioja", "Mendoza", "Misiones", "Neuquén", "Río Negro",
        "Salta", "San Juan", "San Luis", "Santa Cruz", "Santa Fe", "Santiago del Estero",
        "Tierra del Fuego", "Tucumán",
    };

    private static readonly Dictionary<string, string> ByKey = BuildIndex();

    private static Dictionary<string, string> BuildIndex()
    {
        var d = All.ToDictionary(Key, x => x);
        void Alias(string alias, string canonical) => d[Key(alias)] = canonical;
        Alias("Capital Federal", Caba);
        Alias("CABA", Caba);
        Alias("Ciudad de Buenos Aires", Caba);
        Alias("Autonomous City of Buenos Aires", Caba);
        Alias("Buenos Aires Province", "Buenos Aires");
        Alias("Provincia de Buenos Aires", "Buenos Aires");
        Alias("Tierra del Fuego, Antártida e Islas del Atlántico Sur", "Tierra del Fuego");
        return d;
    }

    /// <summary>"Ciudad Autónoma de Buenos Aires" → "ciudad autonoma de buenos aires".</summary>
    public static string Key(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return string.Empty;
        var sb = new StringBuilder(s.Length);
        foreach (var c in s.Trim().ToLowerInvariant().Normalize(NormalizationForm.FormD))
        {
            var cat = CharUnicodeInfo.GetUnicodeCategory(c);
            if (cat == UnicodeCategory.NonSpacingMark) continue;
            sb.Append(char.IsLetterOrDigit(c) ? c : ' ');
        }
        return string.Join(' ', sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>Nombre canónico si es una provincia argentina; null si no.</summary>
    public static string? Canonical(string? region)
        => ByKey.TryGetValue(Key(region), out var c) ? c : null;
}

/// <summary>
/// Provincia de un celular argentino por su código de área (+54 9 &lt;área&gt; &lt;número&gt;), con
/// longest-prefix match sobre 4, 3 y 2 dígitos. Tabla armada a mano a partir del plan de
/// numeración de ENACOM (las ciudades cabecera de cada código): cubre las capitales y las
/// localidades principales, no es exhaustiva. Lo que no matchea queda sin provincia.
/// El 11 es AMBA (CABA + GBA, no se puede separar por el número): se cuenta en CABA y la UI lo aclara.
/// </summary>
public static class ArAreaCodes
{
    public const string Unknown = "Sin identificar";

    private static readonly Dictionary<string, string> Codes = Build();

    private static Dictionary<string, string> Build()
    {
        var d = new Dictionary<string, string>();
        void Add(string province, params string[] codes) { foreach (var c in codes) d[c] = province; }

        Add(ArRegions.Caba, "11");
        Add("Buenos Aires",
            "220", "221", "223", "230", "236", "237", "249", "291", "336", "348",
            "2202", "2221", "2223", "2224", "2225", "2226", "2227", "2229", "2241", "2242", "2243", "2244",
            "2245", "2246", "2252", "2254", "2255", "2257", "2261", "2262", "2264", "2265", "2266", "2267",
            "2268", "2271", "2272", "2273", "2274", "2281", "2283", "2284", "2285", "2286", "2291", "2292",
            "2296", "2297", "2314", "2316", "2317", "2320", "2323", "2324", "2325", "2326", "2337", "2342",
            "2343", "2344", "2345", "2346", "2352", "2353", "2354", "2355", "2356", "2357", "2358", "2392",
            "2393", "2394", "2395", "2396", "2473", "2474", "2475", "2477", "2478", "2921", "2922", "2923",
            "2924", "2925", "2926", "2927", "2928", "2929", "2932", "2933", "2935", "2936", "2982", "2983",
            "3329", "3388", "3407", "3487", "3489");
        Add("Catamarca", "383", "3832", "3835", "3837", "3838");
        Add("Chaco", "362", "364", "3725", "3731", "3732", "3734", "3735");
        Add("Chubut", "280", "297", "2903", "2945");
        Add("Córdoba", "351", "353", "358",
            "2336", "3385", "3387", "3463", "3467", "3468", "3472", "3521", "3522", "3524", "3525", "3532",
            "3533", "3537", "3541", "3542", "3543", "3544", "3546", "3547", "3548", "3549", "3562", "3563",
            "3564", "3571", "3572", "3573", "3574", "3575", "3576", "3582", "3583", "3584", "3585");
        Add("Corrientes", "379", "3756", "3772", "3773", "3774", "3775", "3777", "3781", "3782", "3786");
        Add("Entre Ríos", "343", "345", "3435", "3436", "3437", "3438", "3442", "3444", "3445", "3446", "3447",
            "3454", "3455", "3456", "3458");
        Add("Formosa", "370", "3711", "3715", "3716", "3718");
        Add("Jujuy", "388", "3885", "3886", "3887", "3888");
        Add("La Pampa", "2302", "2331", "2333", "2334", "2335", "2338", "2952", "2953", "2954");
        Add("La Rioja", "380", "3825", "3826", "3827");
        Add("Mendoza", "260", "261", "263", "2622", "2624", "2625", "2626");
        Add("Misiones", "376", "3741", "3743", "3751", "3754", "3755", "3757", "3758");
        Add("Neuquén", "299", "2942", "2948", "2972");
        Add("Río Negro", "294", "298", "2920", "2931", "2934", "2940", "2946");
        Add("Salta", "387", "3868", "3873", "3876", "3877", "3878");
        Add("San Juan", "264", "2646", "2647", "2648");
        Add("San Luis", "266", "2655", "2656", "2657", "2658");
        Add("Santa Cruz", "2902", "2962", "2963", "2966");
        Add("Santa Fe", "341", "342",
            "3382", "3400", "3401", "3402", "3404", "3405", "3406", "3408", "3409", "3460", "3462", "3464",
            "3465", "3466", "3469", "3471", "3476", "3482", "3483", "3491", "3492", "3493", "3496", "3497", "3498");
        Add("Santiago del Estero", "385", "3841", "3843", "3844", "3845", "3846", "3854", "3855", "3856", "3858");
        Add("Tierra del Fuego", "2901", "2964");
        Add("Tucumán", "381", "3862", "3863", "3865", "3867", "3869", "3891", "3892");
        return d;
    }

    /// <summary>
    /// (país ISO2, provincia) de un teléfono en cualquier formato ("+54 9 11 …", "5493515551234",
    /// "0351 15 …"). Fuera de Argentina devuelve el país por prefijo conocido y provincia null.
    /// </summary>
    public static (string? Country, string? Province) Resolve(string? phone)
    {
        if (string.IsNullOrWhiteSpace(phone)) return (null, null);
        var digits = new string(phone.Where(char.IsDigit).ToArray());
        if (digits.Length == 0) return (null, null);

        if (digits.StartsWith("54")) digits = digits[2..];
        else if (digits.StartsWith("598")) return ("UY", null);
        else if (digits.StartsWith("595")) return ("PY", null);
        else if (digits.StartsWith("56")) return ("CL", null);
        else if (digits.StartsWith("52")) return ("MX", null);
        else if (digits.StartsWith("34")) return ("ES", null);
        else if (digits.Length > 10) return (null, null);
        // Nacional: "9" de celular internacional y "0" de larga distancia.
        if (digits.StartsWith("9") && digits.Length >= 11) digits = digits[1..];
        if (digits.StartsWith("0")) digits = digits[1..];
        if (digits.Length < 8) return ("AR", null);

        foreach (var len in new[] { 4, 3, 2 })
            if (digits.Length > len && Codes.TryGetValue(digits[..len], out var p)) return ("AR", p);
        return ("AR", null);
    }
}
