using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using Microsoft.EntityFrameworkCore;
using SalesHub.Core.Domain.Entities;
using SalesHub.Core.Domain.Enums;
using SalesHub.Infrastructure.Persistence;

namespace SalesHub.Api.WebSockets;

/// <summary>
/// Middleware que acepta conexiones WebSocket de devices Android en /ws/devices.
/// Valida el token de pairing, registra la conexión en DeviceHub y mantiene
/// el loop de lectura de mensajes entrantes.
/// </summary>
public class DeviceWebSocketMiddleware
{
    private readonly RequestDelegate _next;
    private readonly DeviceHub _hub;

    public DeviceWebSocketMiddleware(RequestDelegate next, DeviceHub hub)
    {
        _next = next;
        _hub = hub;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (context.Request.Path == "/ws/devices" && context.WebSockets.IsWebSocketRequest)
        {
            var token = context.Request.Query["token"].FirstOrDefault();
            if (string.IsNullOrWhiteSpace(token))
            {
                context.Response.StatusCode = 400;
                return;
            }

            // Validar token contra DB
            var db = context.RequestServices.GetRequiredService<ApplicationDbContext>();
            var device = await db.Devices
                .Include(d => d.Seller)
                .FirstOrDefaultAsync(d => d.PairingToken == token
                    && d.PairingTokenExpiresAt > DateTimeOffset.UtcNow
                    && d.Status == DeviceStatus.Pairing);

            if (device is null)
            {
                context.Response.StatusCode = 401;
                await context.Response.WriteAsync("Invalid or expired pairing token");
                return;
            }

            // Upgrade a WebSocket
            var socket = await context.WebSockets.AcceptWebSocketAsync();

            // Marcar como online y limpiar token
            device.Status = DeviceStatus.Online;
            device.PairingToken = null;
            device.PairingTokenExpiresAt = null;
            device.LastHeartbeatAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();

            var sellerName = device.Seller?.DisplayName;
            _hub.Register(device.Id, socket, sellerName);

            try
            {
                await ReceiveLoop(socket, device.Id);
            }
            finally
            {
                _hub.Unregister(device.Id);
                // Marcar offline (best-effort)
                try
                {
                    using var scope = context.RequestServices.CreateScope();
                    var db2 = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                    var dev = await db2.Devices.FindAsync(device.Id);
                    if (dev is not null)
                    {
                        dev.Status = DeviceStatus.Offline;
                        await db2.SaveChangesAsync();
                    }
                }
                catch { /* ignore */ }
            }
        }
        else
        {
            await _next(context);
        }
    }

    private async Task ReceiveLoop(WebSocket socket, Guid deviceId)
    {
        var buffer = new byte[8192];
        while (socket.State == WebSocketState.Open)
        {
            try
            {
                var result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
                if (result.MessageType == WebSocketMessageType.Close) break;

                if (result.MessageType == WebSocketMessageType.Text)
                {
                    var json = Encoding.UTF8.GetString(buffer, 0, result.Count);
                    // El hub procesa el mensaje en background
                    _ = Task.Run(() => _hub.HandleMessageAsync(deviceId, json));
                }
            }
            catch (WebSocketException) { break; }
        }
    }
}

/// <summary>
/// Extension method para registrar el middleware en la pipeline.
/// </summary>
public static class DeviceWebSocketExtensions
{
    public static IApplicationBuilder UseDeviceWebSockets(this IApplicationBuilder app)
    {
        app.UseWebSockets();
        return app.UseMiddleware<DeviceWebSocketMiddleware>();
    }
}
