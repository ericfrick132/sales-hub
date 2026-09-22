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

    public record Result(int Chats, int Stored, int Truncated, int SkippedChats = 0, int Prospects = 0);

    /// <summary>Cada cuántos chats se avisa el avance (para la barra de progreso).</summary>
    private const int ProgressEvery = 20;

    /// <summary>
    /// Prospectos del teléfono: a quiénes se les reparten los contactos del historial y qué
    /// chats no entran. Null = carga sin prospectos (el chat entra a Conversaciones y nada más).
    /// </summary>
    /// <param name="OwnerIds">Vendedores que se reparten los contactos (round-robin por número).</param>
    /// <param name="SkipWords">Si el nombre del contacto o alguno de sus mensajes tiene una de
    /// estas palabras, el chat entero queda afuera: ni lead ni mensajes.</param>
    /// <param name="OwnPhone">Número del propio teléfono, para no crear un prospecto de uno mismo.</param>
    public record ProspectRules(IReadOnlyList<Guid> OwnerIds, IReadOnlyList<string> SkipWords, string? OwnPhone);

    /// <param name="cutoff">Solo mensajes desde esta fecha; null = todo lo que tenga Evolution.</param>
    /// <param name="maxPagesPerChat">Techo por chat (PageSize × esto); lo que pase queda sin importar y se loguea.</param>
    /// <param name="muteHistoricLeads">
    /// Carga de historial: a un lead que YA existía y cuya última actividad pasa a ser un mensaje
    /// viejo de esta línea se le apaga el bot, para que el re-enganche no le escriba por una
    /// charla de hace meses. Los leads que crea la importación ya nacen muteados.
    /// </param>
    /// <param name="prospects">
    /// Si el teléfono tiene prospectos prendidos, cada chat 1:1 se convierte en un lead de
    /// remarketing asignado a uno de esos vendedores, listo para llamar desde el CRM.
    /// </param>
    /// <param name="messagesPerChat">
    /// Traer sólo los N mensajes más nuevos de cada chat, en un solo pedido. Es lo que hace
    /// rápida la carga de prospectos: lo que importa es tener TODOS los números con algo de
    /// contexto, no la charla completa de cada uno. Null = paginar hasta maxPagesPerChat.
    /// </param>
    /// <param name="onProgress">Avance (chats hechos, total) para la barra de progreso.</param>
    public async Task<Result> ImportAsync(string instanceName, DateTimeOffset? cutoff, int maxPagesPerChat,
        bool muteHistoricLeads, CancellationToken ct, ProspectRules? prospects = null,
        int? messagesPerChat = null, Func<int, int, CancellationToken, Task>? onProgress = null)
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

        // La agenda se usa SOLO para ponerle el nombre que tiene guardado el celu a un chat que
        // ya entra por sus mensajes. Un contacto NO es un prospecto: prospecto es con quien
        // hubo conversación.
        var namesByPhone = new Dictionary<string, string>();
        if (prospects is not null)
        {
            using var scope = _scopes.CreateScope();
            try
            {
                foreach (var c in await scope.ServiceProvider.GetRequiredService<IEvolutionClient>()
                             .FindContactsAsync(instanceName, ct))
                {
                    var key = Suffix(c.RemoteJid);
                    if (key is not null && !string.IsNullOrWhiteSpace(c.Name)) namesByPhone[key] = c.Name!;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogWarning(ex, "Importación: no pude leer la agenda de {Instance}", instanceName);
            }
        }

        var stored = 0;
        var truncated = 0;
        var skippedChats = 0;
        var createdProspects = 0;
        var done = 0;
        var total = eligible.Count;
        var ownSuffix = Suffix(prospects?.OwnPhone);
        if (onProgress is not null) await onProgress(0, total, ct);

        foreach (var chat in eligible)
        {
            done++;
            if (onProgress is not null && done % ProgressEvery == 0) await onProgress(done, total, ct);
            // El chat con uno mismo (mensajes guardados, notas) no es un prospecto.
            if (ownSuffix is not null && Suffix(chat.RemoteJid) == ownSuffix) continue;
            ct.ThrowIfCancellationRequested();
            // Scope por CHAT: con miles de mensajes el change-tracking de un solo DbContext
            // acumularía todo en memoria — el droplet tiene 1GB.
            using var chatScope = _scopes.CreateScope();
            var evo = chatScope.ServiceProvider.GetRequiredService<IEvolutionClient>();
            var conv = chatScope.ServiceProvider.GetRequiredService<ConversationService>();

            var records = new List<JsonElement>();
            try
            {
                if (messagesPerChat is > 0)
                {
                    // Un solo pedido con los más nuevos: con miles de chats, paginar cada uno
                    // hasta el fondo tarda horas y no aporta (para llamar alcanza el contexto).
                    var res = await evo.FindMessagesAsync(instanceName, chat.RemoteJid, 1, messagesPerChat.Value, ct);
                    records.AddRange(res.Records);
                }
                else
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
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Sin los mensajes igual queremos el número: el prospecto se crea abajo.
                _log.LogWarning(ex, "Importación: no pude leer {Chat} de {Instance}", chat.RemoteJid, instanceName);
            }

            // Se parsea TODO el chat antes de guardar nada: el filtro de palabras mira la
            // conversación entera (basta con que una la nombre para que el chat no entre).
            var parsed = new List<ConversationService.IncomingMessage>();
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
                    parsed.Add(prospects is null
                        ? incoming
                        : incoming with { ProspectOwners = prospects.OwnerIds, ContactName = chat.Name });
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _log.LogWarning(ex, "Importación: un mensaje de {Chat} en {Instance} falló", chat.RemoteJid, instanceName);
                }
            }

            if (prospects is { SkipWords.Count: > 0 } && HasSkipWord(chat.Name, parsed, prospects.SkipWords))
            {
                skippedChats++;
                continue;
            }

            // Prospecto = con quien hubo CONVERSACIÓN. Un chat del que WhatsApp todavía no bajó
            // ningún mensaje no entra: se carga en una pasada siguiente, cuando tenga contenido.
            if (prospects is not null && records.Count > 0)
            {
                try
                {
                    var name = (Suffix(chat.RemoteJid) is { } key && namesByPhone.TryGetValue(key, out var agenda))
                        ? agenda
                        : chat.Name;
                    if (await conv.EnsureProspectAsync(instanceName, chat.RemoteJid, name,
                            prospects.OwnerIds, chat.UpdatedAt, ct))
                        createdProspects++;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _log.LogWarning(ex, "Importación: no pude dar de alta el prospecto de {Chat}", chat.RemoteJid);
                }
            }

            var ids = new List<string>();
            foreach (var incoming in parsed)
            {
                try
                {
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

        if (onProgress is not null) await onProgress(total, total, ct);
        if (stored > 0 || truncated > 0 || skippedChats > 0 || createdProspects > 0)
            _log.LogInformation("Importación {Instance}: {Chats} chats, {Stored} mensajes procesados{Prospects}{Skipped}{Trunc}",
                instanceName, eligible.Count, stored,
                createdProspects > 0 ? $", {createdProspects} prospectos nuevos" : "",
                skippedChats > 0 ? $", {skippedChats} chats afuera por el filtro de palabras" : "",
                truncated > 0 ? $", {truncated} chats cortados en {PageSize * maxPagesPerChat} msgs" : "");
        return new Result(total, stored, truncated, skippedChats, createdProspects);
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

    /// <summary>Últimos 8 dígitos (el número de abonado): la parte estable en todos los formatos.</summary>
    private static string? Suffix(string? phoneOrJid)
    {
        if (string.IsNullOrWhiteSpace(phoneOrJid)) return null;
        var digits = new string(phoneOrJid.TakeWhile(c => c != '@').Where(char.IsDigit).ToArray());
        return digits.Length >= 8 ? digits[^8..] : digits.Length > 0 ? digits : null;
    }

    /// <summary>
    /// ¿El chat tiene alguna de las palabras que dejan afuera? Mira el nombre del contacto y
    /// todos sus mensajes, sin tildes ni mayúsculas.
    /// </summary>
    private static bool HasSkipWord(string? chatName, IReadOnlyList<ConversationService.IncomingMessage> messages,
        IReadOnlyList<string> skipWords)
    {
        var words = skipWords.Select(Norm).Where(w => w.Length > 0).ToList();
        if (words.Count == 0) return false;
        var haystack = Norm(chatName + " " + string.Join(" ", messages.Select(m => m.Text)));
        return words.Any(w => haystack.Contains(w, StringComparison.Ordinal));
    }

    /// <summary>Minúsculas y sin tildes, para comparar como se escribe de verdad.</summary>
    private static string Norm(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        var d = s.Trim().ToLowerInvariant().Normalize(System.Text.NormalizationForm.FormD);
        return new string(d.Where(c => System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c)
            != System.Globalization.UnicodeCategory.NonSpacingMark).ToArray());
    }

    private static DateTimeOffset TsOf(JsonElement record) =>
        record.TryGetProperty("messageTimestamp", out var ts) && ts.ValueKind == JsonValueKind.Number
            ? DateTimeOffset.FromUnixTimeSeconds(ts.GetInt64())
            : DateTimeOffset.MinValue;
}
