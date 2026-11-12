using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using SalesHub.Core.Domain.Entities;
using SalesHub.Core.Domain.Enums;

namespace SalesHub.Infrastructure.Instagram;

/// <summary>
/// Reglas del plan de DMs por Instagram que comparten los dos caminos de envío (la extensión de
/// Chrome y el celular): el día de Argentina, el tope de hoy según la rampa semanal, la cola de
/// leads en frío, las versiones del texto y qué pasa cuando Instagram muestra un aviso.
/// Son funciones puras: quién las llama decide cuándo guardar.
/// </summary>
public static partial class IgOutreachPlan
{
    public static readonly LeadStatus[] NotContacted = { LeadStatus.New, LeadStatus.Assigned, LeadStatus.Queued };

    /// <summary>Después de un aviso de Instagram: una semana a mitad de tope.</summary>
    public const int ThrottleDays = 7;
    public const int MaxRampWeeks = 12;

    // ── Día de Argentina ───────────────────────────────────────────────────────────────────

    public static TimeZoneInfo Ar()
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById("America/Argentina/Buenos_Aires"); }
        catch { return TimeZoneInfo.CreateCustomTimeZone("AR", TimeSpan.FromHours(-3), "AR", "AR"); }
    }

    public static DateTimeOffset ArLocal(DateTimeOffset now) => TimeZoneInfo.ConvertTime(now, Ar());

    /// <summary>Inicio del día de Argentina de <paramref name="now"/> (para contar "hoy").</summary>
    public static DateTimeOffset ArDayStart(DateTimeOffset now)
    {
        var local = ArLocal(now);
        return new DateTimeOffset(local.Date, local.Offset);
    }

    public static DateOnly ArToday(DateTimeOffset now) => DateOnly.FromDateTime(ArLocal(now).Date);

    /// <summary>Mañana (Argentina) a la hora de arranque de la app.</summary>
    public static DateTimeOffset TomorrowAt(DateTimeOffset now, int hour)
    {
        var local = ArLocal(now);
        return new DateTimeOffset(local.Date.AddDays(1).AddHours(hour), local.Offset);
    }

    // ── Usuarios ───────────────────────────────────────────────────────────────────────────

    public static string Norm(string? u) => (u ?? "").Trim().TrimStart('@').ToLowerInvariant();

    [GeneratedRegex("^[a-z0-9._]{1,30}$")]
    private static partial Regex HandleRx();

    /// <summary>Usuario de Instagram bien formado. Va dentro de un link que abre el celular: nada raro pasa de acá.</summary>
    public static bool IsValidHandle(string? normalized) => normalized is not null && HandleRx().IsMatch(normalized);

    /// <summary>Lista de usuarios escrita a mano (comas, espacios o líneas), normalizada.</summary>
    public static HashSet<string> ParseHandles(string? raw) =>
        (raw ?? "").Split(new[] { ',', ';', ' ', '\n', '\r', '\t' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(Norm).Where(h => h.Length > 0).ToHashSet();

    // ── Rampa ──────────────────────────────────────────────────────────────────────────────

    /// <summary>"10, 20,30" → [10,20,30]. Vacío es válido (sin rampa).</summary>
    public static bool TryParseRamp(string? raw, int maxValue, out int[] values, out string? error)
    {
        values = Array.Empty<int>();
        error = null;
        if (string.IsNullOrWhiteSpace(raw)) return true;
        var parts = raw.Split(new[] { ',', ';', ' ', '·', '/', '-' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length > MaxRampWeeks) { error = $"La rampa admite hasta {MaxRampWeeks} semanas"; return false; }
        var list = new List<int>();
        foreach (var p in parts)
        {
            if (!int.TryParse(p, NumberStyles.None, CultureInfo.InvariantCulture, out var n) || n > maxValue)
            {
                error = $"Cada semana de la rampa va de 0 a {maxValue} (\"{p}\" no sirve)";
                return false;
            }
            list.Add(n);
        }
        values = list.ToArray();
        return true;
    }

    /// <summary>Rampa ya validada al guardar: lo que no se entiende se ignora.</summary>
    public static int[] ParseRamp(string? raw) => TryParseRamp(raw, int.MaxValue, out var v, out _) ? v : Array.Empty<int>();

    public static string? FormatRamp(int[] values) => values.Length == 0 ? null : string.Join(",", values);

    /// <summary>Semana de la rampa en la que cae hoy (1 = la primera).</summary>
    public static int WeekOf(IgDmSettings s, DateTimeOffset now) =>
        s.RampStartedOn is null ? 1 : Math.Max(1, (ArToday(now).DayNumber - s.RampStartedOn.Value.DayNumber) / 7 + 1);

    private static int FromRamp(int[] ramp, int week) => ramp[Math.Min(ramp.Length, week) - 1];

    public static bool IsThrottled(IgDmSettings s, DateTimeOffset now) => s.ThrottledUntil > now;

    private static int Throttle(IgDmSettings s, DateTimeOffset now, int cap) => IsThrottled(s, now) ? (cap + 1) / 2 : cap;

    /// <summary>DMs en frío que pueden salir hoy: la rampa (o el tope fijo), a la mitad si viene de un aviso.</summary>
    public static int ColdCapToday(IgDmSettings s, DateTimeOffset now)
    {
        var ramp = ParseRamp(s.ColdRamp);
        return Throttle(s, now, ramp.Length == 0 ? s.DailyCap : FromRamp(ramp, WeekOf(s, now)));
    }

    /// <summary>DMs a seguidores que pueden salir hoy. Solo existe con celular y con la opción prendida.</summary>
    public static int FollowerCapToday(IgDmSettings s, DateTimeOffset now)
    {
        if (!s.FollowersEnabled || s.Sender != IgDmSettings.SenderDevice) return 0;
        var ramp = ParseRamp(s.FollowerRamp);
        return ramp.Length == 0 ? 0 : Throttle(s, now, FromRamp(ramp, WeekOf(s, now)));
    }

    /// <summary>Pausa al azar hasta el próximo DM.</summary>
    public static int NextGapSeconds(IgDmSettings s) =>
        Random.Shared.Next(s.MinGapMinutes * 60, Math.Max(s.MinGapMinutes, s.MaxGapMinutes) * 60 + 1);

    /// <summary>
    /// Instagram mostró un aviso de bloqueo: la app se frena hasta mañana a la hora de arranque y
    /// vuelve una semana a mitad de tope.
    /// </summary>
    public static void ApplyBlock(IgDmSettings s, DateTimeOffset now, string? reason)
    {
        s.PausedUntil = TomorrowAt(now, s.FromHour);
        s.PausedReason = string.IsNullOrWhiteSpace(reason) ? "Instagram mostró un aviso de bloqueo" : reason;
        s.ThrottledUntil = now.AddDays(ThrottleDays);
    }

    // ── Cola en frío ───────────────────────────────────────────────────────────────────────

    /// <summary>Leads a los que les puede salir el DM en frío: con @usuario, sin contactar y sin charla.</summary>
    public static IQueryable<Lead> ColdQueue(IQueryable<Lead> leads) =>
        leads.Where(l => l.InstagramHandle != null && l.InstagramHandle != ""
            && l.IgDmSentAt == null && l.IgDmError == null
            && l.InstagramScopedId == null && l.FirstReplyAt == null && NotContacted.Contains(l.Status)
            // Los de Competencia se llaman; nunca les sale un DM solo.
            && l.Source != LeadSource.Competencia && l.Source != LeadSource.InstagramFollower);

    public static IQueryable<Lead> ColdQueue(IQueryable<Lead> leads, string productKey) =>
        ColdQueue(leads).Where(l => l.ProductKey == productKey);

    // ── Textos ─────────────────────────────────────────────────────────────────────────────

    [GeneratedRegex(@"^\s*-{3,}\s*$", RegexOptions.Multiline)]
    private static partial Regex VariantSeparatorRx();

    /// <summary>Versiones del texto: bloques separados por una línea con "---".</summary>
    public static List<string> Variants(string? template) =>
        VariantSeparatorRx().Split(template ?? "").Select(v => v.Trim()).Where(v => v.Length > 0).ToList();

    /// <summary>Una versión al azar (mandar siempre el mismo texto es lo primero que mira Instagram).</summary>
    public static string PickVariant(string? template)
    {
        var all = Variants(template);
        return all.Count == 0 ? "" : all[Random.Shared.Next(all.Count)];
    }

    public static string RenderCold(string variant, Lead lead, Product? product) =>
        variant.Replace("{negocio}", lead.Name).Replace("{app}", product?.DisplayName ?? lead.ProductKey).Trim();

    public static string RenderFollower(string variant, string appName) =>
        variant.Replace("{app}", appName).Replace("{negocio}", "").Trim();

    [GeneratedRegex(@"\s+")]
    private static partial Regex SpacesRx();

    /// <summary>
    /// Deja el texto como lo puede tipear el celular: el teclado por adb solo escribe ASCII, así
    /// que se sacan tildes, ¿ ¡ y emojis, y los saltos de línea pasan a espacios. Lo que devuelve
    /// es exactamente lo que queda escrito en el chat.
    /// </summary>
    public static string FoldForDevice(string? text)
    {
        var src = (text ?? "")
            .Replace('“', '"').Replace('”', '"').Replace('‘', '\'').Replace('’', '\'')
            .Replace("—", "-").Replace("–", "-").Replace("…", "...");
        var sb = new StringBuilder(src.Length);
        foreach (var c in src.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;
            if (c is '\n' or '\r' or '\t') { sb.Append(' '); continue; }
            if (c is >= ' ' and <= '~') sb.Append(c);
        }
        return SpacesRx().Replace(sb.ToString(), " ").Trim();
    }
}
