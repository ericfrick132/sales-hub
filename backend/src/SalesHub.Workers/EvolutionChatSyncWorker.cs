using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SalesHub.Core.Abstractions;
using SalesHub.Infrastructure.Evolution;
using SalesHub.Infrastructure.Persistence;
using SalesHub.Infrastructure.Services;

namespace SalesHub.Workers;

/// <summary>
/// Red de seguridad para que Conversaciones tenga ABSOLUTAMENTE todos los chats de cada
/// instancia vinculada: cada tick lee los chats con actividad reciente directo de Evolution
/// (POST /chat/findChats + /chat/findMessages) y pasa cada mensaje por el mismo ingest del
/// webhook (con FromSync=true: solo registra — no mutea el bot ni re-procesa comandos).
/// Cubre los baches del webhook: deploys del backend (mensajes en vuelo se pierden),
/// webhooks deshabilitados, y todo lo que se descartó históricamente. El dedup por
/// WhatsappMessageId + la ventana de eco hacen el replay idempotente.
///
/// El primer tick mira Sync:ChatSyncBootstrapDays hacia atrás (default 30) para backfillear
/// lo perdido; después solo una ventana corta (2 ticks + margen). El bootstrap se marca en
/// RuntimeFlags ('chat-sync-bootstrapped') para no re-barrer 30 días en cada deploy —
/// apagar esa fila desde la UI fuerza un re-backfill completo en el próximo tick.
/// Gate por flag DB 'chat-sync' (default ON).
/// </summary>
public class EvolutionChatSyncWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly IConfiguration _config;
    private readonly ILogger<EvolutionChatSyncWorker> _log;

    private const string BootstrapFlag = "chat-sync-bootstrapped";

    private const int PageSize = 50;
    // Techo por chat y por tick (PageSize × MaxPages mensajes). Un chat con más backlog
    // que esto en la ventana queda parcialmente sincronizado — se loguea.
    private const int MaxPagesPerChat = 12;

    public EvolutionChatSyncWorker(IServiceScopeFactory scopes, IConfiguration config, ILogger<EvolutionChatSyncWorker> log)
    {
        _scopes = scopes; _config = config; _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _log.LogInformation("EvolutionChatSyncWorker started (gate por flag 'chat-sync'; bootstrap {Days} días)",
            _config.GetValue<int>("Sync:ChatSyncBootstrapDays", 30));
        await Task.Delay(TimeSpan.FromMinutes(3), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try { await TickAsync(stoppingToken); }
            catch (Exception ex) { _log.LogError(ex, "Chat sync tick failed"); }
            await Task.Delay(TimeSpan.FromMinutes(_config.GetValue<int>("Sync:ChatSyncMinutes", 10)), stoppingToken);
        }
    }

    private async Task TickAsync(CancellationToken ct)
    {
        List<string> instances;
        bool bootstrapDone;
        using (var gate = _scopes.CreateScope())
        {
            var db = gate.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            if (!await db.IsFlagOnAsync("chat-sync", _config.GetValue<bool>("Workers:ChatSyncAutoStart", true), ct))
                return;
            bootstrapDone = await db.IsFlagOnAsync(BootstrapFlag, false, ct);
            instances = await db.EvolutionInstances.AsNoTracking()
                .Select(i => i.InstanceName)
                .ToListAsync(ct);
        }

        var intervalMin = _config.GetValue<int>("Sync:ChatSyncMinutes", 10);
        var lookback = bootstrapDone
            ? TimeSpan.FromMinutes(intervalMin * 2 + 5)
            : TimeSpan.FromDays(_config.GetValue<int>("Sync:ChatSyncBootstrapDays", 30));
        var cutoff = DateTimeOffset.UtcNow - lookback;

        foreach (var name in instances)
        {
            try { await SyncInstanceAsync(name, cutoff, ct); }
            catch (Exception ex) { _log.LogWarning(ex, "Chat sync de {Instance} falló", name); }
        }

        if (!bootstrapDone)
        {
            using var scope = _scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var flag = await db.RuntimeFlags.FirstOrDefaultAsync(f => f.Key == BootstrapFlag, ct);
            if (flag is null)
                db.RuntimeFlags.Add(new SalesHub.Core.Domain.Entities.RuntimeFlag
                    { Key = BootstrapFlag, Enabled = true, UpdatedAt = DateTimeOffset.UtcNow });
            else { flag.Enabled = true; flag.UpdatedAt = DateTimeOffset.UtcNow; }
            await db.SaveChangesAsync(ct);
            _log.LogInformation("Chat sync: bootstrap de backfill completado — próximos ticks con ventana corta");
        }
    }

    private async Task SyncInstanceAsync(string instanceName, DateTimeOffset cutoff, CancellationToken ct)
    {
        using var scope = _scopes.CreateScope();
        var evo = scope.ServiceProvider.GetRequiredService<IEvolutionClient>();

        var chats = await evo.FindChatsAsync(instanceName, ct);
        // Solo chats 1:1 (persona o LID). Afuera grupos, canales y status.
        var eligible = chats
            .Where(c => (c.RemoteJid.EndsWith("@s.whatsapp.net", StringComparison.Ordinal)
                      || c.RemoteJid.EndsWith("@lid", StringComparison.Ordinal))
                     && c.UpdatedAt is not null && c.UpdatedAt >= cutoff)
            .ToList();
        if (eligible.Count == 0) return;

        var stored = 0;
        var truncated = 0;
        foreach (var chat in eligible)
        {
            // Scope por CHAT: en el bootstrap (30 días) el change-tracking de un solo
            // DbContext acumularía miles de entidades — el droplet tiene 1GB.
            using var chatScope = _scopes.CreateScope();
            var conv = chatScope.ServiceProvider.GetRequiredService<ConversationService>();

            var records = new List<JsonElement>();
            for (var page = 1; page <= MaxPagesPerChat; page++)
            {
                var res = await evo.FindMessagesAsync(instanceName, chat.RemoteJid, page, PageSize, ct);
                if (res.Records.Count == 0) break;
                records.AddRange(res.Records);
                // Vienen del más nuevo al más viejo: si ya pasamos el cutoff, listo.
                if (res.Records.Min(TsOf) < cutoff) break;
                if (res.Pages > 0 && page >= res.Pages) break;
                if (page == MaxPagesPerChat) truncated++;
            }

            foreach (var rec in records.Where(r => TsOf(r) >= cutoff).OrderBy(TsOf))
            {
                // Parse adentro del try: un record podrido (message null, key rara) no debe
                // matar el sync de toda la instancia.
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
                }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "Chat sync: un mensaje de {Chat} en {Instance} falló", chat.RemoteJid, instanceName);
                }
            }
        }

        if (stored > 0 || truncated > 0)
            _log.LogInformation("Chat sync {Instance}: {Chats} chats con actividad, {Stored} mensajes procesados{Trunc}",
                instanceName, eligible.Count, stored, truncated > 0 ? $", {truncated} chats truncados a {PageSize * MaxPagesPerChat} msgs" : "");
    }

    private static DateTimeOffset TsOf(JsonElement record) =>
        record.TryGetProperty("messageTimestamp", out var ts) && ts.ValueKind == JsonValueKind.Number
            ? DateTimeOffset.FromUnixTimeSeconds(ts.GetInt64())
            : DateTimeOffset.MinValue;
}
