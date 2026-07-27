using Microsoft.EntityFrameworkCore;
using SalesHub.Core.Domain;
using SalesHub.Core.Domain.Entities;
using SalesHub.Core.Domain.Enums;

namespace SalesHub.Infrastructure.Persistence;

/// <summary>
/// Foto de la política de mensajería resuelta a listas de <see cref="LeadSource"/>, que es
/// lo que necesitan las queries EF (un <c>Contains</c> sobre la columna source). Sin fila
/// para un grupo, ese grupo queda permitido.
/// </summary>
public sealed class MessagingPolicySnapshot
{
    private readonly Dictionary<string, MessagingPolicy> _byGroup;

    public MessagingPolicySnapshot(IEnumerable<MessagingPolicy> rows)
    {
        _byGroup = rows.ToDictionary(r => r.SourceGroup, StringComparer.OrdinalIgnoreCase);
        OutreachSources = Allowed(p => p.AllowOutreach);
        FollowupSources = Allowed(p => p.AllowFollowup);
        ReplySources = Allowed(p => p.AllowReply);
    }

    /// <summary>Orígenes a los que SÍ se les puede abrir un primer contacto.</summary>
    public List<LeadSource> OutreachSources { get; }

    /// <summary>Orígenes a los que SÍ se les puede seguir escribiendo (cadencia, nudges).</summary>
    public List<LeadSource> FollowupSources { get; }

    /// <summary>Orígenes cuyos mensajes SÍ contesta el bot.</summary>
    public List<LeadSource> ReplySources { get; }

    public bool AllowsOutreach(LeadSource s) => Get(s)?.AllowOutreach ?? true;
    public bool AllowsFollowup(LeadSource s) => Get(s)?.AllowFollowup ?? true;
    public bool AllowsReply(LeadSource s) => Get(s)?.AllowReply ?? true;

    private MessagingPolicy? Get(LeadSource s) =>
        _byGroup.TryGetValue(MessagingSourceGroups.For(s), out var p) ? p : null;

    private List<LeadSource> Allowed(Func<MessagingPolicy, bool> pick) =>
        Enum.GetValues<LeadSource>()
            .Where(s => _byGroup.TryGetValue(MessagingSourceGroups.For(s), out var p) ? pick(p) : true)
            .ToList();
}

public static class MessagingPolicyExtensions
{
    /// <summary>
    /// Carga la política (≤6 filas) para usarla en un tick. No se cachea a propósito:
    /// apagar un switch tiene que cortar los envíos en el tick siguiente, no en 30s.
    /// </summary>
    public static async Task<MessagingPolicySnapshot> LoadMessagingPolicyAsync(
        this ApplicationDbContext db, CancellationToken ct = default) =>
        new(await db.MessagingPolicies.AsNoTracking().ToListAsync(ct));
}
