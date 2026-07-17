using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using SalesHub.Core.Abstractions;
using SalesHub.Core.Domain.Entities;
using SalesHub.Core.Domain.Enums;
using SalesHub.Infrastructure.Persistence;

namespace SalesHub.Infrastructure.Services;

/// <summary>
/// Auto-reparto de leads disparado por cambios en la capacidad de envío (ej: un vendedor
/// conecta su WhatsApp; el InstanceMonitor lo marca Connected en &lt;60s y el próximo tick
/// de LeadRebalanceWorker reparte solo). Hace 3 cosas, en orden:
///
///  1. Rescate: libera al pool los leads sin contactar de vendedores que NO pueden enviar
///     hace más de Rebalance:FrozenAfterHours (inactivos o desconectados). No toca a los
///     pausados a propósito (SendingEnabled=false con instancia conectada) ni a los que se
///     desconectaron hace poco (ventana de gracia para reinicios/re-scan de QR).
///  2. Retry del pool: re-corre el assigner sobre huérfanos cuyo producto hoy SÍ tiene
///     vendedor elegible. Necesario porque la asignación normal corre UNA sola vez al
///     crear el lead — sin esto el pool no tiene ningún reintento automático.
///  3. Drenaje generalista→especialista: cuando se conecta una línea dedicada (whitelist
///     más chica), le pasa backlog sin contactar de los vendedores menos específicos hasta
///     Rebalance:TargetDaysOfWork × DailyCap. Nunca al revés — una línea dedicada no
///     devuelve leads al catch-all — así el reparto es unidireccional y no hay ping-pong
///     entre ticks.
///
/// Solo mueve leads que todavía no arrancaron conversación (SentAt y FirstReplyAt null);
/// al mover siempre cancela la cadencia pendiente del seller viejo y encola una nueva
/// renderizada para el nuevo (mismo cuidado anti-duplicados que /leads/release).
/// </summary>
public class LeadRebalancer
{
    private readonly ApplicationDbContext _db;
    private readonly ILeadAssigner _assigner;
    private readonly IMessageRenderer _renderer;
    private readonly IConfiguration _config;
    private readonly ILogger<LeadRebalancer> _log;

    // El retry del pool renderiza + encola por lead; capado por tick para mantener el
    // changeset liviano en el droplet — un backlog grande se procesa en ticks sucesivos.
    private const int OrphanBatchPerTick = 500;

    public LeadRebalancer(
        ApplicationDbContext db, ILeadAssigner assigner, IMessageRenderer renderer,
        IConfiguration config, ILogger<LeadRebalancer> log)
    {
        _db = db; _assigner = assigner; _renderer = renderer; _config = config; _log = log;
    }

    public record RebalanceResult(int MismatchReleased, int Rescued, int OrphansAssigned, int Drained);

    /// <param name="force">
    /// Disparo manual desde el botón "Reasignar todo": rescata YA los leads sin contactar de
    /// cualquier vendedor que no pueda enviar (sin esperar la ventana de gracia de
    /// Rebalance:FrozenAfterHours que usa el tick automático). El release por whitelist corre
    /// siempre, con o sin force.
    /// </param>
    public async Task<RebalanceResult> RebalanceAsync(CancellationToken ct, bool force = false)
    {
        // Nuevo: soltar al pool los leads pegados a un vendedor CONECTADO al que el admin le
        // sacó ese producto de la whitelist (cambió las apps asignadas). Sin esto quedaban
        // "muertos" — ningún otro path los rescataba.
        var mismatch = await ReleaseConfigMismatchedAsync(ct);
        var rescued = await RescueFrozenAsync(ct, force);

        // Mismos criterios de elegibilidad que LeadAssigner: listos para enviar AHORA.
        var capable = (await _db.Sellers
                .Include(s => s.EvolutionInstance)
                .Where(s => s.IsActive
                         && s.SendingEnabled
                         && s.EvolutionInstance != null
                         && s.EvolutionInstance.Status == InstanceStatus.Connected)
                .ToListAsync(ct))
            .Where(s => s.Role == SellerRole.Seller
                     || (s.Role == SellerRole.Admin && s.VerticalsWhitelist is { Count: > 0 }))
            .ToList();

        if (capable.Count == 0) return new RebalanceResult(mismatch, rescued, 0, 0);

        var orphansAssigned = await RetryOrphansAsync(capable, ct);
        var drained = await DrainToSpecialistsAsync(capable, ct);

        if (mismatch + rescued + orphansAssigned + drained > 0)
            _log.LogInformation("Rebalance: {Mismatch} liberados por whitelist, {Rescued} rescatados al pool, {Orphans} huérfanos asignados, {Drained} drenados a líneas dedicadas",
                mismatch, rescued, orphansAssigned, drained);
        return new RebalanceResult(mismatch, rescued, orphansAssigned, drained);
    }

    /// <summary>
    /// Leads sin contactar pegados a un vendedor que SIGUE conectado y enviando, pero cuya
    /// whitelist ya NO incluye el producto del lead (el admin le cambió las apps asignadas) → al
    /// pool, para que RetryOrphans los reparta a quien sí corresponda. Solo aplica a vendedores
    /// con whitelist explícita: los catch-all (whitelist vacía) aceptan todo, no hay mismatch.
    /// </summary>
    private async Task<int> ReleaseConfigMismatchedAsync(CancellationToken ct)
    {
        // Pocos sellers: traemos todos y filtramos en memoria (VerticalsWhitelist es jsonb,
        // .Count no traduce a SQL de forma confiable).
        var restricted = (await _db.Sellers.ToListAsync(ct))
            .Where(s => s.VerticalsWhitelist is { Count: > 0 })
            .ToDictionary(s => s.Id, s => new HashSet<string>(s.VerticalsWhitelist, StringComparer.OrdinalIgnoreCase));
        if (restricted.Count == 0) return 0;

        var restrictedIds = restricted.Keys.ToList();
        var held = await _db.Leads
            .Where(l => l.SellerId != null && restrictedIds.Contains(l.SellerId.Value)
                     && (l.Status == LeadStatus.Assigned || l.Status == LeadStatus.Queued)
                     && l.SentAt == null && l.FirstReplyAt == null)
            .ToListAsync(ct);

        var mismatched = held
            .Where(l => restricted.TryGetValue(l.SellerId!.Value, out var wl) && !wl.Contains(l.ProductKey))
            .ToList();
        if (mismatched.Count == 0) return 0;

        await CancelPendingOutboxAsync(mismatched.Select(l => l.Id).ToList(), ct);
        foreach (var lead in mismatched)
        {
            lead.SellerId = null;
            lead.AssignedAt = null;
            lead.QueuedAt = null;
            lead.Status = LeadStatus.New;
        }
        await _db.SaveChangesAsync(ct);
        return mismatched.Count;
    }

    /// <summary>Leads sin contactar de sellers que no pueden enviar hace rato → al pool.
    /// Con <paramref name="force"/> ignora la ventana de gracia (rescata ya, sin esperar horas).</summary>
    private async Task<int> RescueFrozenAsync(CancellationToken ct, bool force = false)
    {
        var frozenHours = force ? 0 : _config.GetValue<int>("Rebalance:FrozenAfterHours", 24);
        var cutoff = DateTimeOffset.UtcNow.AddHours(-frozenHours);

        var holders = await _db.Sellers.Include(s => s.EvolutionInstance).ToListAsync(ct);
        var frozenIds = holders
            .Where(s => !s.IsActive
                     || s.EvolutionInstance is null
                     || (s.EvolutionInstance.Status != InstanceStatus.Connected
                         && (s.EvolutionInstance.DisconnectedAt ?? s.EvolutionInstance.UpdatedAt) <= cutoff))
            .Select(s => s.Id)
            .ToHashSet();
        if (frozenIds.Count == 0) return 0;

        var leads = await _db.Leads
            .Where(l => l.SellerId != null && frozenIds.Contains(l.SellerId.Value)
                     && (l.Status == LeadStatus.Assigned || l.Status == LeadStatus.Queued)
                     && l.SentAt == null && l.FirstReplyAt == null)
            .ToListAsync(ct);
        if (leads.Count == 0) return 0;

        await CancelPendingOutboxAsync(leads.Select(l => l.Id).ToList(), ct);
        foreach (var lead in leads)
        {
            lead.SellerId = null;
            lead.AssignedAt = null;
            lead.QueuedAt = null;
            lead.Status = LeadStatus.New;
        }
        await _db.SaveChangesAsync(ct);
        return leads.Count;
    }

    /// <summary>Re-corre el assigner sobre huérfanos cuyo producto tiene vendedor elegible hoy.</summary>
    private async Task<int> RetryOrphansAsync(IReadOnlyList<Seller> capable, CancellationToken ct)
    {
        // Filtrar por productos cubribles ANTES de traer los leads: sin esto cada tick
        // levantaría y re-evaluaría huérfanos imposibles (ej. los que no tienen producto).
        var hasCatchAll = capable.Any(s => s.VerticalsWhitelist is not { Count: > 0 });
        var coverable = capable
            .Where(s => s.VerticalsWhitelist is { Count: > 0 })
            .SelectMany(s => s.VerticalsWhitelist!)
            .Distinct()
            .ToList();

        var q = _db.Leads.Include(l => l.Product)
            .Where(l => l.SellerId == null && l.Status == LeadStatus.New);
        if (!hasCatchAll) q = q.Where(l => coverable.Contains(l.ProductKey));

        var orphans = await q.OrderBy(l => l.CreatedAt).Take(OrphanBatchPerTick).ToListAsync(ct);
        if (orphans.Count == 0) return 0;

        var assigned = 0;
        foreach (var lead in orphans)
        {
            if (lead.Product is null) continue;
            var sellerId = await _assigner.PickForLeadAsync(lead.ProductKey, lead.LocalityGid2, lead.Province, lead.City, ct);
            if (sellerId is null) continue;
            var seller = capable.FirstOrDefault(s => s.Id == sellerId.Value);
            if (seller is null) continue;
            MoveLeadTo(lead, seller);
            assigned++;
        }
        if (assigned > 0) await _db.SaveChangesAsync(ct);
        return assigned;
    }

    /// <summary>Whitelist más chica = línea más dedicada. Vacía = catch-all (la menos específica).</summary>
    private static int Specificity(Seller s) =>
        s.VerticalsWhitelist is { Count: > 0 } ? s.VerticalsWhitelist.Count : int.MaxValue;

    private async Task<int> DrainToSpecialistsAsync(IReadOnlyList<Seller> capable, CancellationToken ct)
    {
        var targetDays = _config.GetValue<int>("Rebalance:TargetDaysOfWork", 3);

        var capableIds = capable.Select(s => s.Id).ToList();
        var unsentBySeller = await _db.Leads
            .Where(l => l.SellerId != null && capableIds.Contains(l.SellerId.Value)
                     && (l.Status == LeadStatus.Assigned || l.Status == LeadStatus.Queued)
                     && l.SentAt == null && l.FirstReplyAt == null)
            .GroupBy(l => l.SellerId!.Value)
            .Select(g => new { SellerId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.SellerId, x => x.Count, ct);

        var drained = 0;
        // Las líneas más dedicadas eligen primero.
        foreach (var receiver in capable.Where(s => s.VerticalsWhitelist is { Count: > 0 }).OrderBy(Specificity))
        {
            var target = targetDays * Math.Max(1, receiver.DailyCap);
            var deficit = target - unsentBySeller.GetValueOrDefault(receiver.Id);
            if (deficit <= 0) continue;

            foreach (var vertical in receiver.VerticalsWhitelist!)
            {
                if (deficit <= 0) break;
                var donors = capable
                    .Where(d => d.Id != receiver.Id
                             && Specificity(d) > Specificity(receiver)
                             && (d.VerticalsWhitelist is not { Count: > 0 } || d.VerticalsWhitelist.Contains(vertical))
                             && unsentBySeller.GetValueOrDefault(d.Id) > 0)
                    .OrderByDescending(Specificity);

                foreach (var donor in donors)
                {
                    if (deficit <= 0) break;
                    var movable = await _db.Leads.Include(l => l.Product)
                        .Where(l => l.SellerId == donor.Id && l.ProductKey == vertical
                                 && (l.Status == LeadStatus.Assigned || l.Status == LeadStatus.Queued)
                                 && l.SentAt == null && l.FirstReplyAt == null)
                        .OrderBy(l => l.CreatedAt)
                        .Take(deficit)
                        .ToListAsync(ct);
                    if (movable.Count == 0) continue;

                    await CancelPendingOutboxAsync(movable.Select(l => l.Id).ToList(), ct);
                    foreach (var lead in movable) MoveLeadTo(lead, receiver);

                    deficit -= movable.Count;
                    drained += movable.Count;
                    unsentBySeller[donor.Id] = unsentBySeller.GetValueOrDefault(donor.Id) - movable.Count;
                    unsentBySeller[receiver.Id] = unsentBySeller.GetValueOrDefault(receiver.Id) + movable.Count;
                    _log.LogInformation("Rebalance drain: {N} leads de {Vertical} {Donor} → {Receiver}",
                        movable.Count, vertical, donor.DisplayName, receiver.DisplayName);
                }
            }
        }
        if (drained > 0) await _db.SaveChangesAsync(ct);
        return drained;
    }

    /// <summary>Asigna + re-renderiza + encola la cadencia en el seller destino (ya conectado).</summary>
    private void MoveLeadTo(Lead lead, Seller seller)
    {
        lead.SellerId = seller.Id;
        lead.AssignedAt = DateTimeOffset.UtcNow;
        lead.Status = LeadStatus.Assigned;
        if (lead.Product is null) return;

        lead.RenderedMessage = _renderer.Render(lead, lead.Product, seller);
        lead.WhatsappLink = string.IsNullOrWhiteSpace(lead.WhatsappPhone)
            ? null
            : $"https://wa.me/{lead.WhatsappPhone}?text={Uri.EscapeDataString(lead.RenderedMessage ?? "")}";

        if (!string.IsNullOrWhiteSpace(lead.WhatsappPhone) && seller.EvolutionInstance is not null)
        {
            OutboxEnqueueHelper.EnqueueLeadMessages(
                _db, _renderer, lead, lead.Product, seller,
                lead.WhatsappPhone, seller.EvolutionInstance.InstanceName);
            lead.Status = LeadStatus.Queued;
            lead.QueuedAt = DateTimeOffset.UtcNow;
        }
    }

    private async Task<int> CancelPendingOutboxAsync(IReadOnlyCollection<Guid> leadIds, CancellationToken ct)
    {
        var pending = await _db.Outbox
            .Where(o => leadIds.Contains(o.LeadId)
                     && (o.Status == OutboxStatus.Scheduled || o.Status == OutboxStatus.Sending))
            .ToListAsync(ct);
        foreach (var row in pending) row.Status = OutboxStatus.Cancelled;
        return pending.Count;
    }
}
