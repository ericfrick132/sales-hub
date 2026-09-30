using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace SalesHub.Infrastructure.Services;

/// <summary>
/// Normalización del texto de un mensaje para el diccionario de tipos de mensaje. Es la misma
/// que docs/conversaciones/v2/norm.py (con la que se armaron y midieron los patrones): minúscula,
/// sin tildes ni emojis, letras repetidas colapsadas, números = N, mail = EMAIL, link = URL,
/// monto = PRECIO. Si cambia una, tiene que cambiar la otra.
/// </summary>
public static class IntentText
{
    private static readonly TimeSpan T = TimeSpan.FromMilliseconds(200);
    private static readonly Regex Emoji = new(@"[☀-➿️‍]|\uD83C[\uDC00-\uDFFF]|\uD83D[\uDC00-\uDFFF]|\uD83E[\uDC00-\uDFFF]", RegexOptions.Compiled, T);
    private static readonly Regex Url = new(@"https?://\S+", RegexOptions.Compiled, T);
    private static readonly Regex Email = new(@"\S+@\S+\.\S+", RegexOptions.Compiled, T);
    private static readonly Regex Money = new(@"\$\s?\d[\d.,]*", RegexOptions.Compiled, T);
    private static readonly Regex Digits = new(@"\d+", RegexOptions.Compiled, T);
    private static readonly Regex NonLetters = new(@"[^a-zñ\sA-Z]", RegexOptions.Compiled, T);
    private static readonly Regex Triple = new(@"(.)\1{2,}", RegexOptions.Compiled, T);
    private static readonly Regex DoubleEnd = new(@"([aeious])\1+\b", RegexOptions.Compiled, T);
    private static readonly Regex Spaces = new(@"\s+", RegexOptions.Compiled, T);

    public static string Normalize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        var t = Emoji.Replace(text, " ").ToLowerInvariant();
        var sb = new StringBuilder(t.Length);
        foreach (var c in t.Normalize(NormalizationForm.FormD))
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark) sb.Append(c);
        t = sb.ToString();
        t = Url.Replace(t, " URL ");
        t = Email.Replace(t, " EMAIL ");
        t = Money.Replace(t, " PRECIO ");
        t = Digits.Replace(t, " N ");
        t = NonLetters.Replace(t, " ");
        t = Triple.Replace(t, "$1");
        t = DoubleEnd.Replace(t, "$1");
        return Spaces.Replace(t, " ").Trim();
    }

    public static int WordCount(string normalized)
        => normalized.Length == 0 ? 0 : normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
}
