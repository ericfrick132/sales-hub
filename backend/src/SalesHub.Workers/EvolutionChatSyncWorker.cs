using Microsoft.EntityFrameworkCore;
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

    // Techo por chat y por tick (ChatHistoryImporter.PageSize × MaxPages mensajes). Un chat con
    // más backlog que esto en la ventana queda parcialmente sincronizado — se loguea.
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
        using var importScope = _scopes.CreateScope();
        var importer = importScope.ServiceProvider.GetRequiredService<ChatHistoryImporter>();

        foreach (var name in instances)
        {
            try { await importer.ImportAsync(name, cutoff, MaxPagesPerChat, muteHistoricLeads: false, ct); }
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
}
