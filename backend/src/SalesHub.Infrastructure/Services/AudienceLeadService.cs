using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SalesHub.Core.Abstractions;
using SalesHub.Core.Domain.Entities;
using SalesHub.Infrastructure.Persistence;

namespace SalesHub.Infrastructure.Services;

/// <summary>Fila de un listado de la competencia tal como la arma audience_export.py.</summary>
public record AudienceRow(string Dataset, string Competitor, string ProductKey, string? Segment, string ExternalId,
    string Name, string? Category, string? Address, string? City, string? Province, string? PostalCode,
    string? Country, double? Latitude, double? Longitude, string? Phone, string? PhoneType, bool WhatsappProbable,
    string? PhoneSource, string? Instagram, string? Website, string? Email, JsonElement? Raw);

/// <summary>Un paso de la búsqueda de un negocio, con su fecha.</summary>
/// <param name="Step">ig_profile, ig_search, web (extensión) · leads, google (servidor).</param>
/// <param name="Result">ok, no_phone, not_found, no_match, blocked, login, error, distinto…</param>
/// <param name="Detail">@handle, host de la web, fuente del número o mensaje de error.</param>
/// <summary>Resumen de un día (hora argentina) y app: negocios procesados, con número, follows y por qué no salió el resto.</summary>
public record FinderDayRow(string Day, string ProductKey, int Processed, int Found, int FoundInstagram, int FoundWeb,
    int FoundGoogle, int Follows, Dictionary<string, int> Reasons);

public record FinderBatchRow(Guid Id, int Number, string ProductKey, string? Dataset, string Status, string? Note,
    DateTimeOffset CreatedAt, DateTimeOffset? RetriedAt, DateTimeOffset? ClosedAt, int Size, int Pending, int Found,
    int FoundInstagram, int FoundWeb, int FoundGoogle, int Follows, DateTimeOffset? LastActivity, Dictionary<string, int> Reasons);

public record FinderStep(string Step, string Result, string? Detail, DateTimeOffset At);

/// <summary>Resultado de la extensión "Buscar WhatsApp" para un negocio.</summary>
/// <param name="Status">ok (encontró número) / google (no lo encontró en IG ni en la web) / error /
/// blocked o login (IG frenó a mitad: solo se guarda el historial, el negocio sigue pendiente).</param>
/// <param name="Source">instagram_link, instagram_bio, web_whatsapp, web_tel, web_texto.</param>
public record FinderResult(string Dataset, string ExternalId, string Status, string? Phone, string? Source,
    string? Instagram, string? Website, List<FinderStep>? Steps = null, DateTimeOffset? At = null);

public record AudienceSummaryRow(string ProductKey, string Competitor, string Dataset, string Segment, string? Country,
    int Total, int WithPhone, int Whatsapp, int FromCompetitor, int FromInstagram, int FromWeb, int FromLeads,
    int FromGoogle, int FinderDone, int FinderPending, int GooglePending);

public record AudienceRecentRow(string Name, string Dataset, string? City, string? Country, string? Phone,
    bool WhatsappProbable, string? PhoneSource, string? Instagram, DateTimeOffset UpdatedAt);

/// <summary>Un negocio de un batch: qué se buscó, los pasos que hizo la extensión y en qué terminó.</summary>
public record FinderBatchLeadRow(Guid Id, string Name, string Dataset, string? City, string? Instagram, string? Website,
    string? FinderStatus, string? Phone, string? PhoneSource, bool Followed, string? Reason, DateTimeOffset? CheckedAt,
    List<FinderStep> Steps);

/// <summary>Último paso por el que pasó un negocio (pantalla de Públicos, "Movimientos").</summary>
public record AudienceEventRow(string Name, string ProductKey, string Dataset, string? City, string? FinderStatus,
    string? Phone, string? PhoneSource, DateTimeOffset? CheckedAt, List<FinderStep> Steps);

/// <summary>
/// Tabla <c>audience_leads</c>: listados de la competencia para públicos de Meta (ver <see cref="AudienceLead"/>).
/// El WhatsApp que falta se busca en este orden: Instagram → web (extensión de Chrome, que manda acá
/// cada resultado) → leads de la base (gratis) → Google Places (pago, solo sobre lo que la extensión ya
/// descartó). Nada de esto crea leads ni encola mensajes.
/// </summary>
public class AudienceLeadService
{
    private readonly ApplicationDbContext _db;
    private readonly IGooglePlacesEnricher _places;
    private readonly IPhoneNormalizer _phone;
    private readonly ILogger<AudienceLeadService> _log;

    public AudienceLeadService(ApplicationDbContext db, IGooglePlacesEnricher places, IPhoneNormalizer phone,
        ILogger<AudienceLeadService> log)
    {
        _db = db; _places = places; _phone = phone; _log = log;
    }

    /// <summary>Clave fija para que la extensión mande resultados sin login: HMAC de la clave del JWT.</summary>
    public static string IngestKey(string jwtSigningKey) =>
        Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(jwtSigningKey), "audience-ingest"u8.ToArray()))[..32]
            .ToLowerInvariant();

    /// <summary>Upsert por (dataset, externalId). Un teléfono ya conseguido no se pisa con un null.</summary>
    public async Task<(int Inserted, int Updated)> ImportAsync(IReadOnlyList<AudienceRow> rows, CancellationToken ct = default)
    {
        int inserted = 0, updated = 0;
        foreach (var chunk in rows.Chunk(500))
        {
            var datasets = chunk.Select(r => r.Dataset).Distinct().ToList();
            var ids = chunk.Select(r => r.ExternalId).ToList();
            var existing = await _db.AudienceLeads
                .Where(a => datasets.Contains(a.Dataset) && ids.Contains(a.ExternalId))
                .ToDictionaryAsync(a => (a.Dataset, a.ExternalId), ct);
            foreach (var r in chunk)
            {
                if (!existing.TryGetValue((r.Dataset, r.ExternalId), out var a))
                {
                    a = new AudienceLead { Dataset = r.Dataset, ExternalId = r.ExternalId };
                    _db.AudienceLeads.Add(a);
                    existing[(r.Dataset, r.ExternalId)] = a;
                    inserted++;
                }
                else updated++;
                a.Competitor = r.Competitor;
                a.ProductKey = r.ProductKey;
                a.Segment = r.Segment ?? "usa";
                var name = Cut(r.Name, 256)!;
                // Con nombre nuevo (p. ej. el real de la ficha en vez del slug) la búsqueda en IG vale otro intento.
                if (a.Name != null && a.Name != name && a.Phone == null && a.FinderStatus == "google")
                {
                    a.FinderStatus = null;
                    a.FinderCheckedAt = null;
                }
                a.Name = name;
                a.Category = Cut(r.Category, 256);
                a.Address = Cut(r.Address, 512) ?? a.Address;
                a.City = Cut(r.City, 128) ?? a.City;
                a.Province = Cut(r.Province, 128) ?? a.Province;
                a.PostalCode = Cut(r.PostalCode, 16) ?? a.PostalCode;
                a.Country = r.Country ?? a.Country;
                a.Latitude = r.Latitude ?? a.Latitude;
                a.Longitude = r.Longitude ?? a.Longitude;
                if (!string.IsNullOrWhiteSpace(r.Phone))
                {
                    a.Phone = Cut(r.Phone, 32);
                    a.PhoneType = r.PhoneType;
                    a.WhatsappProbable = r.WhatsappProbable;
                    a.PhoneSource = Cut(r.PhoneSource ?? "competidor", 64);
                }
                a.Instagram = Cut(NormalizeInstagram(r.Instagram), 128)
                              ?? (a.Instagram != null && PlatformIg.IsMatch(a.Instagram) ? null : a.Instagram);
                a.Website = Cut(r.Website, 512) ?? a.Website;
                a.Email = Cut(r.Email, 256) ?? a.Email;
                a.RawJson = r.Raw?.GetRawText() ?? a.RawJson;
                a.UpdatedAt = DateTimeOffset.UtcNow;
            }
            await _db.SaveChangesAsync(ct);
            _db.ChangeTracker.Clear();
        }
        return (inserted, updated);
    }

    public record FinderItem(string ProductKey, string Dataset, string ExternalId, string Name, string? City, string? Country,
        string? Instagram, string? Website);

    /// <summary>
    /// Orden en que la extensión recorre los listados ("app:dataset"; el mismo dataset puede estar en dos
    /// apps, como AgendaPro). Eric 21/10: primero TurnosPro con AgendaPro.
    /// </summary>
    private static readonly string[] FinderPriority =
    {
        "turnospro:agendapro-ar", "turnospro:fresha-ar", "turnospro:booksy-ar", "turnospro:booksy-mx",
        "gymhero:crossfy-activos", "gymhero:crossfy-sin-clases", "gymhero:agendapro-ar", "gymhero:mindbody-mx-uy",
        "gymhero:totalpass-mx", "playcrew:atc-sin-uso", "playcrew:atc-online", "gymhero:wellhub-ar", "gymhero:wellhub-mx",
        "playcrew:playtomic-mx", "gymhero:fitpass-mx", "gymhero:apps-marca-blanca-ar-uy"
    };

    /// <summary>
    /// Próximos negocios para la extensión: sin pasar todavía por Instagram/web y sin teléfono, o con teléfono
    /// pero con Instagram (para sumar el número de su perfil como secundario, Eric 21/10). Primero los que no
    /// tienen teléfono; dentro de cada grupo, los que tienen Instagram. <paramref name="skip"/> = claves
    /// "dataset|id" que la extensión ya tiene en cola o procesó y todavía no llegaron acá.
    /// <paramref name="excludeProducts"/> = apps que ya llegaron a su tope del día en la extensión;
    /// <paramref name="onlyProducts"/> = la extensión trabaja solo para esas apps (un perfil de Chrome por app).
    /// </summary>
    public async Task<List<FinderItem>> FinderQueueAsync(int limit, IReadOnlyCollection<string> skip,
        IReadOnlyCollection<string>? excludeProducts = null, IReadOnlyCollection<string>? onlyProducts = null,
        CancellationToken ct = default)
    {
        // Solo negocios de batches abiertos (se arman a mano desde Públicos), del más viejo al más nuevo.
        var batches = (await _db.FinderBatches.Where(b => b.Status == "open").OrderBy(b => b.Number)
                .Select(b => new { b.Id, b.ProductKey }).ToListAsync(ct))
            .Where(b => (excludeProducts == null || !excludeProducts.Contains(b.ProductKey))
                        && (onlyProducts == null || onlyProducts.Count == 0 || onlyProducts.Contains(b.ProductKey)));
        var skipSet = skip.ToHashSet();
        var items = new List<FinderItem>();
        foreach (var batch in batches)
        {
            var rows = await _db.AudienceLeads
                .Where(a => a.FinderBatchId == batch.Id && a.FinderStatus == null && (a.Phone == null || a.Instagram != null))
                .OrderBy(a => a.Phone != null).ThenBy(a => a.Instagram == null).ThenBy(a => a.Name)
                .Take(limit + skipSet.Count)
                .Select(a => new FinderItem(a.ProductKey, a.Dataset, a.ExternalId, a.Name, a.City, a.Country, a.Instagram, a.Website))
                .ToListAsync(ct);
            items.AddRange(rows.Where(r => !skipSet.Contains($"{r.Dataset}|{r.ExternalId}")));
            if (items.Count >= limit) break;
        }
        return items.Take(limit).ToList();
    }

    /// <summary>
    /// Arma un batch con <paramref name="size"/> pendientes (sin buscar y sin batch; sin teléfono, o con teléfono
    /// e Instagram para el secundario) de la app, en el orden de prioridad de los listados (o solo de
    /// <paramref name="dataset"/>). Dentro de cada listado, primero los que no tienen teléfono.
    /// </summary>
    public const int MaxFinderAttempts = 3;

    public async Task<FinderBatchRow?> CreateBatchAsync(string productKey, string? dataset, int size, string? note,
        bool includeFailed = false, CancellationToken ct = default)
    {
        var pending = _db.AudienceLeads.Where(a => a.ProductKey == productKey && a.FinderStatus == null && a.FinderBatchId == null
            && (a.Phone == null || a.Instagram != null));
        if (!string.IsNullOrWhiteSpace(dataset)) pending = pending.Where(a => a.Dataset == dataset);
        var datasets = (await pending.Select(a => a.Dataset).Distinct().ToListAsync(ct))
            .OrderBy(d => Array.IndexOf(FinderPriority, $"{productKey}:{d}") is var i && i >= 0 ? i : 99).ThenBy(d => d);
        var ids = new List<Guid>();
        foreach (var d in datasets)
        {
            ids.AddRange(await pending.Where(a => a.Dataset == d)
                .OrderBy(a => a.Phone != null).ThenBy(a => a.Instagram == null).ThenBy(a => a.Name)
                .Take(size - ids.Count).Select(a => a.Id).ToListAsync(ct));
            if (ids.Count >= size) break;
        }
        // Los que no salieron en pasadas anteriores (sin número, "google" o error), hasta 2 reintentos: para volver a
        // correrlos después de cambiar algo en la extensión. No toca los que están en un batch abierto.
        var retried = new List<Guid>();
        if (includeFailed && ids.Count < size)
        {
            var openBatches = _db.FinderBatches.Where(b => b.Status == "open").Select(b => (Guid?)b.Id);
            var failed = _db.AudienceLeads.Where(a => a.ProductKey == productKey && a.Phone == null
                && (a.FinderStatus == "google" || a.FinderStatus == "error") && a.FinderAttempts < MaxFinderAttempts
                && !openBatches.Contains(a.FinderBatchId));
            if (!string.IsNullOrWhiteSpace(dataset)) failed = failed.Where(a => a.Dataset == dataset);
            retried = await failed.OrderBy(a => a.FinderAttempts).ThenBy(a => a.Instagram == null).ThenBy(a => a.Name)
                .Take(size - ids.Count).Select(a => a.Id).ToListAsync(ct);
            ids.AddRange(retried);
        }
        if (ids.Count == 0) return null;
        var batch = new FinderBatch
        {
            Id = Guid.NewGuid(),
            Number = (await _db.FinderBatches.MaxAsync(b => (int?)b.Number, ct) ?? 0) + 1,
            ProductKey = productKey,
            Dataset = string.IsNullOrWhiteSpace(dataset) ? null : dataset,
            Size = ids.Count,
            Note = Cut(note, 256),
            CreatedAt = DateTimeOffset.UtcNow,
        };
        _db.FinderBatches.Add(batch);
        await _db.SaveChangesAsync(ct);
        await _db.AudienceLeads.Where(a => ids.Contains(a.Id)).ExecuteUpdateAsync(u => u.SetProperty(a => a.FinderBatchId, batch.Id), ct);
        if (retried.Count > 0)
            await _db.AudienceLeads.Where(a => retried.Contains(a.Id)).ExecuteUpdateAsync(u => u
                .SetProperty(a => a.FinderStatus, (string?)null).SetProperty(a => a.FinderCheckedAt, (DateTimeOffset?)null), ct);
        return (await BatchesAsync(1, batch.Id, ct)).FirstOrDefault();
    }

    /// <summary>
    /// Devuelve a pendientes los negocios de la app que la extensión dio por "no lo encontró en IG" desde
    /// <paramref name="since"/> (p. ej. por un error de la extensión). No toca los que tienen número.
    /// </summary>
    public async Task<int> ResetNoMatchAsync(string productKey, DateTimeOffset since, CancellationToken ct = default)
    {
        var rows = await _db.AudienceLeads
            .Where(a => a.ProductKey == productKey && a.Phone == null && a.FinderStatus == "google" && a.FinderCheckedAt >= since)
            .ToListAsync(ct);
        var reset = rows.Where(a => ReadLog(a.FinderLog).Any(x => x.At >= since && x.Step == "ig_search" && x.Result == "no_match")).ToList();
        foreach (var a in reset)
        {
            a.FinderStatus = null;
            a.FinderCheckedAt = null;
        }
        await _db.SaveChangesAsync(ct);
        return reset.Count;
    }

    /// <summary>Cierra el batch: los que la extensión todavía no tocó vuelven a quedar libres.</summary>
    public async Task<bool> CloseBatchAsync(Guid id, CancellationToken ct = default)
    {
        var batch = await _db.FinderBatches.FirstOrDefaultAsync(b => b.Id == id, ct);
        if (batch is null) return false;
        batch.Status = "closed";
        batch.ClosedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);
        await _db.AudienceLeads.Where(a => a.FinderBatchId == id && a.FinderStatus == null)
            .ExecuteUpdateAsync(u => u.SetProperty(a => a.FinderBatchId, (Guid?)null), ct);
        return true;
    }

    /// <summary>Vuelve a poner en cola los del batch que terminaron sin número (p. ej. después de arreglar un error).</summary>
    public async Task<int> RetryBatchAsync(Guid id, CancellationToken ct = default)
    {
        var batch = await _db.FinderBatches.FirstOrDefaultAsync(b => b.Id == id, ct);
        if (batch is null) return 0;
        batch.Status = "open";
        batch.ClosedAt = null;
        batch.RetriedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);
        return await _db.AudienceLeads.Where(a => a.FinderBatchId == id && a.Phone == null && a.FinderStatus != null)
            .ExecuteUpdateAsync(u => u.SetProperty(a => a.FinderStatus, (string?)null).SetProperty(a => a.FinderCheckedAt, (DateTimeOffset?)null), ct);
    }

    /// <summary>Últimos batches con su avance: hechos, pendientes, con número (por origen), follows y motivos.</summary>
    public async Task<List<FinderBatchRow>> BatchesAsync(int limit, Guid? only = null, CancellationToken ct = default)
    {
        var q = _db.FinderBatches.AsQueryable();
        if (only is not null) q = q.Where(b => b.Id == only);
        var batches = await q.OrderByDescending(b => b.Number).Take(limit).ToListAsync(ct);
        var ids = batches.Select(b => (Guid?)b.Id).ToList();
        var leads = await _db.AudienceLeads.Where(a => ids.Contains(a.FinderBatchId))
            .Select(a => new { a.FinderBatchId, a.Phone, a.PhoneSource, a.SecondaryPhoneSource, a.FinderStatus, a.FinderLog })
            .ToListAsync(ct);
        return batches.Select(b =>
        {
            var from = b.RetriedAt ?? b.CreatedAt;
            var mine = leads.Where(l => l.FinderBatchId == b.Id).ToList();
            var reasons = new Dictionary<string, int>();
            int ig = 0, web = 0, google = 0, follows = 0;
            DateTimeOffset? last = null;
            foreach (var l in mine)
            {
                var steps = ReadLog(l.FinderLog).Where(x => x.At >= b.CreatedAt).ToList();
                follows += steps.Count(x => x.Step == "ig_follow" && x.Result == "followed");
                if (steps.Count > 0 && (last is null || steps[^1].At > last)) last = steps[^1].At;
                // Lo que trajo la búsqueda: el número principal, o el secundario si el principal ya venía del listado.
                var hadPhone = l.PhoneSource == "competidor" || l.PhoneSource?.StartsWith("cruce:") == true;
                var found = hadPhone ? l.SecondaryPhoneSource : l.Phone is not null ? l.PhoneSource ?? "" : null;
                if (found is not null)
                {
                    if (found.StartsWith("instagram")) ig++;
                    else if (found.StartsWith("web")) web++;
                    else google++;
                }
                else if (l.FinderStatus is not null)
                {
                    var r = Reason(steps.Where(x => x.At >= from).ToList());
                    reasons[r] = reasons.GetValueOrDefault(r) + 1;
                }
            }
            return new FinderBatchRow(b.Id, b.Number, b.ProductKey, b.Dataset, b.Status, b.Note, b.CreatedAt, b.RetriedAt, b.ClosedAt,
                mine.Count, mine.Count(l => l.FinderStatus is null), ig + web + google, ig, web, google, follows, last, reasons);
        }).ToList();
    }

    /// <summary>
    /// Lo que manda la extensión. Un número ya cargado (del competidor u otra fuente) no se pisa: si el de
    /// Instagram/web es otro, queda como secundario.
    /// </summary>
    public async Task<int> ApplyFinderResultsAsync(IReadOnlyList<FinderResult> results, CancellationToken ct = default)
    {
        var n = 0;
        foreach (var r in results)
        {
            var a = await _db.AudienceLeads.FirstOrDefaultAsync(x => x.Dataset == r.Dataset && x.ExternalId == r.ExternalId, ct);
            if (a is null) continue;
            AppendLog(a, r.Steps);
            n++;
            // Bloqueo, login o error que se reintenta: el negocio sigue en la cola; solo queda el historial.
            if (r.Status is "blocked" or "login" or "retry")
            {
                a.UpdatedAt = DateTimeOffset.UtcNow;
                continue;
            }
            a.FinderStatus = r.Status is "ok" or "google" or "error" ? r.Status : "error";
            a.FinderAttempts++;
            a.FinderCheckedAt = r.At ?? DateTimeOffset.UtcNow;
            a.Instagram ??= Cut(NormalizeInstagram(r.Instagram), 128);
            a.Website ??= Cut(r.Website, 512);
            if (r.Status == "ok" && !string.IsNullOrWhiteSpace(r.Phone))
            {
                var waLink = r.Source is "instagram_link" or "web_whatsapp" or "google_wa";
                if (a.Phone is null)
                    SetPhone(a, r.Phone, r.Source ?? "instagram_bio", waLink);
                else if (a.SecondaryPhone is null && NormalizePhone(a, r.Phone, waLink) is { } other
                         && !SameNumber(a.Phone, other))
                {
                    a.SecondaryPhone = Cut("+" + other, 32);
                    a.SecondaryPhoneSource = Cut(r.Source ?? "instagram_bio", 64);
                }
            }
            a.UpdatedAt = DateTimeOffset.UtcNow;
        }
        await _db.SaveChangesAsync(ct);
        return n;
    }

    /// <summary>
    /// Completa teléfonos de lo que la extensión ya descartó (FinderStatus = google): primero gratis contra
    /// la tabla leads (mismo Instagram, o mismo nombre a menos de 1 km) y después con Google Places
    /// (~USD 0,03 por búsqueda; cada fila se busca una sola vez). <paramref name="onlyFinderDone"/> = false
    /// salta el orden (para datasets que no van a pasar por la extensión).
    /// </summary>
    public async Task<(int Pending, int FromLeads, int FromGoogle, int Searched)> PlacesAsync(
        string? dataset, int maxGoogle, bool useGoogle = true, bool onlyFinderDone = true, CancellationToken ct = default, int? take = null)
    {
        var query = _db.AudienceLeads
            .Where(a => a.Phone == null && a.PlacesCheckedAt == null
                        && (dataset == null || a.Dataset == dataset)
                        && (!onlyFinderDone || a.FinderStatus == "google"))
            .OrderBy(a => a.Dataset).ThenBy(a => a.Name);
        // take: la corrida automática toma solo lo que entra en el tope del día (no carga todo a memoria).
        var pending = await (take is { } t ? query.Take(t) : query).ToListAsync(ct);
        int fromLeads = 0, fromGoogle = 0, searched = 0;

        foreach (var a in pending)
        {
            var match = await MatchLeadAsync(a, ct);
            if (match is not null)
            {
                SetPhone(a, match.Value.Phone, "lead", waLink: false);
                a.PlaceId ??= match.Value.PlaceId;
                AppendLog(a, new() { new("leads", "ok", "mismo negocio en la base de leads", DateTimeOffset.UtcNow) });
                fromLeads++;
            }
            else if (useGoogle && searched < maxGoogle)
            {
                searched++;
                var p = await _places.EnrichAsync(a.Name, a.Address, a.City, CountryName(a.Country), "es", ct);
                a.PlacesCheckedAt = DateTimeOffset.UtcNow;
                var (result, detail) =
                    p is null ? ("no_match", "Google no lo encontró")
                    : !IsSamePlace(a, p) ? ("distinto", $"otro lugar: {Cut(p.FormattedAddress, 120)}")
                    : string.IsNullOrWhiteSpace(p.Phone) ? ("no_phone", "en Maps sin teléfono")
                    : ("ok", "Google Maps");
                if (result == "ok")
                {
                    SetPhone(a, p!.Phone!, "google_places", waLink: false);
                    a.PlaceId = p.PlaceId;
                    a.Website ??= Cut(p.Website, 512);
                    a.Latitude ??= p.Latitude;
                    a.Longitude ??= p.Longitude;
                    fromGoogle++;
                }
                AppendLog(a, new() { new("google", result, detail, DateTimeOffset.UtcNow) });
            }
            else continue;
            a.UpdatedAt = DateTimeOffset.UtcNow;
            await _db.SaveChangesAsync(ct);
        }
        _log.LogInformation("audience-places {Dataset}: {Pending} pendientes, {Leads} desde leads, {Google} desde Google ({Searched} búsquedas)",
            dataset ?? "*", pending.Count, fromLeads, fromGoogle, searched);
        return (pending.Count, fromLeads, fromGoogle, searched);
    }

    public async Task<List<AudienceSummaryRow>> SummaryAsync(CancellationToken ct = default)
    {
        // Proyección anónima para que EF lo traduzca a un GROUP BY; el record y el orden se arman en memoria.
        var rows = await _db.AudienceLeads
            .GroupBy(a => new { a.ProductKey, a.Competitor, a.Dataset, a.Segment, a.Country })
            .Select(g => new
            {
                g.Key.ProductKey, g.Key.Competitor, g.Key.Dataset, g.Key.Segment, g.Key.Country,
                Total = g.Count(),
                WithPhone = g.Count(a => a.Phone != null),
                Whatsapp = g.Count(a => a.WhatsappProbable),
                FromCompetitor = g.Count(a => a.PhoneSource == "competidor" || (a.PhoneSource != null && a.PhoneSource.StartsWith("cruce:"))),
                FromInstagram = g.Count(a => a.PhoneSource != null && a.PhoneSource.StartsWith("instagram")),
                FromWeb = g.Count(a => a.PhoneSource != null && a.PhoneSource.StartsWith("web")),
                FromLeads = g.Count(a => a.PhoneSource == "lead"),
                FromGoogle = g.Count(a => a.PhoneSource == "google_places"),
                FinderDone = g.Count(a => a.FinderStatus != null),
                FinderPending = g.Count(a => a.Phone == null && a.FinderStatus == null),
                GooglePending = g.Count(a => a.Phone == null && a.FinderStatus == "google" && a.PlacesCheckedAt == null),
            })
            .ToListAsync(ct);
        return rows
            .Select(r => new AudienceSummaryRow(r.ProductKey, r.Competitor, r.Dataset, r.Segment, r.Country, r.Total,
                r.WithPhone, r.Whatsapp, r.FromCompetitor, r.FromInstagram, r.FromWeb, r.FromLeads, r.FromGoogle,
                r.FinderDone, r.FinderPending, r.GooglePending))
            .OrderBy(r => r.ProductKey).ThenBy(r => r.Dataset).ThenBy(r => r.Country)
            .ToList();
    }

    /// <summary>Últimos números conseguidos después de la carga (extensión, leads o Google).</summary>
    public Task<List<AudienceRecentRow>> RecentAsync(int limit, CancellationToken ct = default) =>
        _db.AudienceLeads
            .Where(a => a.Phone != null && a.PhoneSource != null && a.PhoneSource != "competidor" && !a.PhoneSource.StartsWith("cruce:"))
            .OrderByDescending(a => a.UpdatedAt)
            .Take(limit)
            .Select(a => new AudienceRecentRow(a.Name, a.Dataset, a.City, a.Country, a.Phone, a.WhatsappProbable,
                a.PhoneSource, a.Instagram, a.UpdatedAt))
            .ToListAsync(ct);

    /// <summary>Últimos negocios por los que pasó la búsqueda, con todos sus pasos.</summary>
    /// <summary>Negocios de un batch con sus pasos desde que se armó (incluye reintentos).</summary>
    public async Task<List<FinderBatchLeadRow>> BatchLeadsAsync(Guid batchId, CancellationToken ct = default)
    {
        var since = await _db.FinderBatches.Where(b => b.Id == batchId).Select(b => (DateTimeOffset?)b.CreatedAt).FirstOrDefaultAsync(ct);
        if (since is null) return new();
        var rows = await _db.AudienceLeads
            .Where(a => a.FinderBatchId == batchId)
            .Select(a => new { a.Id, a.Name, a.Dataset, a.City, a.Instagram, a.Website, a.FinderStatus, a.Phone, a.PhoneSource, a.FinderCheckedAt, a.FinderLog })
            .ToListAsync(ct);
        return rows.Select(r =>
            {
                var steps = ReadLog(r.FinderLog).Where(st => st.At >= since).OrderBy(st => st.At).ToList();
                var followed = steps.Any(x => x.Step == "ig_follow" && x.Result == "followed");
                var reason = r.Phone is null && steps.Count > 0 ? Reason(steps) : null;
                return new FinderBatchLeadRow(r.Id, r.Name, r.Dataset, r.City, r.Instagram, r.Website, r.FinderStatus,
                    r.Phone, r.PhoneSource, followed, reason, r.FinderCheckedAt, steps);
            })
            .OrderBy(r => r.Steps.Count == 0).ThenByDescending(r => r.CheckedAt).ThenBy(r => r.Name)
            .ToList();
    }

    public async Task<List<AudienceEventRow>> EventsAsync(int limit, CancellationToken ct = default)
    {
        var rows = await _db.AudienceLeads
            .Where(a => a.FinderLog != null)
            .OrderByDescending(a => a.UpdatedAt)
            .Take(limit)
            .Select(a => new { a.Name, a.ProductKey, a.Dataset, a.City, a.FinderStatus, a.Phone, a.PhoneSource, a.FinderCheckedAt, a.FinderLog })
            .ToListAsync(ct);
        return rows.Select(r => new AudienceEventRow(r.Name, r.ProductKey, r.Dataset, r.City, r.FinderStatus, r.Phone,
            r.PhoneSource, r.FinderCheckedAt, ReadLog(r.FinderLog))).ToList();
    }

    /// <summary>
    /// Por día y app: cada negocio cuenta una vez por día con lo que pasó ese día (pasos de la extensión
    /// y de Google). Si no salió número, el motivo es el más informativo de sus pasos.
    /// </summary>
    public async Task<List<FinderDayRow>> FinderDaysAsync(int days, CancellationToken ct = default)
    {
        var since = DateTimeOffset.UtcNow.AddDays(-days);
        var rows = await _db.AudienceLeads
            .Where(a => a.FinderLog != null && a.UpdatedAt >= since)
            .Select(a => new { a.ProductKey, a.FinderLog })
            .ToListAsync(ct);
        var art = TimeSpan.FromHours(-3);
        var attempts = rows.SelectMany(r => ReadLog(r.FinderLog)
                .Where(st => st.At >= since)
                .GroupBy(st => st.At.ToOffset(art).ToString("yyyy-MM-dd"))
                .Select(g => (Day: g.Key, r.ProductKey, Steps: g.ToList())));
        return attempts
            .GroupBy(a => (a.Day, a.ProductKey))
            .Select(g =>
            {
                int found = 0, ig = 0, web = 0, google = 0, follows = 0;
                var reasons = new Dictionary<string, int>();
                foreach (var (_, _, steps) in g)
                {
                    follows += steps.Count(x => x.Step == "ig_follow" && x.Result == "followed");
                    var ok = steps.FirstOrDefault(x => x.Result == "ok" && x.Step is "ig_profile" or "web" or "google" or "leads");
                    if (ok is not null)
                    {
                        found++;
                        if (ok.Step == "ig_profile") ig++; else if (ok.Step == "web") web++; else google++;
                        continue;
                    }
                    var reason = Reason(steps);
                    reasons[reason] = reasons.GetValueOrDefault(reason) + 1;
                }
                return new FinderDayRow(g.Key.Day, g.Key.ProductKey, g.Count(), found, ig, web, google, follows, reasons);
            })
            .OrderByDescending(r => r.Day).ThenBy(r => r.ProductKey)
            .ToList();
    }

    private static string Reason(List<FinderStep> steps)
    {
        bool Has(string step, string result) => steps.Any(x => x.Step == step && x.Result == result);
        if (steps.Any(x => x.Result is "blocked")) return "Instagram frenó";
        if (steps.Any(x => x.Result is "login")) return "Instagram pidió login";
        if (steps.Any(x => x.Step is "google" or "leads") && !steps.Any(x => x.Step.StartsWith("ig_") || x.Step == "web"))
            return Has("google", "distinto") ? "Google: otro negocio" : "Google: sin número";
        var profile = Has("ig_profile", "no_phone");
        if (profile && Has("web", "no_phone")) return "perfil y web sin número";
        if (profile) return "perfil sin número ni web";
        if (Has("ig_search", "no_match") || (Has("ig_profile", "not_found") && !profile)) return "no encontró el Instagram";
        if (Has("web", "no_phone")) return "web sin número";
        if (Has("google_search", "captcha")) return "Google pidió captcha";
        if (Has("google_search", "no_phone")) return "ni IG, ni web, ni Google";
        if (steps.Any(x => x.Result == "error")) return "error";
        return "otro";
    }

    private static readonly JsonSerializerOptions LogJson = new(JsonSerializerDefaults.Web);

    private static List<FinderStep> ReadLog(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new();
        try { return JsonSerializer.Deserialize<List<FinderStep>>(json, LogJson) ?? new(); }
        catch (JsonException) { return new(); }
    }

    /// <summary>Agrega pasos al historial del negocio (se guardan los últimos 60).</summary>
    private static void AppendLog(AudienceLead a, List<FinderStep>? steps)
    {
        if (steps is null || steps.Count == 0) return;
        var log = ReadLog(a.FinderLog);
        log.AddRange(steps.Select(s => s with { Detail = Cut(s.Detail, 200), Result = Cut(s.Result, 32)!, Step = Cut(s.Step, 32)! }));
        a.FinderLog = JsonSerializer.Serialize(log.TakeLast(60), LogJson);
    }

    /// <summary>
    /// Guarda el número en E.164. Un link de WhatsApp es WhatsApp seguro; en Argentina un número
    /// marcado como celular (con 15 o 9) también. El resto queda sin clasificar.
    /// </summary>
    /// <summary>Número en dígitos con código de país (sin "+"); null si es muy corto para ser un teléfono.</summary>
    private string? NormalizePhone(AudienceLead a, string raw, bool waLink)
    {
        var prefix = a.Country switch { "MX" => "52", "UY" => "598", "CL" => "56", "PE" => "51", _ => "54" };
        var digits = new string(raw.Where(char.IsDigit).ToArray());
        var normalized = (waLink && digits.Length >= 10 ? digits : _phone.NormalizeBusiness(raw, prefix) ?? digits).TrimStart('+');
        return normalized.Length < 8 ? null : normalized;
    }

    /// <summary>Mismo número aunque uno tenga el 9 de celular argentino o el 0/15: se comparan los últimos 8 dígitos.</summary>
    private static bool SameNumber(string a, string b)
    {
        var da = new string(a.Where(char.IsDigit).ToArray());
        var db = new string(b.Where(char.IsDigit).ToArray());
        return da.Length >= 8 && db.Length >= 8 && da[^8..] == db[^8..];
    }

    private void SetPhone(AudienceLead a, string raw, string source, bool waLink)
    {
        var prefix = a.Country switch { "MX" => "52", "UY" => "598", "CL" => "56", "PE" => "51", _ => "54" };
        var normalized = NormalizePhone(a, raw, waLink);
        if (normalized is null) return;
        var mobile = waLink || (prefix == "54" && normalized.StartsWith("549"));
        a.Phone = Cut("+" + normalized.TrimStart('+'), 32);
        a.PhoneType = mobile ? "celular" : "desconocido";
        a.WhatsappProbable = mobile;
        a.PhoneSource = Cut(source, 64);
    }

    private async Task<(string Phone, string? PlaceId)?> MatchLeadAsync(AudienceLead a, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(a.Instagram))
        {
            var byIg = await _db.Leads
                .Where(l => l.InstagramHandle != null && l.InstagramHandle.ToLower() == a.Instagram
                            && (l.WhatsappPhone != null || l.RawPhone != null))
                .Select(l => new { Phone = l.WhatsappPhone ?? l.RawPhone, l.PlaceId }).FirstOrDefaultAsync(ct);
            if (byIg is not null) return (byIg.Phone!, byIg.PlaceId);
        }
        if (a.Latitude is null || a.Longitude is null) return null;
        const double box = 0.01; // ~1 km
        var near = await _db.Leads
            .Where(l => (l.WhatsappPhone != null || l.RawPhone != null) && l.Latitude != null && l.Longitude != null
                        && l.Latitude > a.Latitude - box && l.Latitude < a.Latitude + box
                        && l.Longitude > a.Longitude - box && l.Longitude < a.Longitude + box)
            .Select(l => new { l.Name, Phone = l.WhatsappPhone ?? l.RawPhone, l.PlaceId }).ToListAsync(ct);
        var key = NormalizeName(a.Name);
        var hit = near.FirstOrDefault(l => key.Length >= 4 && NormalizeName(l.Name) == key);
        return hit is null ? null : (hit.Phone!, hit.PlaceId);
    }

    /// <summary>Google devuelve el primer candidato aunque sea otro negocio: se acepta solo si queda cerca o es de la misma ciudad.</summary>
    private static bool IsSamePlace(AudienceLead a, EnrichedPlace p)
    {
        if (a.Latitude is { } la && a.Longitude is { } lo && p.Latitude is { } pla && p.Longitude is { } plo)
            return Km(la, lo, pla, plo) < 1.5;
        var city = NormalizeName(a.City);
        return city.Length >= 4 && NormalizeName(p.FormattedAddress).Contains(city);
    }

    private static double Km(double a, double b, double c, double d)
    {
        double R(double x) => x * Math.PI / 180;
        var h = Math.Sin(R(c - a) / 2) * Math.Sin(R(c - a) / 2)
                + Math.Cos(R(a)) * Math.Cos(R(c)) * Math.Sin(R(d - b) / 2) * Math.Sin(R(d - b) / 2);
        return 12742 * Math.Asin(Math.Sqrt(h));
    }

    private static string? Cut(string? s, int max) => s is null ? null : s.Length <= max ? s : s[..max];

    private static string CountryName(string? iso) => iso switch
    {
        "MX" => "México", "UY" => "Uruguay", "CL" => "Chile", "PE" => "Perú", _ => "Argentina"
    };

    private static string? NormalizeInstagram(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var m = Regex.Match(s, @"instagram\.com/([A-Za-z0-9_.]+)");
        var h = (m.Success ? m.Groups[1].Value : s).Trim().TrimStart('@').Trim('.').ToLowerInvariant();
        return h.Length == 0 || h is "p" or "reel" || PlatformIg.IsMatch(h) ? null : h;
    }

    /// <summary>Cuentas de las plataformas (el pie de las fichas de AgendaPro trae @agendapro_software), no del negocio.</summary>
    private static readonly Regex PlatformIg =
        new(@"^(agendapro|fresha|booksy|playtomic|atcsports|crossfy|wellhub|gympass|totalpass|fitpass|mindbody)", RegexOptions.Compiled);

    private static readonly string[] Generic =
        { "crossfit", "cross", "box", "gym", "gimnasio", "training", "club", "fitness", "centro", "studio", "estudio", "the", "de", "el", "la" };

    private static string NormalizeName(string? s)
    {
        var d = (s ?? "").ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var plain = new string(d.Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark).ToArray());
        return string.Concat(Regex.Split(plain, "[^a-z0-9]+").Where(w => w.Length > 0 && !Generic.Contains(w)));
    }
}
