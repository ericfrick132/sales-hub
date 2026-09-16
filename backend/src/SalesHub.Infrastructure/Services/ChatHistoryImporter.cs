using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SalesHub.Core.Abstractions;
using SalesHub.Core.Domain.Entities;
using SalesHub.Infrastructure.Evolution;
using SalesHub.Infrastructure.Persistence;

namespace SalesHub.Infrastructure.Services;

/// <summary>
/// Lee los chats 1:1 de una instancia directo de Evolution (findChats + findMessages) y pasa
/// cada mensaje por el mismo ingest del webhook con FromSync=true. Lo usan el sync periódico
/// (ventana corta) y la carga del historial de un teléfono recién escaneado (sin límite de
/// fecha). Idempotente: el ingest deduplica por id de mensaje.
/// </summary>
public class ChatHistoryImporter
{
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<ChatHistoryImporter> _log;

    public ChatHistoryImporter(IServiceScopeFactory scopes, ILogger<ChatHistoryImporter> log)
    {
        _scopes = scopes; _log = log;
    }

    public const int PageSize = 50;

    /// <summary>
    /// Pasado este tiempo un mensaje importado es HISTORIA: no puede hacer que el bot conteste
    /// ni re-enganche (ver ConversationService.IsHistory).
    /// </summary>
    public static readonly TimeSpan HistoryAge = ConversationService.HistoryAge;

    public record Result(int Chats, int Stored, int Truncated);

    /// <param name="cutoff">Solo mensajes desde esta fecha; null = todo lo que tenga Evolution.</param>
    /// <param name="maxPagesPerChat">Techo por chat (PageSize × esto); lo que pase queda sin importar y se loguea.</param>
    /// <param name="muteHistoricLeads">
    /// Carga de historial: a un lead que YA existía y cuya última actividad pasa a ser un mensaje
    /// viejo de esta línea se le apaga el bot, para que el re-enganche no le escriba por una
    /// charla de hace meses. Los leads que crea la importación ya nacen muteados.
    /// </param>
    public async Task<Result> ImportAsync(string instanceName, DateTimeOffset? cutoff, int maxPagesPerChat,
        bool muteHistoricLeads, CancellationToken ct)
    {
        IReadOnlyList<EvolutionChatSummary> chats;
        using (var scope = _scopes.CreateScope())
            chats = await scope.ServiceProvider.GetRequiredService<IEvolutionClient>().FindChatsAsync(instanceName, ct);

        // Solo chats 1:1 (persona o LID). Afuera grupos, canales y status.
        var eligible = chats
            .Where(c => c.RemoteJid.EndsWith("@s.whatsapp.net", StringComparison.Ordinal)
                     || c.RemoteJid.EndsWith("@lid", StringComparison.Ordinal))
            .Where(c => cutoff is null || (c.UpdatedAt is not null && c.UpdatedAt >= cutoff))
            .ToList();
        if (eligible.Count == 0) return new Result(0, 0, 0);

        var stored = 0;
        var truncated = 0;
        foreach (var chat in eligible)
        {
            ct.ThrowIfCancellationRequested();
            // Scope por CHAT: con miles de mensajes el change-tracking de un solo DbContext
            // acumularía todo en memoria — el droplet tiene 1GB.
            using var chatScope = _scopes.CreateScope();
            var evo = chatScope.ServiceProvider.GetRequiredService<IEvolutionClient>();
            var conv = chatScope.ServiceProvider.GetRequiredService<ConversationService>();

            var records = new List<JsonElement>();
            try
            {
                for (var page = 1; page <= maxPagesPerChat; page++)
                {
                    var res = await evo.FindMessagesAsync(instanceName, chat.RemoteJid, page, PageSize, ct);
                    if (res.Records.Count == 0) break;
                    records.AddRange(res.Records);
                    // Vienen del más nuevo al más viejo: si ya pasamos el cutoff, listo.
                    if (cutoff is not null && res.Records.Min(TsOf) < cutoff) break;
                    if (res.Pages > 0 && page >= res.Pages) break;
                    if (page == maxPagesPerChat) truncated++;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogWarning(ex, "Importación: no pude leer {Chat} de {Instance}", chat.RemoteJid, instanceName);
                continue;
            }

            var ids = new List<string>();
            foreach (var rec in records.Where(r => cutoff is null || TsOf(r) >= cutoff).OrderBy(TsOf))
            {
                // Parse adentro del try: un record podrido (message null, key rara) no debe
                // matar la importación de toda la instancia.
                try
                {
                    var incoming = EvolutionMessageParser.Parse(instanceName, rec, topSender: null, fromSync: true);
                    if (incoming is null) continue;
                    // Chat LID sin teléfono real resolvible (el webhook en vivo tiene el
                    // payload.sender como fallback; acá no) → mejor saltear que inventar.
                    if (incoming.FromJid.EndsWith("@lid", StringComparison.Ordinal) && incoming.FromPhone is null)
                        continue;
                    var handled = incoming.FromMe
                        ? await conv.HandleOwnMessageAsync(incoming, ct)
                        : await conv.HandleIncomingAsync(incoming, ct);
                    if (handled) stored++;
                    if (!string.IsNullOrWhiteSpace(incoming.MessageId)) ids.Add(incoming.MessageId!);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _log.LogWarning(ex, "Importación: un mensaje de {Chat} en {Instance} falló", chat.RemoteJid, instanceName);
                }
            }

            if (muteHistoricLeads && ids.Count > 0)
            {
                try { await MuteHistoricLeadsAsync(chatScope.ServiceProvider.GetRequiredService<ApplicationDbContext>(), instanceName, ids, ct); }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _log.LogWarning(ex, "Importación: no pude apagar el bot de los leads de {Chat}", chat.RemoteJid);
                }
            }
        }

        if (stored > 0 || truncated > 0)
            _log.LogInformation("Importación {Instance}: {Chats} chats, {Stored} mensajes procesados{Trunc}",
                instanceName, eligible.Count, stored,
                truncated > 0 ? $", {truncated} chats cortados en {PageSize * maxPagesPerChat} msgs" : "");
        return new Result(eligible.Count, stored, truncated);
    }

    private static async Task MuteHistoricLeadsAsync(ApplicationDbContext db, string instanceName, List<string> messageIds, CancellationToken ct)
    {
        var leadIds = await db.ConversationMessages.AsNoTracking()
            .Where(m => m.WhatsappMessageId != null && messageIds.Contains(m.WhatsappMessageId))
            .Select(m => m.LeadId)
            .Distinct()
            .ToListAsync(ct);
        if (leadIds.Count == 0) return;

        var historic = DateTimeOffset.UtcNow - HistoryAge;
        var leads = await db.Leads.Where(l => leadIds.Contains(l.Id) && l.BotMutedAt == null).ToListAsync(ct);
        foreach (var lead in leads)
        {
            var newest = await db.ConversationMessages.AsNoTracking()
                .Where(m => m.LeadId == lead.Id)
                .OrderByDescending(m => m.Timestamp)
                .Select(m => new { m.EvolutionInstance, m.Timestamp })
                .FirstOrDefaultAsync(ct);
            // Si lo último del lead pasó en otra línea (o es reciente), la charla está viva
            // en otro lado: no la tocamos.
            if (newest is null || newest.EvolutionInstance != instanceName || newest.Timestamp >= historic) continue;
            lead.BotMutedAt = DateTimeOffset.UtcNow;
            lead.UpdatedAt = DateTimeOffset.UtcNow;
        }
        await db.SaveChangesAsync(ct);
    }

    private static DateTimeOffset TsOf(JsonElement record) =>
        record.TryGetProperty("messageTimestamp", out var ts) && ts.ValueKind == JsonValueKind.Number
            ? DateTimeOffset.FromUnixTimeSeconds(ts.GetInt64())
            : DateTimeOffset.MinValue;
}
