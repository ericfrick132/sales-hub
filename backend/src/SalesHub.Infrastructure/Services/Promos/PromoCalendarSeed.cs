using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SalesHub.Core.Domain.Entities.Promos;
using SalesHub.Infrastructure.Persistence;

namespace SalesHub.Infrastructure.Services.Promos;

/// <summary>
/// Calendario curado de promos (docs/promo-calendar/calendario-promos-2026-2027.json, embebido
/// como Resources/promo-calendar.json). Se carga solo si la tabla está vacía; después se cura
/// desde la UI. <see cref="ImportAsync"/> agrega/actualiza por slug a pedido.
/// </summary>
public static class PromoCalendarSeed
{
    public record Entry(string Date, string? EndDate, string Title, string Slug, string Description,
        List<string>? Apps, string? Confidence, bool? Enabled, string? Source, string? Note,
        string? ImageSourceUrl, string? ImageCredit, int? DiscountPercent, int? DurationMonths, string? CouponCode);

    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    public static List<Entry> Embedded()
    {
        using var s = typeof(PromoCalendarSeed).Assembly.GetManifestResourceStream("promo-calendar.json")
            ?? throw new InvalidOperationException("Falta el recurso promo-calendar.json");
        return JsonSerializer.Deserialize<List<Entry>>(s, Json) ?? new();
    }

    public static List<Entry> Parse(string json) => JsonSerializer.Deserialize<List<Entry>>(json, Json) ?? new();

    public static async Task<int> EnsureSeededAsync(ApplicationDbContext db, CancellationToken ct)
    {
        if (await db.PromoEvents.AnyAsync(ct)) return 0;
        return await ImportAsync(db, Embedded(), overwrite: false, ct);
    }

    /// <summary>Alta por slug; con <paramref name="overwrite"/> también pisa los existentes (menos la imagen ya bajada).</summary>
    public static async Task<int> ImportAsync(ApplicationDbContext db, IEnumerable<Entry> entries, bool overwrite, CancellationToken ct)
    {
        var existing = await db.PromoEvents.ToDictionaryAsync(e => e.Slug, ct);
        var n = 0;
        foreach (var x in entries)
        {
            if (string.IsNullOrWhiteSpace(x.Slug) || !DateOnly.TryParse(x.Date, out var date)) continue;
            var end = DateOnly.TryParse(x.EndDate, out var e2) && e2 >= date ? e2 : date;
            if (existing.TryGetValue(x.Slug, out var ev))
            {
                if (!overwrite) continue;
            }
            else
            {
                ev = new PromoEvent { Id = Guid.NewGuid(), Slug = x.Slug.Trim() };
                db.PromoEvents.Add(ev);
                existing[ev.Slug] = ev;
            }
            ev.Date = date;
            ev.EndDate = end;
            ev.Title = x.Title.Trim();
            ev.Description = x.Description.Trim();
            ev.Apps = x.Apps ?? new();
            ev.Confidence = string.IsNullOrWhiteSpace(x.Confidence) ? "confirmada" : x.Confidence;
            ev.Enabled = x.Enabled ?? true;
            ev.Source = x.Source;
            ev.Note = x.Note;
            if (ev.ImageSourceUrl != x.ImageSourceUrl) ev.ImageAssetId = null; // foto nueva → re-bajar
            ev.ImageSourceUrl = x.ImageSourceUrl;
            ev.ImageCredit = x.ImageCredit;
            ev.DiscountPercent = x.DiscountPercent;
            ev.DurationMonths = x.DurationMonths;
            ev.CouponCode = string.IsNullOrWhiteSpace(x.CouponCode) ? null : x.CouponCode.Trim().ToUpperInvariant();
            ev.UpdatedAt = DateTimeOffset.UtcNow;
            n++;
        }
        await db.SaveChangesAsync(ct);
        return n;
    }
}
