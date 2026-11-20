using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SalesHub.Core.Abstractions;
using SalesHub.Core.Domain.Entities;
using SalesHub.Core.Domain.Enums;
using SalesHub.Infrastructure.Persistence;

namespace SalesHub.Infrastructure.Services;

/// <summary>
/// Cada minuto: estado en vivo de cada línea de WhatsApp + VIGILANTE de reconexión.
///
/// Por qué existe el vigilante (19/11/2026): desde el reinicio de Evolution del 17/11 las sesiones
/// se cortan cada pocas horas y Evolution no las vuelve a levantar solo. La línea de GymHero estuvo
/// 26 h y después 16 h sin recibir nada: WhatsApp retiene lo entrante y lo entrega de golpe al
/// reconectar, pero el bot contesta horas tarde. Abrir el QR en /devices la "arreglaba" porque eso
/// llama a <c>instance/connect</c>, que con la sesión guardada reconecta sin escanear. El vigilante
/// hace exactamente eso solo, y avisa al maestro si no alcanza.
///
/// Es conservador a propósito: reconectar NO manda mensajes, pero una línea vieja revivida sí
/// podría volver a mandar (cadencias, bot). Por eso solo toca líneas vistas conectadas en vivo
/// hace menos de <see cref="ReconnectWindow"/>, y deja de insistir si WhatsApp la desvinculó (401/403).
/// </summary>
public class InstanceMonitor
{
    /// <summary>Hasta cuánto después de la última vez que la vimos viva se reintenta una línea.</summary>
    private static readonly TimeSpan ReconnectWindow = TimeSpan.FromHours(72);
    /// <summary>Espacio entre reintentos de la misma línea.</summary>
    private static readonly TimeSpan ReconnectEvery = TimeSpan.FromMinutes(3);
    /// <summary>Caída más larga que esto sin poder reconectar → aviso al maestro.</summary>
    private static readonly TimeSpan AlertAfter = TimeSpan.FromMinutes(15);

    private readonly ApplicationDbContext _db;
    private readonly IEvolutionClient _evo;
    private readonly IAdminAlerter _alerter;
    private readonly ILogger<InstanceMonitor> _log;

    public InstanceMonitor(ApplicationDbContext db, IEvolutionClient evo, IAdminAlerter alerter, ILogger<InstanceMonitor> log)
    {
        _db = db; _evo = evo; _alerter = alerter; _log = log;
    }

    /// <summary>
    /// Líneas que viven SOLO en Evolution, sin fila en evolution_instances (adrede: no aparecen en /devices
    /// ni las recorre ningún worker). Hoy: el WhatsApp personal de Eric (<see cref="ClaudeTaskService.LineName"/>),
    /// que le transcribe los audios que se reenvía y le pasa tareas a Claude. Se vigilan igual que las otras
    /// (20/11/2026: estuvo caída 3 días sin que nadie avisara). Su estado vive en memoria (no hay fila donde
    /// guardarlo): tras reiniciar la API se vuelve a vigilar desde la primera vez que se la vea conectada.
    /// </summary>
    private static readonly EvolutionInstance[] PrivateLines =
    {
        new() { InstanceName = ClaudeTaskService.LineName, Label = "WhatsApp personal de Eric (transcripcion / Claude)" },
    };

    public async Task TickAsync(CancellationToken ct)
    {
        var instances = await _db.EvolutionInstances.ToListAsync(ct);
        var alerts = new List<string>();
        foreach (var inst in instances)
            await CheckAsync(inst, alerts, ct);
        // Objetos sueltos, sin trackear: el SaveChanges de abajo no los toca.
        foreach (var inst in PrivateLines)
            await CheckAsync(inst, alerts, ct);

        // connectionState no trae el número: si un teléfono se vinculó por fuera del modal del
        // QR (o se re-escaneó con otro celu), lo completamos desde fetchInstances.
        var missingPhone = instances.Where(i => i.Status == InstanceStatus.Connected && i.ConnectedPhoneNumber is null).ToList();
        if (missingPhone.Count > 0)
        {
            var owners = await _evo.GetInstanceOwnersAsync(ct);
            foreach (var inst in missingPhone)
                if (owners.TryGetValue(inst.InstanceName, out var owner) && !string.IsNullOrWhiteSpace(owner))
                    inst.ConnectedPhoneNumber = owner;
        }
        await _db.SaveChangesAsync(ct);

        // Después de guardar: si el aviso falla (o tarda), el estado ya quedó registrado.
        foreach (var alert in alerts)
            await _alerter.AlertAsync(alert, ct);
    }

    /// <summary>Estado en vivo de una línea; si no está open, la pasa al vigilante.</summary>
    private async Task CheckAsync(EvolutionInstance inst, List<string> alerts, CancellationToken ct)
    {
            try
            {
                var info = await _evo.GetInstanceStatusAsync(inst.InstanceName, ct);
                var now = DateTimeOffset.UtcNow;
                var status = info.Status switch
                {
                    "open" or "connected" => InstanceStatus.Connected,
                    "connecting" or "qr" => InstanceStatus.Connecting,
                    "close" or "disconnected" or "not_found" => InstanceStatus.Disconnected,
                    _ => InstanceStatus.Unknown
                };
                if (status != inst.Status)
                {
                    inst.Status = status;
                    inst.UpdatedAt = now;
                    if (status == InstanceStatus.Connected && inst.ConnectedAt is null)
                        inst.ConnectedAt = now;
                    if (status == InstanceStatus.Disconnected)
                        inst.DisconnectedAt = now;
                }
                inst.LastStatusCheckAt = now;

                if (status == InstanceStatus.Connected)
                {
                    if (inst.DownAlertedAt is not null && inst.LastSeenOpenAt is { } downSince)
                        alerts.Add($"Linea {Name(inst)} volvio a conectarse (estuvo caida {Ago(now - downSince)}).");
                    inst.LastSeenOpenAt = now;
                    inst.NeedsQrSince = null;
                    inst.DownAlertedAt = null;
                }
                else if (info.Status != "not_found")
                {
                    await WatchAsync(inst, info.Status, now, alerts, ct);
                }
            }
            catch (Exception ex)
            {
                _log.LogDebug(ex, "Instance {N} status check failed", inst.InstanceName);
            }
    }

    /// <summary>Línea que no está open: reintentar la reconexión y/o avisar, si corresponde.</summary>
    private async Task WatchAsync(EvolutionInstance inst, string liveState, DateTimeOffset now, List<string> alerts, CancellationToken ct)
    {
        // Solo líneas que estaban vivas hace poco. Las viejas/abandonadas no se tocan nunca.
        if (inst.LastSeenOpenAt is not { } lastOpen || now - lastOpen > ReconnectWindow) return;
        if (inst.NeedsQrSince is not null) return;

        // "connecting" = Evolution ya está intentando (o hay un QR abierto en /devices): no pisarlo.
        if (liveState == "close"
            && (inst.LastReconnectAttemptAt is null || now - inst.LastReconnectAttemptAt >= ReconnectEvery))
        {
            inst.LastReconnectAttemptAt = now;
            _log.LogWarning("Vigilante: la línea {Line} está caída (vista viva hace {Ago}) → instance/connect",
                inst.InstanceName, Ago(now - lastOpen));
            // OJO: lo que devuelve instance/connect NO dice si hace falta QR. Evolution guarda en memoria
            // el último QR que generó y nunca lo limpia al conectar, así que devuelve ese base64 viejo
            // aunque la sesión esté sana y reconecte sola (falsa alarma del 19/11 13:11).
            await _evo.GetQrCodeAsync(inst.InstanceName, ct);

            // Desvinculada de verdad = Evolution registró un 401/403 después de la última vez que la
            // vimos viva. Ahí reintentar no sirve: hay que escanear.
            var cut = await _evo.GetLastDisconnectionAsync(inst.InstanceName, ct);
            if (cut is { Code: 401 or 403 } d && d.At >= lastOpen.AddMinutes(-2))
            {
                inst.NeedsQrSince = now;
                inst.DownAlertedAt = now;
                _log.LogWarning("Vigilante: la línea {Line} fue desvinculada por WhatsApp ({Code}) → pide QR", inst.InstanceName, d.Code);
                alerts.Add($"Linea {Name(inst)} se desvinculo de WhatsApp: hay que escanear el QR de nuevo {QrWhere(inst)}. Hasta entonces no entran mensajes por esa linea.");
                return;
            }
        }

        if (inst.DownAlertedAt is null && now - lastOpen >= AlertAfter)
        {
            inst.DownAlertedAt = now;
            alerts.Add($"Linea {Name(inst)} esta caida hace {Ago(now - lastOpen)} y no se pudo reconectar sola: no estan entrando mensajes. Probar abriendo su QR {QrWhere(inst)}.");
        }
    }

    /// <summary>Dónde se escanea el QR: las líneas privadas no están en /devices.</summary>
    private static string QrWhere(EvolutionInstance i) =>
        PrivateLines.Contains(i)
            ? $"(no esta en /devices: pedile a Claude el QR de la instancia {i.InstanceName} de Evolution)"
            : "en sales.efcloud.tech/devices";

    private static string Name(EvolutionInstance i)
    {
        var label = string.IsNullOrWhiteSpace(i.Label) ? i.InstanceName : i.Label.Trim();
        return string.IsNullOrWhiteSpace(i.ConnectedPhoneNumber) ? label : $"{label} (+{i.ConnectedPhoneNumber})";
    }

    private static string Ago(TimeSpan t) =>
        t.TotalMinutes < 120 ? $"{Math.Max(1, (int)t.TotalMinutes)} min" : $"{(int)t.TotalHours} h";
}
