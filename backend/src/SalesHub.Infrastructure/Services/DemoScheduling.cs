using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace SalesHub.Infrastructure.Services;

/// <summary>
/// Reglas del agendado de demo por WhatsApp (sin IA, sobre Google Calendar): qué horarios ofrecer (hasta 2, hoy o mañana), cómo decirlos y cómo
/// entender cuál eligió el lead. Si la respuesta no es clara, no se adivina: pasa a la persona.
/// </summary>
public static class DemoScheduling
{
    private static readonly TimeZoneInfo Ar = LeadEntryService.ArTz;
    private static readonly string[] Days = { "domingo", "lunes", "martes", "miercoles", "jueves", "viernes", "sabado" };

    /// <summary>Ventana en la que se ofrecen demos: desde ahora hasta el fin de mañana (hora AR).</summary>
    public static (DateTimeOffset From, DateTimeOffset To) Window(DateTimeOffset now)
    {
        var dayAfter = Local(now).Date.AddDays(2);
        return (now, AtAr(dayAfter, 0));
    }

    private static DateTimeOffset AtAr(DateTime localDate, int minutesOfDay)
    {
        var local = DateTime.SpecifyKind(localDate.Date.AddMinutes(minutesOfDay), DateTimeKind.Unspecified);
        return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(local, Ar), TimeSpan.Zero);
    }

    /// <summary>
    /// Horarios posibles de hoy y mañana (días hábiles, franja y duración de la config) que no pisan
    /// ningún ocupado del calendario y arrancan con el margen mínimo desde ahora.
    /// </summary>
    public static List<DateTimeOffset> FreeSlots(IReadOnlyList<(DateTimeOffset Start, DateTimeOffset End)> busy,
        DateTimeOffset now, Core.Domain.Entities.GoogleCalendarSettings cfg)
    {
        var days = cfg.Days.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(d => int.TryParse(d, out var x) ? x : -1).ToHashSet();
        var len = Math.Max(cfg.SlotMinutes, 10);
        var earliest = now.AddMinutes(cfg.MinNoticeMinutes);
        var result = new List<DateTimeOffset>();
        var today = Local(now).Date;
        foreach (var day in new[] { today, today.AddDays(1) })
        {
            if (!days.Contains((int)day.DayOfWeek)) continue;
            for (var m = cfg.StartHour * 60; m + len <= cfg.EndHour * 60; m += len)
            {
                var start = AtAr(day, m);
                var end = start.AddMinutes(len);
                if (start < earliest) continue;
                if (busy.Any(b => b.Start < end && b.End > start)) continue;
                result.Add(start);
            }
        }
        return result;
    }

    /// <summary>
    /// Hasta 2 horarios, solo de hoy o mañana: el primero libre, y el otro mañana si el primero es hoy
    /// (así el que no puede hoy tiene opción); si no, más tarde el mismo día, separados para que sea
    /// una opción real (mañana / tarde).
    /// </summary>
    public static List<DateTimeOffset> PickTwo(IReadOnlyList<DateTimeOffset> free, DateTimeOffset now)
    {
        var ok = free.Where(t => t > now).OrderBy(t => t).ToList();
        if (ok.Count == 0) return new();
        var today = Local(now).Date;
        ok = ok.Where(t => Local(t).Date <= today.AddDays(1)).ToList();
        if (ok.Count == 0) return new();
        var first = ok[0];
        var firstDay = Local(first).Date;
        var second = firstDay == today ? ok.FirstOrDefault(t => Local(t).Date > today) : default;
        if (second == default) second = ok.FirstOrDefault(t => Local(t).Date == firstDay && t >= first.AddHours(3));
        if (second == default) second = ok.FirstOrDefault(t => t >= first.AddHours(1));
        if (second == default) second = ok.FirstOrDefault(t => t > first);
        return second == default ? new() { first } : new() { first, second };
    }

    /// <summary>Nombre comercial de la app para el título del evento.</summary>
    public static string AppName(string productKey) => productKey switch
    {
        "gymhero" => "GymHero",
        "turnospro" => "TurnosPro",
        "playcrew" => "PlayCrew",
        "construction" => "ArchiCloud",
        "bunker" => "Bunker",
        "unistock" => "DashFlow",
        _ => productKey,
    };

    public static DateTimeOffset Local(DateTimeOffset t) => TimeZoneInfo.ConvertTime(t, Ar);

    /// <summary>"el jueves 2 a las 10:30" (sin tildes). "mañana"/"hoy" cuando corresponde.</summary>
    public static string Say(DateTimeOffset t, DateTimeOffset now)
    {
        var l = Local(t); var n = Local(now);
        var day = l.Date == n.Date ? "hoy" : l.Date == n.Date.AddDays(1) ? "mañana" : $"el {Days[(int)l.DayOfWeek]} {l.Day}";
        return $"{day} a las {SayHour(t)}";
    }

    /// <summary>"10" o "10:30" (hora AR).</summary>
    public static string SayHour(DateTimeOffset t)
    {
        var l = Local(t);
        return l.Minute == 0 ? $"{l.Hour}" : $"{l.Hour}:{l.Minute:00}";
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
