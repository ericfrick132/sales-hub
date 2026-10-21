using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SalesHub.Core.Domain.Entities;
using SalesHub.Core.Domain.Enums;
using SalesHub.Infrastructure.Instagram;
using SalesHub.Infrastructure.Persistence;

namespace SalesHub.Api.Controllers;

/// <summary>
/// DMs en frío por Instagram que manda el userscript desde el Chrome logueado con la cuenta de la
/// app. La extensión pide "el próximo" y reporta el resultado; acá se aplican el tope diario, la
/// franja horaria, la pausa al azar entre DMs y el freno por aviso de bloqueo de Instagram.
/// </summary>
[ApiController]
[Route("api/ig-dm")]
[Authorize]
public class IgDmController : ControllerBase
{
    private readonly ApplicationDbContext _db;
    private readonly InstagramMessagingOptions _ig;
    private readonly ILogger<IgDmController> _log;

    public IgDmController(ApplicationDbContext db, IOptions<InstagramMessagingOptions> ig, ILogger<IgDmController> log)
    {
        _db = db; _ig = ig.Value; _log = log;
    }

    private static readonly LeadStatus[] NotContacted = { LeadStatus.New, LeadStatus.Assigned, LeadStatus.Queued };

    private static TimeZoneInfo Ar()
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById("America/Argentina/Buenos_Aires"); }
        catch { return TimeZoneInfo.CreateCustomTimeZone("AR", TimeSpan.FromHours(-3), "AR", "AR"); }
    }

    private static string Norm(string? u) => (u ?? "").Trim().TrimStart('@').ToLowerInvariant();

    /// <summary>Cuenta del webhook oficial de la app (si tiene): de ahí sale el usuario por default y la línea del chat.</summary>
    private InstagramMessagingOptions.Account? OfficialAccount(string productKey) =>
        _ig.Accounts.FirstOrDefault(a => a.ProductKey == productKey);

    private string? AccountOf(IgDmSettings s) =>
        string.IsNullOrWhiteSpace(s.AccountUsername) ? OfficialAccount(s.ProductKey)?.Username : s.AccountUsername;

    /// <summary>Inicio del día AR de <paramref name="now"/>, en UTC.</summary>
    private static DateTimeOffset ArDayStart(DateTimeOffset now, TimeZoneInfo tz)
    {
        var local = TimeZoneInfo.ConvertTime(now, tz);
        return new DateTimeOffset(local.Date, local.Offset);
    }

    private static string Render(string template, Lead lead, Product? product) =>
        template.Replace("{negocio}", lead.Name).Replace("{app}", product?.DisplayName ?? lead.ProductKey).Trim();

    public record SettingsDto(
        string ProductKey, string DisplayName, bool Enabled, string? AccountUsername, string? DefaultAccount,
        int DailyCap, int MinGapMinutes, int MaxGapMinutes, int FromHour, int ToHour, string Template,
        DateTimeOffset? PausedUntil, string? PausedReason, int SentToday, int Pending, bool RepliesToConversations);

    [HttpGet("settings")]
    public async Task<ActionResult<List<SettingsDto>>> GetSettings(CancellationToken ct)
    {
        if (!CurrentUser.IsAdmin(User)) return Forbid();
        var tz = Ar();
        var dayStart = ArDayStart(DateTimeOffset.UtcNow, tz);
        var products = await _db.Products.AsNoTracking()
            .Where(p => p.Active && p.ProductKey != "").OrderBy(p => p.DisplayName).ToListAsync(ct);
        var all = await _db.IgDmSettings.AsNoTracking().ToDictionaryAsync(s => s.ProductKey, ct);
        var sent = await _db.Leads.AsNoTracking().Where(l => l.IgDmSentAt >= dayStart)
            .GroupBy(l => l.ProductKey).Select(g => new { g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.N, ct);
        var pending = await _db.Leads.AsNoTracking()
            .Where(l => l.InstagramHandle != null && l.InstagramHandle != "" && l.IgDmSentAt == null && l.IgDmError == null
                && l.InstagramScopedId == null && l.FirstReplyAt == null && NotContacted.Contains(l.Status))
            .GroupBy(l => l.ProductKey).Select(g => new { g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.N, ct);

        return products.Select(p =>
        {
            var s = all.GetValueOrDefault(p.ProductKey) ?? new IgDmSettings { ProductKey = p.ProductKey };
            var official = OfficialAccount(p.ProductKey);
            return new SettingsDto(p.ProductKey, p.DisplayName, s.Enabled, s.AccountUsername, official?.Username,
                s.DailyCap, s.MinGapMinutes, s.MaxGapMinutes, s.FromHour, s.ToHour, s.Template,
                s.PausedUntil > DateTimeOffset.UtcNow ? s.PausedUntil : null, s.PausedUntil > DateTimeOffset.UtcNow ? s.PausedReason : null,
                sent.GetValueOrDefault(p.ProductKey), pending.GetValueOrDefault(p.ProductKey), official is not null);
        }).ToList();
    }

    public record SaveSettingsRequest(
        bool Enabled, string? AccountUsername, int DailyCap, int MinGapMinutes, int MaxGapMinutes,
        int FromHour, int ToHour, string? Template, bool ClearPause = false);

    [HttpPut("settings/{productKey}")]
    public async Task<IActionResult> SaveSettings(string productKey, [FromBody] SaveSettingsRequest r, CancellationToken ct)
    {
        if (!CurrentUser.IsAdmin(User)) return Forbid();
        if (!await _db.Products.AnyAsync(p => p.ProductKey == productKey, ct)) return NotFound();
        if (r.DailyCap is < 0 or > 100) return BadRequest(new { error = "El tope diario va de 0 a 100" });
        if (r.MinGapMinutes < 1 || r.MaxGapMinutes < r.MinGapMinutes || r.MaxGapMinutes > 240)
            return BadRequest(new { error = "La pausa va de 1 a 240 minutos y la mínima no puede ser mayor que la máxima" });
        if (r.FromHour is < 0 or > 23 || r.ToHour is < 1 or > 24 || r.FromHour >= r.ToHour)
            return BadRequest(new { error = "Horario inválido: el 'desde' tiene que ser menor que el 'hasta'" });

        var s = await _db.IgDmSettings.FirstOrDefaultAsync(x => x.ProductKey == productKey, ct);
        if (s is null) { s = new IgDmSettings { ProductKey = productKey }; _db.IgDmSettings.Add(s); }
        s.AccountUsername = string.IsNullOrWhiteSpace(r.AccountUsername) ? null : Norm(r.AccountUsername);
        s.DailyCap = r.DailyCap;
        s.MinGapMinutes = r.MinGapMinutes;
        s.MaxGapMinutes = r.MaxGapMinutes;
        s.FromHour = r.FromHour;
        s.ToHour = r.ToHour;
        s.Template = (r.Template ?? "").Trim();
        if (r.Enabled && string.IsNullOrWhiteSpace(s.Template))
            return BadRequest(new { error = "Para prenderlo falta el texto del DM" });
        if (r.Enabled && string.IsNullOrWhiteSpace(AccountOf(s)))
            return BadRequest(new { error = "Para prenderlo falta la cuenta de Instagram que manda" });
        s.Enabled = r.Enabled;
        if (r.ClearPause) { s.PausedUntil = null; s.PausedReason = null; }
        s.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);
        return Ok();
    }

    /// <summary>Respuesta de "next": un DM para mandar, o cuántos segundos esperar y por qué.</summary>
    public record NextDm(Guid? LeadId, string? Handle, string? Text, string? ProductKey, int WaitSeconds, string? Reason);

    /// <summary>
    /// Próximo DM para la cuenta de IG logueada en la extensión. El lead queda reservado 15 min
    /// (si la extensión se cae, vuelve a la cola).
    /// </summary>
    [HttpGet("next")]
    public async Task<ActionResult<NextDm>> Next([FromQuery] string account, CancellationToken ct)
    {
        var acct = Norm(account);
        if (acct.Length == 0) return BadRequest(new { error = "Falta la cuenta" });
        var tz = Ar();
        var now = DateTimeOffset.UtcNow;
        var local = TimeZoneInfo.ConvertTime(now, tz);
        var dayStart = ArDayStart(now, tz);

        var settings = (await _db.IgDmSettings.Where(s => s.Enabled).ToListAsync(ct))
            .Where(s => Norm(AccountOf(s)) == acct).ToList();
        if (settings.Count == 0)
            return new NextDm(null, null, null, null, 600, $"@{acct} no tiene DMs prendidos en SalesHub");

        NextDm? best = null;
        foreach (var s in settings)
        {
            NextDm Wait(DateTimeOffset until, string why) =>
                new(null, null, null, s.ProductKey, (int)Math.Clamp((until - now).TotalSeconds, 30, 6 * 3600), why);
            DateTimeOffset AtHour(int dayOffset, int hour) =>
                new DateTimeOffset(local.Date.AddDays(dayOffset).AddHours(hour), local.Offset);

            NextDm candidate;
            if (s.PausedUntil > now)
                candidate = Wait(s.PausedUntil.Value, $"Pausado: {s.PausedReason}");
            else if (local.Hour < s.FromHour)
                candidate = Wait(AtHour(0, s.FromHour), $"Fuera de horario (manda de {s.FromHour} a {s.ToHour} h)");
            else if (local.Hour >= s.ToHour)
                candidate = Wait(AtHour(1, s.FromHour), $"Fuera de horario (manda de {s.FromHour} a {s.ToHour} h)");
            else if (await _db.Leads.CountAsync(l => l.ProductKey == s.ProductKey && l.IgDmSentAt >= dayStart, ct) >= s.DailyCap)
                candidate = Wait(AtHour(1, s.FromHour), $"Tope del día alcanzado ({s.DailyCap})");
            else if (s.NextAllowedAt > now)
                candidate = Wait(s.NextAllowedAt.Value, "Pausa entre DMs");
            else
            {
                var claimCutoff = now.AddMinutes(-15);
                var lead = await _db.Leads.Include(l => l.Product)
                    .Where(l => l.ProductKey == s.ProductKey && l.InstagramHandle != null && l.InstagramHandle != ""
                        && l.IgDmSentAt == null && l.IgDmError == null
                        && (l.IgDmClaimedAt == null || l.IgDmClaimedAt < claimCutoff)
                        && l.InstagramScopedId == null && l.FirstReplyAt == null && NotContacted.Contains(l.Status)
                        // Los de Competencia se llaman; nunca les sale un DM solo.
                        && l.Source != LeadSource.Competencia)
                    .OrderByDescending(l => l.Score).ThenByDescending(l => l.CreatedAt)
                    .FirstOrDefaultAsync(ct);
                if (lead is null)
                    candidate = Wait(now.AddMinutes(30), "No hay leads con Instagram sin contactar");
                else
                {
                    lead.IgDmClaimedAt = now;
                    await _db.SaveChangesAsync(ct);
                    return new NextDm(lead.Id, Norm(lead.InstagramHandle), Render(s.Template, lead, lead.Product), s.ProductKey, 0, null);
                }
            }
            if (best is null || candidate.WaitSeconds < best.WaitSeconds) best = candidate;
        }
        return best!;
    }

    public record ResultRequest(bool Ok, string? Text, string? Error, bool Blocked = false);

    [HttpPost("{leadId:guid}/result")]
    public async Task<IActionResult> Result(Guid leadId, [FromBody] ResultRequest r, CancellationToken ct)
    {
        var lead = await _db.Leads.FirstOrDefaultAsync(l => l.Id == leadId, ct);
        if (lead is null) return NotFound();
        var s = await _db.IgDmSettings.FirstOrDefaultAsync(x => x.ProductKey == lead.ProductKey, ct);
        var now = DateTimeOffset.UtcNow;
        lead.IgDmClaimedAt = null;
        lead.UpdatedAt = now;

        if (r.Ok)
        {
            lead.IgDmSentAt = now;
            var text = (r.Text ?? "").Trim();
            _db.LeadNotes.Add(new LeadNote
            {
                Id = Guid.NewGuid(), LeadId = lead.Id, Kind = LeadNoteKind.System, CreatedAt = now,
                Text = $"DM en frío por Instagram a @{Norm(lead.InstagramHandle)}" + (text.Length > 0 ? $": {text}" : ""),
            });
            // Si la cuenta tiene webhook oficial, el DM queda en el chat: cuando conteste, la
            // respuesta cae en el mismo hilo (match por @usuario en ConversationService).
            var official = OfficialAccount(lead.ProductKey);
            if (official is not null && text.Length > 0)
                _db.ConversationMessages.Add(new ConversationMessage
                {
                    Id = Guid.NewGuid(), LeadId = lead.Id, SellerId = lead.SellerId,
                    Direction = MessageDirection.Outbound, Status = MessageDeliveryStatus.Sent,
                    Text = text, EvolutionInstance = InstagramMessengerClient.LineName(official.IgUserId),
                    Timestamp = now, IsRead = true,
                });
            if (s is not null)
            {
                var gap = Random.Shared.Next(s.MinGapMinutes * 60, s.MaxGapMinutes * 60 + 1);
                s.NextAllowedAt = now.AddSeconds(gap);
            }
        }
        else if (r.Blocked)
        {
            // Aviso de bloqueo de Instagram: se frena la app hasta mañana a la hora de arranque.
            // El lead vuelve a la cola (no fue culpa de él).
            if (s is not null)
            {
                var tz = Ar();
                var local = TimeZoneInfo.ConvertTime(now, tz);
                s.PausedUntil = new DateTimeOffset(local.Date.AddDays(1).AddHours(s.FromHour), local.Offset);
                s.PausedReason = Trunc(r.Error, 300) ?? "Instagram mostró un aviso de bloqueo";
            }
            _log.LogWarning("DMs de IG frenados para {Product}: {Reason}", lead.ProductKey, r.Error);
        }
        else
        {
            lead.IgDmError = Trunc(r.Error, 300) ?? "error";
            if (s is not null) s.NextAllowedAt = now.AddMinutes(1);
        }
        await _db.SaveChangesAsync(ct);
        return Ok();
    }

    private static string? Trunc(string? s, int max) =>
        string.IsNullOrWhiteSpace(s) ? null : s.Length <= max ? s : s[..max];
}
