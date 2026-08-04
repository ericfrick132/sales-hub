using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SalesHub.Core.Abstractions;
using SalesHub.Core.Domain.Enums;
using SalesHub.Infrastructure.Persistence;

namespace SalesHub.Infrastructure.Services;

/// <summary>
/// Manda un texto por la línea del vendedor eligiendo el transporte: si tiene un celu
/// vinculado (bridge) lo encola ahí; si no, cae a Evolution.
///
/// Existe porque las respuestas del agente salían SIEMPRE por Evolution: con las líneas
/// migradas a dispositivos, el lead contestaba y la respuesta moría en una instancia
/// muerta. Ver [[feedback-no-evolution]].
///
/// Ojo con la semántica del true: por celu significa "encolado, sale en el próximo poll
/// (≤30s) con el tipeo", no "ya entregado".
/// </summary>
public class SellerLineSender
{
    private readonly ApplicationDbContext _db;
    private readonly IEvolutionClient _evo;
    private readonly BridgeDirectSendService _direct;
    private readonly ILogger<SellerLineSender> _log;

    public SellerLineSender(ApplicationDbContext db, IEvolutionClient evo,
        BridgeDirectSendService direct, ILogger<SellerLineSender> log)
    {
        _db = db; _evo = evo; _direct = direct; _log = log;
    }

    /// <summary>¿La línea de este vendedor puede mandar texto? (celu vinculado o Evolution viva)</summary>
    public async Task<bool> CanSendAsync(Guid? sellerId, string? instanceName, InstanceStatus? instanceStatus, CancellationToken ct)
    {
        if (instanceStatus == InstanceStatus.Connected && !string.IsNullOrWhiteSpace(instanceName)) return true;
        if (sellerId is null) return false;
        return await _db.Devices.AnyAsync(d => d.SellerId == sellerId, ct);
    }

    public async Task<bool> SendTextAsync(Guid? sellerId, string? instanceName, string phone, string text, CancellationToken ct)
    {
        var deviceId = sellerId is null ? null : await _db.Devices
            .Where(d => d.SellerId == sellerId)
            .OrderByDescending(d => d.LastHeartbeatAt ?? DateTimeOffset.MinValue)
            .Select(d => (Guid?)d.Id)
            .FirstOrDefaultAsync(ct);

        if (deviceId is not null)
        {
            var digits = new string(phone.Where(char.IsDigit).ToArray());
            _direct.Queue(deviceId.Value, digits, text, BridgeDirectSendService.KindChatReply);
            _log.LogInformation("Respuesta encolada al celu {Device} para {Phone}", deviceId, digits);
            return true;
        }

        if (string.IsNullOrWhiteSpace(instanceName)) return false;
        return await _evo.SendTextAsync(instanceName, phone, text, ct);
    }
}
