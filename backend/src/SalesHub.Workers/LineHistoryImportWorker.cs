using Microsoft.EntityFrameworkCore;
using SalesHub.Infrastructure.Persistence;
using SalesHub.Infrastructure.Services;

namespace SalesHub.Workers;

/// <summary>
/// Carga el historial de un teléfono recién escaneado (los chats de ANTES de vincularlo) cuando
/// quien lo agregó tildó "cargar para atrás". WhatsApp le manda ese historial al dispositivo
/// vinculado en tandas durante los minutos siguientes al escaneo y Evolution lo guarda; por eso
/// hay dos pasadas: a los 3 min de conectar y a los 30 min (la segunda levanta lo que llegó
/// tarde; el ingest deduplica, así que repasar no duplica nada).
/// No depende del flag 'chat-sync': es un pedido explícito para ese teléfono.
/// </summary>
public class LineHistoryImportWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<LineHistoryImportWorker> _log;

    // Tres pasadas: WhatsApp le va pasando la lista de chats al dispositivo recién vinculado
    // durante un buen rato, así que la última (2 h) es la que asegura que no quede ningún
    // número afuera. El ingest deduplica, así que repasar no duplica nada.
    private static readonly TimeSpan[] PassDelays =
        { TimeSpan.FromMinutes(3), TimeSpan.FromMinutes(30), TimeSpan.FromHours(2) };
    // 5.000 mensajes por chat: alcanza para encontrar el primer mensaje de casi cualquier
    // contacto sin que un chat gigante trabe la carga del resto.
    private const int MaxPagesPerChat = 100;
    // Teléfono con prospectos: importan TODOS los números, no la charla entera de cada uno.
    // Con los últimos 10 mensajes el que llama entra al chat sabiendo de qué venía hablando, y
    // un celu de miles de chats se recorre en minutos en vez de horas.
    private const int ProspectMessagesPerChat = 10;

    public LineHistoryImportWorker(IServiceScopeFactory scopes, ILogger<LineHistoryImportWorker> log)
    {
        _scopes = scopes; _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        _log.LogInformation("LineHistoryImportWorker started");
        await Task.Delay(TimeSpan.FromMinutes(1), ct);
        while (!ct.IsCancellationRequested)
        {
            try { await TickAsync(ct); }
            catch (Exception ex) when (ex is not OperationCanceledException) { _log.LogError(ex, "LineHistoryImportWorker tick failed"); }
            await Task.Delay(TimeSpan.FromMinutes(1), ct);
        }
    }

    private async Task TickAsync(CancellationToken ct)
    {
        List<(Guid Id, string Name, int Pass, DateTimeOffset? ConnectedAt, ChatHistoryImporter.ProspectRules? Prospects)> due;
        using (var scope = _scopes.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var now = DateTimeOffset.UtcNow;
            // Sin exigir Status == Connected: la sesión de WhatsApp parpadea (caso real:
            // conectada/desconectada/conectando en ticks seguidos del monitor), así que pedir
            // "conectada justo en este segundo" hacía que las pasadas no salieran nunca. Lo que
            // manda es que el teléfono se haya vinculado (ConnectedAt): si la sesión está caída,
            // Evolution devuelve lo que tenga guardado y la pasada siguiente completa.
            var candidates = await db.EvolutionInstances.AsNoTracking()
                .Where(i => i.ImportHistory
                    && i.ConnectedAt != null && i.HistoryImportPasses < PassDelays.Length)
                .Select(i => new { i.Id, i.InstanceName, i.HistoryImportPasses, i.ConnectedAt,
                    i.ProspectSellerIds, i.ProspectSkipWords, i.ConnectedPhoneNumber })
                .ToListAsync(ct);
            due = candidates
                .Where(c => now >= c.ConnectedAt!.Value + PassDelays[c.HistoryImportPasses])
                .Select(c => (c.Id, c.InstanceName, c.HistoryImportPasses, c.ConnectedAt,
                    c.ProspectSellerIds.Count > 0
                        ? new ChatHistoryImporter.ProspectRules(c.ProspectSellerIds, c.ProspectSkipWords, c.ConnectedPhoneNumber)
                        : null))
                .ToList();
        }

        // De a un teléfono por vez: una importación grande ya le da trabajo de sobra a la DB.
        foreach (var (id, name, pass, connectedAt, prospects) in due)
        {
            var startedAt = DateTimeOffset.UtcNow;
            if (!await MarkAsync(id, i =>
            {
                i.HistoryImportStartedAt = startedAt;
                i.HistoryImportTotalChats = 0;
                i.HistoryImportDoneChats = 0;
            }, ct)) continue;
            _log.LogInformation("Cargando historial de {Instance} (pasada {Pass})", name, pass + 1);

            using var importScope = _scopes.CreateScope();
            var importer = importScope.ServiceProvider.GetRequiredService<ChatHistoryImporter>();
            ChatHistoryImporter.Result result;
            try
            {
                result = await importer.ImportAsync(name, cutoff: null, MaxPagesPerChat, muteHistoricLeads: true, ct,
                    prospects,
                    messagesPerChat: prospects is null ? null : ProspectMessagesPerChat,
                    onProgress: (chatsDone, total, c) => MarkAsync(id, i =>
                    {
                        i.HistoryImportTotalChats = total;
                        i.HistoryImportDoneChats = chatsDone;
                    }, c));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Sin marcar la pasada: se reintenta en el próximo tick.
                _log.LogWarning(ex, "La carga del historial de {Instance} falló", name);
                continue;
            }

            await MarkAsync(id, i =>
            {
                // Si mientras tanto lo desvincularon (o re-escanearon), la pasada ya no corresponde
                // a esta vinculación: que la nueva arranque de cero.
                if (i.ConnectedAt != connectedAt || i.HistoryImportPasses != pass) return;
                // Si la primera pasada ya arrancó pasada la media hora (ej. se prendió la opción en un
                // teléfono vinculado hace días), el historial ya había llegado entero: no hace falta otra.
                i.HistoryImportPasses = pass == 0 && startedAt - connectedAt >= PassDelays[^1]
                    ? PassDelays.Length
                    : pass + 1;
                i.HistoryImportedAt = DateTimeOffset.UtcNow;
                i.HistoryImportedMessages += result.Stored;
                i.HistoryImportProspects += result.Prospects;
                i.HistoryImportDoneChats = result.Chats;
                i.HistoryImportTotalChats = result.Chats;
            }, ct);
            _log.LogInformation("Historial de {Instance}: pasada {Pass} lista ({Chats} chats, {Stored} mensajes nuevos, {Prospects} prospectos)",
                name, pass + 1, result.Chats, result.Stored, result.Prospects);
        }
    }

    /// <summary>Escribe un cambio puntual en la fila del teléfono (avance, contadores, estado).</summary>
    private async Task<bool> MarkAsync(Guid id, Action<SalesHub.Core.Domain.Entities.EvolutionInstance> change, CancellationToken ct)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var line = await db.EvolutionInstances.FirstOrDefaultAsync(i => i.Id == id, ct);
        if (line is null) return false; // lo borraron
        change(line);
        await db.SaveChangesAsync(ct);
        return true;
    }
}
