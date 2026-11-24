using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SalesHub.Core.Abstractions;
using SalesHub.Core.Domain.Entities;
using SalesHub.Core.Domain.Enums;
using SalesHub.Infrastructure.Apify;
using SalesHub.Infrastructure.Persistence;

namespace SalesHub.Infrastructure.Services.Outbound;

/// <summary>
/// Arma la lista para el outbound: busca negocios en Google Places por rubro × zona, los da de alta
/// reservados para el outbound (etiqueta <see cref="OutboundService.ReservedTag"/>: el reparto automático
/// no los toca y no les sale cadencia), y los va nutriendo y puntuando.
/// </summary>
public class ProspectService
{
    private readonly ApplicationDbContext _db;
    private readonly GooglePlacesSource _places;
    private readonly ProspectEnricher _enricher;
    private readonly IPhoneNormalizer _phone;
    private readonly ILogger<ProspectService> _log;

    public ProspectService(ApplicationDbContext db, GooglePlacesSource places, ProspectEnricher enricher,
        IPhoneNormalizer phone, ILogger<ProspectService> log)
    {
        _db = db; _places = places; _enricher = enricher; _phone = phone; _log = log;
    }

    public sealed record SearchResult(int Queries, int Found, int Created, int Duplicates, int Discarded);

    /// <summary>
    /// Una búsqueda de Places por cada término del rubro en la zona (máx <paramref name="perQuery"/>
    /// resultados cada una, cada resultado es una llamada de detalle). Los nuevos entran con puntaje
    /// provisorio; la web se lee después en el worker.
    /// </summary>
    public async Task<SearchResult> SearchAsync(string productKey, string rubroKey, string zoneKey, int perQuery, CancellationToken ct)
    {
        var product = await _db.Products.FirstOrDefaultAsync(p => p.ProductKey == productKey, ct)
            ?? throw new ArgumentException("Esa app no existe.");
        var rubro = ProspectScorer.RubroByKey(rubroKey) ?? throw new ArgumentException("Rubro desconocido.");
        if (rubro.ProductKey != product.ProductKey) throw new ArgumentException($"El rubro {rubro.Label} no es de {product.DisplayName}.");
        var zone = ProspectScorer.ZoneByKey(zoneKey) ?? throw new ArgumentException("Zona desconocida.");
        perQuery = Math.Clamp(perQuery, 1, 20);

        var batch = await _enricher.LoadBatchAsync(product.ProductKey, ct);
        int queries = 0, found = 0, created = 0, dup = 0, discarded = 0;
        var seenPhones = new HashSet<string>();
        var seenPlaces = new HashSet<string>();

        foreach (var term in rubro.SearchTerms)
        {
            queries++;
            var res = await _places.RunAsync(new SourceRunRequest(product, zone.SearchCity, zone.SearchProvince, term, perQuery), ct);
            foreach (var lead in res.Leads)
            {
                found++;
                lead.WhatsappPhone = _phone.NormalizeBusiness(lead.RawPhone, product.PhonePrefix);
                lead.Types = TypesFrom(lead.RawDataJson);

                if (!Usable(lead)) { discarded++; continue; }

                var phone = lead.WhatsappPhone;
                var placeId = lead.PlaceId;
                var exists = (phone is not null && !seenPhones.Add(phone))
                          || (placeId is not null && !seenPlaces.Add(placeId))
                          || (phone is not null && await _db.Leads.AnyAsync(l => l.ProductKey == product.ProductKey && l.WhatsappPhone == phone, ct))
                          || (placeId is not null && await _db.Leads.AnyAsync(l => l.ProductKey == product.ProductKey && l.PlaceId == placeId, ct));
                if (exists) { dup++; continue; }

                lead.Id = Guid.NewGuid();
                lead.Product = product;
                lead.Source = LeadSource.GooglePlaces;
                lead.Status = LeadStatus.New;
                lead.SellerId = null;
                lead.City = ProspectScorer.ZoneFor(lead.Latitude, lead.Longitude)?.Label ?? zone.Label;
                lead.Province = zone.SearchProvince;
                lead.Tags = new List<string> { OutboundService.ReservedTag };
                ProspectEnricher.Rescore(lead, batch);
                _db.Leads.Add(lead);
                created++;
            }
        }
        await _db.SaveChangesAsync(ct);
        _log.LogInformation("Prospectos {Rubro} en {Zona}: {Found} encontrados, {Created} nuevos, {Dup} repetidos, {Disc} descartados",
            rubro.Key, zone.Key, found, created, dup, discarded);
        return new SearchResult(queries, found, created, dup, discarded);
    }

    /// <summary>Sin WhatsApp posible (ni teléfono ni web donde buscarlo), cerrado o mal puntuado no sirve.</summary>
    private static bool Usable(Lead lead)
    {
        if (string.IsNullOrWhiteSpace(lead.WhatsappPhone) && string.IsNullOrWhiteSpace(lead.Website)) return false;
        if (lead.BusinessStatus is "CLOSED_PERMANENTLY" or "CLOSED_TEMPORARILY") return false;
        if (lead.Rating is < 2.5 && lead.TotalReviews is >= 10) return false;
        return true;
    }

    private static List<string> TypesFrom(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return new();
        try
        {
            using var doc = JsonDocument.Parse(raw);
            return doc.RootElement.TryGetProperty("types", out var t) && t.ValueKind == JsonValueKind.Array
                ? t.EnumerateArray().Select(x => x.GetString()).Where(x => x is not null).Select(x => x!).ToList()
                : new();
        }
        catch (JsonException) { return new(); }
    }

    /// <summary>
    /// Productos cuyos leads se nutren: los de las líneas outbound, más los que tienen leads traídos para el
    /// outbound aunque la línea todavía no exista (así el ranking está listo antes de escanear).
    /// </summary>
    public async Task<List<string>> ProductsToScoreAsync(CancellationToken ct)
    {
        var fromLines = await _db.EvolutionInstances.AsNoTracking()
            .Where(i => i.OutboundCap > 0 && i.ProductKey != null)
            .Select(i => i.ProductKey!)
            .ToListAsync(ct);
        var fromTags = await _db.Leads.AsNoTracking()
            .Where(l => l.Tags.Contains(OutboundService.ReservedTag))
            .Select(l => l.ProductKey)
            .Distinct()
            .ToListAsync(ct);
        return fromLines.Concat(fromTags).Distinct().ToList();
    }

    /// <summary>
    /// Una tanda de enriquecimiento: los leads fríos sin contactar que todavía no se nutrieron (la web se
    /// lee una sola vez). Devuelve cuántos procesó.
    /// </summary>
    public async Task<int> EnrichPendingAsync(string productKey, int take, CancellationToken ct)
    {
        var leads = await _db.Leads
            .Where(l => l.ProductKey == productKey && (int)l.Source < 400
                     && l.ProspectEnrichedAt == null
                     && (l.Status == LeadStatus.New || l.Status == LeadStatus.Assigned)
                     && l.SentAt == null)
            .OrderByDescending(l => l.Tags.Contains(OutboundService.ReservedTag))
            .ThenByDescending(l => l.CreatedAt)
            .Take(take)
            .ToListAsync(ct);
        if (leads.Count == 0) return 0;

        var batch = await _enricher.LoadBatchAsync(productKey, ct);
        foreach (var lead in leads)
        {
            ct.ThrowIfCancellationRequested();
            await _enricher.EnrichAsync(lead, batch, ct);
            lead.UpdatedAt = DateTimeOffset.UtcNow;
        }
        await _db.SaveChangesAsync(ct);
        return leads.Count;
    }

    /// <summary>Recalcula el puntaje de todos los fríos sin contactar del producto (sin leer las webs de nuevo).</summary>
    public async Task<int> RescoreAllAsync(string productKey, CancellationToken ct)
    {
        var batch = await _enricher.LoadBatchAsync(productKey, ct);
        var leads = await _db.Leads
            .Where(l => l.ProductKey == productKey && (int)l.Source < 400
                     && (l.Status == LeadStatus.New || l.Status == LeadStatus.Assigned)
                     && l.SentAt == null)
            .ToListAsync(ct);
        foreach (var lead in leads)
        {
            if (lead.Types is not { Count: > 0 }) lead.Types = TypesFrom(lead.RawDataJson);
            ProspectEnricher.Rescore(lead, batch);
        }
        await _db.SaveChangesAsync(ct);
        return leads.Count;
    }
}
