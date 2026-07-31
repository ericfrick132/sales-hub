using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SalesHub.Core.Domain.Entities;
using SalesHub.Core.Domain.Enums;
using SalesHub.Infrastructure.Persistence;

namespace SalesHub.Api.WebSockets;

/// <summary>
/// Hub central que gestiona todas las conexiones WebSocket de devices Android.
/// Singleton: una instancia para toda la app.
/// </summary>
public class DeviceHub
{
    private readonly ConcurrentDictionary<Guid, DeviceConnection> _connections = new();
    private readonly IServiceScopeFactory _scopes;

    public DeviceHub(IServiceScopeFactory scopes) => _scopes = scopes;

    public record DeviceConnection(WebSocket Socket, Guid DeviceId, string? SellerName);

    /// <summary>Registra una nueva conexión WebSocket para un device.</summary>
    public void Register(Guid deviceId, WebSocket socket, string? sellerName)
    {
        _connections[deviceId] = new DeviceConnection(socket, deviceId, sellerName);
    }

    /// <summary>Elimina la conexión de un device (se desconectó).</summary>
    public void Unregister(Guid deviceId)
    {
        _connections.TryRemove(deviceId, out _);
    }

    /// <summary>Envía un mensaje JSON a un device específico.</summary>
    public async Task SendAsync(Guid deviceId, object message, CancellationToken ct = default)
    {
        if (!_connections.TryGetValue(deviceId, out var conn)) return;
        if (conn.Socket.State != WebSocketState.Open) return;

        var json = JsonSerializer.Serialize(message);
        var bytes = Encoding.UTF8.GetBytes(json);
        await conn.Socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, ct);
    }

    /// <summary>Envía un comando ADB a un device (ejecutar shell command).</summary>
    public async Task<bool> SendAdbCommand(Guid deviceId, string command, CancellationToken ct = default)
    {
        if (!_connections.TryGetValue(deviceId, out var conn)) return false;
        if (conn.Socket.State != WebSocketState.Open) return false;

        var msg = new { type = "adb", command };
        await SendAsync(deviceId, msg, ct);
        return true;
    }

    /// <summary>Devuelve los IDs de todos los devices conectados.</summary>
    public IReadOnlyList<Guid> GetOnlineDevices() => _connections.Keys.ToList();

    /// <summary>Verifica si un device está conectado.</summary>
    public bool IsOnline(Guid deviceId) => _connections.ContainsKey(deviceId);

    /// <summary>Procesa un mensaje entrante de un device.</summary>
    public async Task HandleMessageAsync(Guid deviceId, string rawJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(rawJson);
            var type = doc.RootElement.GetProperty("type").GetString();

            switch (type)
            {
                case "heartbeat":
                    await UpdateHeartbeat(deviceId, doc.RootElement);
                    break;
                case "notification":
                    await HandleNotification(deviceId, doc.RootElement);
                    break;
                case "status":
                    await UpdateStatus(deviceId, doc.RootElement);
                    break;
            }
        }
        catch { /* ignore malformed messages */ }
    }

    private async Task UpdateHeartbeat(Guid deviceId, JsonElement data)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var device = await db.Devices.FindAsync(deviceId);
        if (device is null) return;

        device.LastHeartbeatAt = DateTimeOffset.UtcNow;
        device.Status = DeviceStatus.Online;

        if (data.TryGetProperty("battery", out var bat))
            device.BatteryLevel = bat.GetInt32();
        if (data.TryGetProperty("tailscaleIp", out var ip))
            device.TailscaleIp = ip.GetString();

        await db.SaveChangesAsync();
    }

    private async Task HandleNotification(Guid deviceId, JsonElement data)
    {
        // El device detectó una notificación de WhatsApp entrante
        var text = data.GetProperty("text").GetString();
        var title = data.GetProperty("title").GetString();

        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        // Buscar lead por phone (si viene en la notificación) o matchear por nombre
        if (data.TryGetProperty("phone", out var phoneEl))
        {
            var phone = phoneEl.GetString();
            var lead = await db.Leads.FirstOrDefaultAsync(l => l.WhatsappPhone == phone);
            if (lead is not null)
            {
                db.ConversationMessages.Add(new ConversationMessage
                {
                    Id = Guid.NewGuid(),
                    LeadId = lead.Id,
                    Direction = MessageDirection.Inbound,
                    Status = MessageDeliveryStatus.Received,
                    Text = $"{title}: {text}",
                    Timestamp = DateTimeOffset.UtcNow,
                    IsRead = false
                });
                if (lead.Status == LeadStatus.Sent) lead.Status = LeadStatus.Replied;
                await db.SaveChangesAsync();
            }
        }
    }

    private async Task UpdateStatus(Guid deviceId, JsonElement data)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var device = await db.Devices.FindAsync(deviceId);
        if (device is null) return;

        if (data.TryGetProperty("battery", out var bat))
            device.BatteryLevel = bat.GetInt32();
        if (data.TryGetProperty("tailscaleIp", out var ip))
            device.TailscaleIp = ip.GetString();

        device.LastHeartbeatAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();
    }
}
