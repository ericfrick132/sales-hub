using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace SalesHub.Infrastructure.Services;

/// <summary>
/// Reglas del agendado de demo por WhatsApp (sin IA): qué 2 horarios ofrecer, cómo decirlos y cómo
/// entender cuál eligió el lead. Si la respuesta no es clara, no se adivina: pasa a la persona.
/// </summary>
public static class DemoScheduling
{
    private static readonly TimeZoneInfo Ar = LeadEntryService.ArTz;
    private static readonly string[] Days = { "domingo", "lunes", "martes", "miercoles", "jueves", "viernes", "sabado" };

    /// <summary>
    /// Elige 2 horarios: el primero libre con al menos 3 h de margen (preferentemente a la mañana,
    /// cuando más contestan) y otro en un día distinto, para que tenga opción real.
    /// </summary>
    public static List<DateTimeOffset> PickTwo(IReadOnlyList<DateTimeOffset> free, DateTimeOffset now)
    {
        var ok = free.Where(t => t >= now.AddHours(3)).OrderBy(t => t).ToList();
        if (ok.Count == 0) return new();
        static bool Morning(DateTimeOffset t) { var h = Local(t).Hour; return h >= 9 && h < 13; }
        var first = ok.FirstOrDefault(t => Morning(t) && Local(t).Date <= Local(ok[0]).Date.AddDays(1));
        if (first == default) first = ok[0];
        var second = ok.FirstOrDefault(t => Local(t).Date != Local(first).Date && Morning(t));
        if (second == default) second = ok.FirstOrDefault(t => Local(t).Date != Local(first).Date);
        if (second == default) second = ok.FirstOrDefault(t => t > first.AddHours(2));
        return second == default ? new() { first } : new() { first, second };
    }

    public static DateTimeOffset Local(DateTimeOffset t) => TimeZoneInfo.ConvertTime(t, Ar);

    /// <summary>"el jueves 2 a las 10:30" (sin tildes). "mañana"/"hoy" cuando corresponde.</summary>
    public static string Say(DateTimeOffset t, DateTimeOffset now)
    {
        var l = Local(t); var n = Local(now);
        var day = l.Date == n.Date ? "hoy" : l.Date == n.Date.AddDays(1) ? "mañana" : $"el {Days[(int)l.DayOfWeek]} {l.Day}";
        var hour = l.Minute == 0 ? $"{l.Hour}" : $"{l.Hour}:{l.Minute:00}";
        return $"{day} a las {hour}";
    }

    private static string Plain(string? s)
    {
        var t = (s ?? "").ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder();
        foreach (var c in t) if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark) sb.Append(c);
        return Regex.Replace(sb.ToString(), @"\s+", " ").Trim();
    }

    private static readonly Regex First = new(@"\b(1|uno|la primera|el primero|primero|primera|la 1|el 1|opcion 1)\b", RegexOptions.Compiled);
    private static readonly Regex Second = new(@"\b(2|dos|la segunda|el segundo|segundo|segunda|la 2|el 2|opcion 2|la otra|el otro)\b", RegexOptions.Compiled);
    private static readonly Regex Hour = new(@"\b(\d{1,2})(?:[:.](\d{2}))?\s*(?:hs|h|am|pm)?\b", RegexOptions.Compiled);
    private static readonly Regex CantOrOther = new(@"no (puedo|me queda|me sirve|llego)|otro (dia|horario)|mas tarde|mas temprano|no,|^no\b|\?", RegexOptions.Compiled);

    /// <summary>
    /// Índice del horario elegido (0 o 1), o null si no queda claro (entonces pasa a la persona).
    /// Entiende "la primera", "el jueves", "10:30", "a las 11", "mañana".
    /// </summary>
    public static int? ParseChoice(string? text, IReadOnlyList<DateTimeOffset> offered, DateTimeOffset now)
    {
        if (offered.Count == 0) return null;
        var t = Plain(text);
        if (t.Length == 0 || CantOrOther.IsMatch(t)) return null;

        var hits = new HashSet<int>();
        if (First.IsMatch(t) && !Hour.Matches(t).Any(m => int.Parse(m.Groups[1].Value) > 2)) hits.Add(0);
        if (Second.IsMatch(t) && offered.Count > 1 && !Hour.Matches(t).Any(m => int.Parse(m.Groups[1].Value) > 2)) hits.Add(1);

        for (var i = 0; i < offered.Count; i++)
        {
            var l = Local(offered[i]); var n = Local(now);
            var dayName = Days[(int)l.DayOfWeek];
            if (Regex.IsMatch(t, $@"\b{dayName}\b")) hits.Add(i);
            if (l.Date == n.Date.AddDays(1) && Regex.IsMatch(t, @"\bmanana\b")) hits.Add(i);
            if (l.Date == n.Date && Regex.IsMatch(t, @"\bhoy\b")) hits.Add(i);
            foreach (Match m in Hour.Matches(t))
            {
                var h = int.Parse(m.Groups[1].Value);
                var min = m.Groups[2].Success ? int.Parse(m.Groups[2].Value) : 0;
                if (h < 7 && h + 12 == l.Hour) h += 12; // "a las 4" = 16 h
                if (h == l.Hour && min == l.Minute) hits.Add(i);
            }
        }
        // "si"/"dale" sueltos: solo si se ofreció un único horario.
        if (hits.Count == 0 && offered.Count == 1 && Regex.IsMatch(t, @"^(si|dale|ok|perfecto|genial|joya|de una|va|bueno)\b")) hits.Add(0);
        return hits.Count == 1 ? hits.First() : null;
    }
}
