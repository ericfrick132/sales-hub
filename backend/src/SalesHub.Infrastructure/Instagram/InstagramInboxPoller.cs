using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SalesHub.Core.Domain.Entities;
using SalesHub.Infrastructure.Persistence;
using SalesHub.Infrastructure.Services;

namespace SalesHub.Infrastructure.Instagram;

/// <summary>
/// Pollea el inbox de DMs de cada cuenta de Instagram logueada y mete los mensajes
/// entrantes en las conversaciones (vía <see cref="ConversationService"/>), igual
/// que el webhook de WhatsApp. Es el camino de ENTRADA del canal Instagram.
/// </summary>
public class InstagramInboxPoller
{
    private readonly ApplicationDbContext _db;
    private readonly InstagramEncryptionService _crypto;
    private readonly InstagramOptions _opts;
    private readonly ConversationService _conversations;
    private readonly ILogger<InstagramInboxPoller> _log;

    public InstagramInboxPoller(
        ApplicationDbContext db,
        InstagramEncryptionService crypto,
        InstagramOptions opts,
        ConversationService conversations,
        ILogger<InstagramInboxPoller> log)
    {
        _db = db;
        _crypto = crypto;
        _opts = opts;
        _conversations = conversations;
        _log = log;
    }

    /// <summary>Pollea el inbox de todas las cuentas logueadas. Devuelve cuántos mensajes nuevos guardó.</summary>
    public async Task<int> PollAllAsync(CancellationToken ct = default)
    {
        var accounts = await _db.InstagramAccounts
            .Where(a => a.IsActive && !a.IsActionBlocked && a.IsLoggedIn)
            .ToListAsync(ct);

        if (accounts.Count == 0) return 0;

        var total = 0;
        foreach (var account in accounts)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                total += await PollAccountAsync(account, ct);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "IG inbox poll falló para {User}", account.Username);
            }
        }
        return total;
    }

    private async Task<int> PollAccountAsync(InstagramAccount account, CancellationToken ct)
    {
        await using var client = new InstagramClient(_db, _crypto, _opts, _log);
        await client.InitializeAsync(account, ct);

        var sessionOk = await client.CheckSessionAsync(ct);
        if (!sessionOk)
        {
            _log.LogWarning("Sesión caída polleando inbox de {User}", account.Username);
            account.IsLoggedIn = false;
            account.SessionCookiesJson = null;
            await _db.SaveChangesAsync(ct);
            return 0;
        }

        var messages = await client.ReadInboxAsync(20, ct);

        account.LastUsedAt = DateTimeOffset.UtcNow;
        await client.SaveSessionCookiesAsync(account, ct);
        await _db.SaveChangesAsync(ct);

        var stored = 0;
        foreach (var m in messages)
        {
            if (await _conversations.HandleInstagramInboundAsync(m.Handle, m.ExternalId, m.Text, m.Timestamp, ct))
                stored++;
        }

        if (stored > 0)
            _log.LogInformation("IG inbox {User}: {Stored} mensajes nuevos guardados", account.Username, stored);

        return stored;
    }
}
