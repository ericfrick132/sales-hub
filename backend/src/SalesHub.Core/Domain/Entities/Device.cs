using SalesHub.Core.Domain.Enums;

namespace SalesHub.Core.Domain.Entities;

/// <summary>
/// Dispositivo Android físico conectado a SalesHub vía WebSocket + Tailscale.
/// Cada device ejecuta los comandos ADB que recibe del servidor.
/// </summary>
public class Device
{
    public Guid Id { get; set; }

    /// <summary>Nombre descriptivo (ej. "Moto E14 - Martu")</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Seller dueño del device. null = device sin asignar.</summary>
    public Guid? SellerId { get; set; }
    public Seller? Seller { get; set; }

    /// <summary>IP de Tailscale del dispositivo (ej. "100.110.223.14")</summary>
    public string? TailscaleIp { get; set; }

    /// <summary>Puerto ADB en el dispositivo (default 5555)</summary>
    public int AdbPort { get; set; } = 5555;

    /// <summary>Estado de conexión del device</summary>
    public DeviceStatus Status { get; set; } = DeviceStatus.Offline;

    /// <summary>Token de pairing de un solo uso. null = ya pareado.</summary>
    public string? PairingToken { get; set; }

    /// <summary>Cuándo expira el token de pairing</summary>
    public DateTimeOffset? PairingTokenExpiresAt { get; set; }

    /// <summary>Último heartbeat recibido del device</summary>
    public DateTimeOffset? LastHeartbeatAt { get; set; }

    /// <summary>Versión del APK que reporta el celu en cada poll (ej. "1.21").</summary>
    public string? AppVersion { get; set; }

    /// <summary>Charlas nuevas por día que abre un celu si el admin no le puso otro número.</summary>
    public const int DefaultDailyNewChatCap = 10;

    /// <summary>
    /// Tope de charlas NUEVAS por día de este celu (lo que sigue de una charla ya abierta no
    /// cuenta). null = <see cref="DefaultDailyNewChatCap"/>. Lo edita el admin en /devices.
    /// </summary>
    public int? DailyNewChatCap { get; set; }

    /// <summary>
    /// Si además de los anuncios manda la cadencia a leads fríos (capturas de Maps, etc.) de su
    /// vendedor. Apagado por defecto: hay vendedores con miles de filas frías de hace meses que no
    /// deben salir solas. Se prende en la línea de una cold caller que arranca de cero.
    /// </summary>
    public bool SendsColdLeads { get; set; }

    /// <summary>Batería reportada (0-100)</summary>
    public int? BatteryLevel { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
