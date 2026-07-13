using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SalesHub.Core.Domain.Entities;
using SalesHub.Infrastructure.Instagram;
using SalesHub.Infrastructure.Persistence;

namespace SalesHub.Workers;

/// <summary>
/// Worker que analiza la estructura HTML de Instagram usando Playwright,
/// envía el HTML limpio a un LLM (OpenAI/DeepSeek) y guarda los selectores
/// actualizados en la DB.
///
/// Se ejecuta:
/// 1. Una vez por día (programado)
/// 2. A demanda cuando un selector falla N veces seguidas (auto-reparación)
/// 3. Manualmente via API
///
/// Esto permite que el scraper se adapte automáticamente a cambios
/// en el DOM de Instagram sin necesidad de modificar código.
/// </summary>
public class InstagramStructureAnalyzerWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly IOptions<InstagramOptions> _opts;
    private readonly IOptions<InstagramLlmOptions> _llmOpts;
    private readonly SelectorFailureTracker _failureTracker;
    private readonly ILogger<InstagramStructureAnalyzerWorker> _log;

    // Semáforo para evitar ejecuciones concurrentes
    private readonly SemaphoreSlim _runningLock = new(1, 1);

    public InstagramStructureAnalyzerWorker(
        IServiceScopeFactory scopes,
        IOptions<InstagramOptions> opts,
        IOptions<InstagramLlmOptions> llmOpts,
        SelectorFailureTracker failureTracker,
        ILogger<InstagramStructureAnalyzerWorker> log)
    {
        _scopes = scopes;
        _opts = opts;
        _llmOpts = llmOpts;
        _failureTracker = failureTracker;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromHours(_llmOpts.Value.AnalysisIntervalHours);

        _log.LogInformation("InstagramStructureAnalyzerWorker started (interval: {Interval}h, auto-heal: {MaxFailures} failures)",
            interval.TotalHours, _opts.Value.MaxSelectorFailuresBeforeReanalysis);

        // Suscribirse a fallos de selectores para auto-reparación
        _failureTracker.OnMaxFailuresReached += OnSelectorFailuresReached;

        // Esperar 5 minutos al inicio para dar tiempo a que el sistema arranque
        await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Verificar si ya se ejecutó hoy
                var alreadyRan = await CheckIfAlreadyRanTodayAsync(stoppingToken);
                if (alreadyRan)
                {
                    _log.LogInformation("Análisis de estructura ya ejecutado hoy, esperando próximo ciclo");
                }
                else
                {
                    await RunAnalysisAsync(stoppingToken);
                }
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "InstagramStructureAnalyzerWorker run failed");
            }

            await Task.Delay(interval, stoppingToken);
        }
    }

    /// <summary>
    /// Handler para auto-reparación: cuando un selector falla N veces,
    /// se gatilla un re-análisis inmediato de la estructura.
    /// </summary>
    private async Task OnSelectorFailuresReached(string pageKey, string selectorKey)
    {
        _log.LogWarning(
            "Auto-healing: selector {PageKey}.{SelectorKey} falló. Gatillando re-análisis de estructura...",
            pageKey, selectorKey);

        await RunAnalysisAsync(CancellationToken.None);
    }

    /// <summary>
    /// Ejecuta el análisis de estructura a demanda (puede ser llamado desde API).
    /// Devuelve true si se ejecutó, false si ya había uno en ejecución.
    /// </summary>
    public async Task<bool> RunAnalysisOnDemandAsync(CancellationToken ct = default)
    {
        // No tomamos el lock acá: RunAnalysisAsync ya lo maneja. Tomarlo dos veces hacía
        // que el análisis a demanda no ejecutara nada (WaitAsync(0) fallaba adentro).
        await RunAnalysisAsync(ct);
        return true;
    }

    private async Task<bool> CheckIfAlreadyRanTodayAsync(CancellationToken ct)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        return await db.InstagramStructureSnapshots
            .AnyAsync(s => s.SnapshotDate == today && s.IsValid, ct);
    }

    private async Task RunAnalysisAsync(CancellationToken ct)
    {
        if (!await _runningLock.WaitAsync(0, ct))
        {
            _log.LogWarning("Ya hay un análisis de estructura en ejecución, saltando");
            return;
        }

        try
        {
            using var scope = _scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var crypto = scope.ServiceProvider.GetRequiredService<InstagramEncryptionService>();
            var analyzer = scope.ServiceProvider.GetRequiredService<InstagramStructureAnalyzer>();

            _log.LogInformation("Iniciando análisis de estructura de Instagram...");

            // Buscar una cuenta de Instagram disponible
            var account = await db.InstagramAccounts
                .Where(a => a.IsActive && !a.IsActionBlocked)
                .FirstOrDefaultAsync(ct);

            if (account is null)
            {
                _log.LogWarning("No hay cuentas de Instagram disponibles para el análisis de estructura");
                await SaveFailedSnapshotAsync(db, "No hay cuentas de Instagram disponibles", ct);
                return;
            }

            try
            {
                // Inicializar cliente de Instagram
                await using var client = new InstagramClient(db, crypto, _opts.Value, _log);
                await client.InitializeAsync(account, ct);

                // Verificar sesión
                var sessionOk = await client.CheckSessionAsync(ct);
                if (!sessionOk)
                {
                    _log.LogWarning("Sesión de Instagram expirada para {User}, intentando reloguear...", account.Username);
                    account.SessionCookiesJson = null;
                    await db.SaveChangesAsync(ct);
                    await SaveFailedSnapshotAsync(db, $"Sesión expirada para {account.Username}", ct);
                    return;
                }

                // Obtener la página de Playwright desde el cliente
                var page = client.Page;
                if (page is null)
                {
                    _log.LogWarning("No se pudo obtener la página de Playwright del cliente");
                    await SaveFailedSnapshotAsync(db, "No se pudo obtener la página de Playwright", ct);
                    return;
                }

                // Ejecutar el análisis de estructura (el LLM ahora es Claude, cableado
                // adentro del analyzer vía ClaudeClient — ya no se pasan endpoint/key acá).
                var selectors = await analyzer.AnalyzeStructureAsync(
                    page,
                    isLoggedIn: true,
                    ct);

                // Guardar el snapshot en la DB
                var snapshot = new InstagramStructureSnapshot
                {
                    Id = Guid.NewGuid(),
                    SnapshotDate = DateOnly.FromDateTime(DateTime.UtcNow),
                    SelectorsJson = JsonSerializer.Serialize(selectors, new JsonSerializerOptions { WriteIndented = true }),
                    RawHtmlJson = "{}",
                    LlmModel = "claude",
                    IsValid = true,
                    InstagramAccountId = account.Id,
                    CreatedAt = DateTimeOffset.UtcNow
                };

                db.InstagramStructureSnapshots.Add(snapshot);
                await db.SaveChangesAsync(ct);

                // Resetear contadores de fallos ya que tenemos selectores nuevos
                _failureTracker.ResetAll();

                _log.LogInformation("Análisis de estructura completado y guardado para {Date}",
                    snapshot.SnapshotDate);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Error durante el análisis de estructura de Instagram");
                await SaveFailedSnapshotAsync(db, ex.Message, ct);
            }
        }
        finally
        {
            _runningLock.Release();
        }
    }

    private static async Task SaveFailedSnapshotAsync(
        ApplicationDbContext db,
        string errorMessage,
        CancellationToken ct)
    {
        var snapshot = new InstagramStructureSnapshot
        {
            Id = Guid.NewGuid(),
            SnapshotDate = DateOnly.FromDateTime(DateTime.UtcNow),
            SelectorsJson = "{}",
            RawHtmlJson = "{}",
            LlmModel = string.Empty,
            IsValid = false,
            ErrorMessage = errorMessage,
            CreatedAt = DateTimeOffset.UtcNow
        };

        db.InstagramStructureSnapshots.Add(snapshot);
        await db.SaveChangesAsync(ct);
    }
}
