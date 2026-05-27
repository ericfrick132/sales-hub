using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SalesHub.Core.Domain.Entities;
using SalesHub.Infrastructure.Persistence;

namespace SalesHub.Infrastructure.Instagram;

/// <summary>
/// Envía DMs de Instagram a leads usando el InstagramClient.
/// Se integra con el OutboxSender para el flujo automático de mensajes.
/// </summary>
public class InstagramDmSender
{
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
    /// Envía un DM de Instagram a un lead.
    /// Busca una cuenta de Instagram disponible del seller asignado al lead.
    /// </summary>
    public async Task<bool> SendDmAsync(MessageOutbox outboxItem, CancellationToken ct = default)
    {
        var lead = outboxItem.Lead;
        var handle = lead?.InstagramHandle;
        if (string.IsNullOrEmpty(handle))
        {
            _log.LogWarning("Outbox item {Id} no tiene InstagramHandle", outboxItem.Id);
            return false;
        }

        // Buscar cuenta de Instagram del seller asignado al lead
        InstagramAccount? account = null;

        if (lead?.SellerId is not null)
        {
            account = await _db.InstagramAccounts
                .Where(a => a.SellerId == lead.SellerId && a.IsActive && !a.IsActionBlocked)
                .FirstOrDefaultAsync(ct);
        }

        // Si no hay cuenta del seller, usar cualquier cuenta disponible
        account ??= await _db.InstagramAccounts
            .Where(a => a.IsActive && !a.IsActionBlocked)
            .FirstOrDefaultAsync(ct);

        if (account is null)
        {
            _log.LogWarning("No hay cuentas de Instagram disponibles para enviar DM a {Handle}",
                handle);
            return false;
        }

        if (account.IsActionBlocked)
        {
            _log.LogWarning("Cuenta {User} está bloqueada, no se puede enviar DM", account.Username);
            return false;
        }

        // Verificar rate limiting
        var dmSentToday = await _db.InstagramMetrics
            .Where(m => m.InstagramAccountId == account.Id && m.Date == DateOnly.FromDateTime(DateTime.UtcNow))
            .Select(m => (int?)m.DmSent)
            .FirstOrDefaultAsync(ct);

        if (dmSentToday.HasValue && dmSentToday.Value >= _opts.MaxDmPerHour)
        {
            _log.LogWarning("Cuenta {User} alcanzó el límite de DMs por hora ({Limit})",
                account.Username, _opts.MaxDmPerHour);
            return false;
        }

        // Inicializar cliente y enviar
        await using var client = new InstagramClient(_db, _crypto, _opts, _log);
        await client.InitializeAsync(account, ct);

        var sessionOk = await client.CheckSessionAsync(ct);
        if (!sessionOk)
        {
            _log.LogWarning("Sesión de Instagram expirada para {User}", account.Username);
            account.SessionCookiesJson = null;
            await _db.SaveChangesAsync(ct);
            return false;
        }

        var success = await client.SendDmAsync(handle, outboxItem.Message, ct);

        // Actualizar métricas
        await UpdateDmMetricsAsync(account.Id, success, ct);

        // Guardar cookies actualizadas
        await client.SaveSessionCookiesAsync(account, ct);

        return success;
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
