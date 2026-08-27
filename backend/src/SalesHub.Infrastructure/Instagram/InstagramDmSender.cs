using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SalesHub.Core.Domain.Entities;
using SalesHub.Core.Domain.Enums;
using SalesHub.Infrastructure.Persistence;

namespace SalesHub.Infrastructure.Instagram;

/// <summary>
/// Equivalente del <c>OutboxSender</c> para el canal Instagram: procesa la cola
/// de <see cref="MessageOutbox"/> con <c>Channel == Instagram</c>, despachando
/// los DMs vía <see cref="InstagramClient"/>. Maneja el lifecycle del row
/// (lock, estado, reintentos), las métricas de la cuenta y el hilo de
/// conversación, igual que el sender de WhatsApp.
/// </summary>
public class InstagramDmSender
{
    /// <summary>Tras este tiempo en Sending, una fila se considera zombie (proceso reiniciado).</summary>
    private static readonly TimeSpan StaleLockTimeout = TimeSpan.FromMinutes(15);

    private readonly ApplicationDbContext _db;
    private readonly InstagramEncryptionService _crypto;
    private readonly InstagramOptions _opts;
    private readonly ILogger<InstagramDmSender> _log;

    public InstagramDmSender(
        ApplicationDbContext db,
        InstagramEncryptionService crypto,
        InstagramOptions opts,
        ILogger<InstagramDmSender> log)
    {
        _db = db;
        _crypto = crypto;
        _opts = opts;
        _log = log;
    }

    /// <summary>
    /// Procesa la cola de DMs de Instagram: recupera filas zombie y despacha como
    /// mucho 1 DM por tick (el pacing anti-ban lo da el intervalo del worker).
    /// Devuelve cuántos DMs se enviaron efectivamente.
    /// </summary>
    public async Task<int> TickAsync(CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;

        // Recuperar filas Sending viejas (proceso reiniciado a mitad de un envío).
        var staleCutoff = now - StaleLockTimeout;
        var stale = await _db.Outbox
            .Where(o => o.Channel == MessageChannel.Instagram
                     && o.Status == OutboxStatus.Sending
                     && o.LockedAt != null && o.LockedAt < staleCutoff)
            .ToListAsync(ct);
        if (stale.Count > 0)
        {
            foreach (var s in stale)
            {
                s.Status = s.Attempts >= 3 ? OutboxStatus.Failed : OutboxStatus.Scheduled;
                s.LockedAt = null;
            }
            await _db.SaveChangesAsync(ct);
            _log.LogWarning("Reclaimed {N} stale Instagram DM rows", stale.Count);
        }

        // Política de mensajería por origen (misma que WhatsApp): un lead que nunca recibió
        // nada es MENSAJE NUEVO, si ya recibió algo es SEGUIMIENTO. Lo bloqueado queda
        // Scheduled y sale cuando se vuelva a prender.
        var policy = await _db.LoadMessagingPolicyAsync(ct);
        var outreachSources = policy.OutreachSources;
        var followupSources = policy.FollowupSources;

        // Tomar el DM de IG pendiente más viejo.
        var next = await _db.Outbox
            .Include(o => o.Lead)
            .Where(o => o.Channel == MessageChannel.Instagram
                     && o.Status == OutboxStatus.Scheduled
                     && o.ScheduledAt <= now
                     && (o.Lead == null
                         || (_db.Outbox.Any(x => x.LeadId == o.LeadId && x.Status == OutboxStatus.Sent)
                             && followupSources.Contains(o.Lead.Source))
                         || (!_db.Outbox.Any(x => x.LeadId == o.LeadId && x.Status == OutboxStatus.Sent)
                             && outreachSources.Contains(o.Lead.Source))))
            .OrderBy(o => o.ScheduledAt)
            .FirstOrDefaultAsync(ct);

        if (next is null) return 0;

        var handle = next.Lead?.InstagramHandle;
        if (string.IsNullOrWhiteSpace(handle))
        {
            next.Status = OutboxStatus.Cancelled;
            next.Error = "Lead sin InstagramHandle";
            await _db.SaveChangesAsync(ct);
            return 0;
        }

        // Lock optimista antes de abrir el browser.
        next.Status = OutboxStatus.Sending;
        next.LockedAt = now;
        await _db.SaveChangesAsync(ct);

        var result = await SendOneAsync(next, handle!, ct);

        switch (result)
        {
            case InstagramDmResult.Sent:
                next.Status = OutboxStatus.Sent;
                next.SentAt = DateTimeOffset.UtcNow;
                next.LockedAt = null;
                next.Error = null;
                await MarkLeadAndThreadAsync(next, ct);
                await _db.SaveChangesAsync(ct);
                _log.LogInformation("DM IG enviado a @{Handle} (outbox {Id})", handle, next.Id);
                return 1;

            case InstagramDmResult.NoAccountAvailable:
            case InstagramDmResult.RateLimited:
            case InstagramDmResult.SessionExpired:
                // No es un fallo del mensaje: reintentar más tarde sin consumir intento.
                next.Status = OutboxStatus.Scheduled;
                next.LockedAt = null;
                next.ScheduledAt = DateTimeOffset.UtcNow.AddMinutes(
                    result == InstagramDmResult.RateLimited ? 60 : 15);
                next.Error = result.ToString();
                await _db.SaveChangesAsync(ct);
                return 0;

            case InstagramDmResult.SendFailed:
            default:
                next.Attempts++;
                next.LockedAt = null;
                next.Error = "ig_dm_failed";
                next.Status = next.Attempts >= 3 ? OutboxStatus.Failed : OutboxStatus.Scheduled;
                if (next.Status == OutboxStatus.Scheduled)
                    next.ScheduledAt = DateTimeOffset.UtcNow.AddMinutes(Random.Shared.Next(10, 31));
                await _db.SaveChangesAsync(ct);
                return 0;
        }
    }

    /// <summary>
    /// Elige una cuenta de IG (la del seller asignado, o cualquiera disponible),
    /// respeta el cap diario y manda el DM. No toca el estado del outbox row.
    /// </summary>
    private async Task<InstagramDmResult> SendOneAsync(MessageOutbox item, string handle, CancellationToken ct)
    {
        var lead = item.Lead;

        // ROTACIÓN entre cuentas (estilo "N drivers × M mensajes"): entre las activas/logueadas
        // que todavía tienen cupo hoy, manda la que MENOS DMs mandó (empate: la menos usada).
        // Primero las del seller asignado; si ninguna tiene cupo, cualquiera con cupo.
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var candidates = await _db.InstagramAccounts
            .Where(a => a.IsActive && !a.IsActionBlocked && a.IsLoggedIn)
            .ToListAsync(ct);
        if (candidates.Count == 0)
        {
            _log.LogWarning("No hay cuentas de Instagram disponibles para DM a @{Handle}", handle);
            return InstagramDmResult.NoAccountAvailable;
        }
        var ids = candidates.Select(a => a.Id).ToList();
        var sentToday = await _db.InstagramMetrics
            .Where(m => ids.Contains(m.InstagramAccountId) && m.Date == today)
            .ToDictionaryAsync(m => m.InstagramAccountId, m => m.DmSent, ct);
        int CapOf(InstagramAccount a) => a.DailyDmCap is > 0 ? a.DailyDmCap.Value : _opts.MaxDmPerDay;
        int SentOf(InstagramAccount a) => sentToday.GetValueOrDefault(a.Id);
        var withRoom = candidates.Where(a => SentOf(a) < CapOf(a)).ToList();
        if (withRoom.Count == 0)
        {
            _log.LogInformation("Todas las cuentas de IG ({N}) alcanzaron su cap diario de DMs", candidates.Count);
            return InstagramDmResult.RateLimited;
        }
        var pool = lead?.SellerId is not null && withRoom.Any(a => a.SellerId == lead.SellerId)
            ? withRoom.Where(a => a.SellerId == lead.SellerId).ToList()
            : withRoom;
        var account = pool
            .OrderBy(SentOf)
            .ThenBy(a => a.LastUsedAt ?? DateTimeOffset.MinValue)
            .First();
        _log.LogInformation("DM IG a @{Handle} sale por {User} ({Sent}/{Cap} hoy)", handle, account.Username, SentOf(account), CapOf(account));

        await using var client = new InstagramClient(_db, _crypto, _opts, _log);
        await client.InitializeAsync(account, ct);

        var sessionOk = await client.CheckSessionAsync(ct);
        if (!sessionOk)
        {
            _log.LogWarning("Sesión de Instagram expirada para {User}", account.Username);
            account.IsLoggedIn = false;
            account.SessionCookiesJson = null;
            await _db.SaveChangesAsync(ct);
            return InstagramDmResult.SessionExpired;
        }

        var ok = await client.SendDmAsync(handle, item.Message, ct);

        await UpdateDmMetricsAsync(account.Id, ok, ct);
        account.LastUsedAt = DateTimeOffset.UtcNow;
        await client.SaveSessionCookiesAsync(account, ct);
        await _db.SaveChangesAsync(ct);

        return ok ? InstagramDmResult.Sent : InstagramDmResult.SendFailed;
    }

    /// <summary>Marca el lead como contactado y registra el outbound en el hilo + stats.</summary>
    private async Task MarkLeadAndThreadAsync(MessageOutbox item, CancellationToken ct)
    {
        var lead = await _db.Leads.FirstOrDefaultAsync(l => l.Id == item.LeadId, ct);
        if (lead is not null && lead.Status != LeadStatus.Sent)
        {
            lead.Status = LeadStatus.Sent;
            lead.SentAt = DateTimeOffset.UtcNow;
        }

        _db.ConversationMessages.Add(new ConversationMessage
        {
            Id = Guid.NewGuid(),
            LeadId = item.LeadId,
            SellerId = item.SellerId,
            Direction = MessageDirection.Outbound,
            Status = MessageDeliveryStatus.Sent,
            Text = item.Message,
            EvolutionInstance = string.Empty,
            Timestamp = DateTimeOffset.UtcNow,
            IsRead = true
        });

        // Stats del vendedor (aprox., en UTC — el canal IG no maneja timezone propia).
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var stats = await _db.DailyStats.FindAsync(new object[] { item.SellerId, today }, ct);
        if (stats is null)
        {
            stats = new SellerDailyStats { SellerId = item.SellerId, Date = today };
            _db.DailyStats.Add(stats);
        }
        stats.MessagesSent++;
    }

    private async Task UpdateDmMetricsAsync(Guid accountId, bool success, CancellationToken ct)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var metrics = await _db.InstagramMetrics
            .FirstOrDefaultAsync(m => m.InstagramAccountId == accountId && m.Date == today, ct);

        if (metrics is null)
        {
            metrics = new InstagramMetrics
            {
                Id = Guid.NewGuid(),
                InstagramAccountId = accountId,
                Date = today
            };
            _db.InstagramMetrics.Add(metrics);
        }

        if (success)
            metrics.DmSent++;
        else
            metrics.DmFailed++;

        metrics.UpdatedAt = DateTimeOffset.UtcNow;
    }
}

/// <summary>Resultado de intentar despachar un DM de Instagram.</summary>
public enum InstagramDmResult
{
    /// <summary>Enviado OK.</summary>
    Sent,
    /// <summary>No había ninguna cuenta de IG disponible para mandar.</summary>
    NoAccountAvailable,
    /// <summary>La cuenta llegó a su cap diario de DMs.</summary>
    RateLimited,
    /// <summary>La sesión de la cuenta expiró (hay que re-loguear).</summary>
    SessionExpired,
    /// <summary>Se intentó el envío pero Instagram/el browser falló.</summary>
    SendFailed
}
