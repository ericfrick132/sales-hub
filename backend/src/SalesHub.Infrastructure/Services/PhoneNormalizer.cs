using System.Text.RegularExpressions;
using SalesHub.Core.Abstractions;

namespace SalesHub.Infrastructure.Services;

public class PhoneNormalizer : IPhoneNormalizer
{
    private static readonly Regex NonDigit = new(@"\D", RegexOptions.Compiled);
    // 549 + área (arranca con 1, 2 o 3) + resto: 13 dígitos totales. Cubre 11 (CABA/GBA),
    // 2xx/2xxx y 3xx/3xxx del interior.
    private static readonly Regex ArCanonicalRx = new(@"^549[123]\d{9}$", RegexOptions.Compiled);

    public string? Normalize(string? rawPhone, string countryPrefix)
    {
        if (string.IsNullOrWhiteSpace(rawPhone)) return null;
        var digits = NonDigit.Replace(rawPhone, "").TrimStart('0');
        if (digits.Length < 6) return null;

        if (digits.StartsWith(countryPrefix)) return digits;

        // Argentina-specific: mobile numbers without 9
        if (countryPrefix == "54")
        {
            if (digits.Length == 10) return "54" + digits;
            if (digits.Length == 11 && digits.StartsWith("9")) return "54" + digits;
            return "54" + digits;
        }

        if (countryPrefix == "52" && digits.Length == 10) return "52" + digits;

        return countryPrefix + digits;
    }

    public bool IsCanonicalAr(string? phone) =>
        !string.IsNullOrEmpty(phone) && ArCanonicalRx.IsMatch(phone);

    /// <summary>Deja el número como "base" nacional (sin 54/549/0/9 iniciales, sin el 15).</summary>
    private static string ToArBase(string raw)
    {
        var d = NonDigit.Replace(raw, "");
        d = d.TrimStart('0');                              // 00 internacional / 0 de área
        while (d.StartsWith("5454")) d = d[2..];           // país duplicado ("54 54 9 ...")
        if (d.StartsWith("549")) d = d[3..];
        else if (d.StartsWith("54")) d = d[2..];
        if (d.Length == 11 && d[0] == '9') d = d[1..];     // 9 repetido ("549 9 11 ...")
        if (d.Length == 11 && d[0] == '0') d = d[1..];     // 0 de área adentro
        // "15" después del área (0341 15-5551234 → 341 555 1234): probar largos de área 2..4
        if (d.Length == 12)
        {
            for (var areaLen = 2; areaLen <= 4; areaLen++)
            {
                if (d.Substring(areaLen, 2) == "15")
                {
                    var cand = d[..areaLen] + d[(areaLen + 2)..];
                    if (cand.Length == 10) { d = cand; break; }
                }
            }
        }
        return d;
    }

    public string? NormalizeArStrict(string? rawPhone)
    {
        if (string.IsNullOrWhiteSpace(rawPhone)) return null;
        var b = ToArBase(rawPhone);
        var full = "549" + b;
        return b.Length == 10 && ArCanonicalRx.IsMatch(full) ? full : null;
    }

    public IReadOnlyList<string> ArRepairCandidates(string? rawPhone)
    {
        var result = new List<string>();
        void Add(string basePart)
        {
            var full = "549" + basePart;
            if (ArCanonicalRx.IsMatch(full) && !result.Contains(full)) result.Add(full);
        }

        if (string.IsNullOrWhiteSpace(rawPhone)) return result;
        var strict = NormalizeArStrict(rawPhone);
        if (strict is not null) result.Add(strict);

        var b = ToArBase(rawPhone);
        // Un dígito DE MÁS (typo duplicado): probar sacando de a uno. 11 candidatos máx,
        // el CheckNumbers de Evolution decide cuál existe de verdad.
        if (b.Length == 11)
            for (var i = 0; i < b.Length; i++) Add(b.Remove(i, 1));
        // Doce dígitos sin "15" detectable: probar sacando pares obvios (área duplicada).
        if (b.Length == 12)
        {
            for (var areaLen = 2; areaLen <= 4; areaLen++)
                if (b.Length - areaLen >= 10 && b[..areaLen] == b.Substring(areaLen, areaLen))
                    Add(b[areaLen..]);
        }
        // Ocho dígitos pelados: probable CABA sin área.
        if (b.Length == 8) Add("11" + b);
        // País metido dos veces ("54 9 54 3730 4321" → base "5437304321"): reintentar
        // tratando la base como raw. Recursión acotada (la base se achica siempre).
        if (b.Length >= 10 && b.StartsWith("54"))
            foreach (var c in ArRepairCandidates(b))
                if (!result.Contains(c)) result.Add(c);
        return result.Count > 12 ? result.Take(12).ToList() : result;
    }
}
