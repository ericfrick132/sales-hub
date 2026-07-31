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

    /// <summary>Batería reportada (0-100)</summary>
    public int? BatteryLevel { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
