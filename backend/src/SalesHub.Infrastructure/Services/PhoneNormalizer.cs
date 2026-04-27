using System.Text.RegularExpressions;
using SalesHub.Core.Abstractions;

namespace SalesHub.Infrastructure.Services;

public class PhoneNormalizer : IPhoneNormalizer
{
    private static readonly Regex NonDigit = new(@"\D", RegexOptions.Compiled);

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
}
