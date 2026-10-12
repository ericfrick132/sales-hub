using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SalesHub.Core.Abstractions;
using SalesHub.Core.Domain.Entities;
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

    /// <summary>Por dónde sale un mensaje a un lead.</summary>
    public sealed record LeadLine(string? InstanceName, Guid? BridgeSellerId, bool IsSellerLine)
    {
        public bool IsBridge => BridgeSellerId is not null && InstanceName is null;
    }

    /// <summary>
    /// La línea donde VIVE la conversación (la que recibió el último inbound) si está conectada y
    /// no es solo-escucha; si no, la línea del vendedor; si no, el celu (bridge) del vendedor.
    /// Con las líneas por app el vendedor del lead es la PERSONA responsable, no el número: sin
    /// esto el bot le contestaría desde otro número al que el cliente le escribió.
    /// </summary>
    public async Task<LeadLine?> ResolveForLeadAsync(Lead lead, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(lead.WhatsappPhone)) return null;
        var lastInstance = await _db.ConversationMessages.AsNoTracking()
            .Where(m => m.LeadId == lead.Id && m.Direction == MessageDirection.Inbound && m.EvolutionInstance != null)
            .OrderByDescending(m => m.Timestamp)
            .Select(m => m.EvolutionInstance)
            .FirstOrDefaultAsync(ct);
        if (!string.IsNullOrWhiteSpace(lastInstance))
        {
            var line = await _db.EvolutionInstances.AsNoTracking().FirstOrDefaultAsync(i => i.InstanceName == lastInstance, ct);
            if (line is not null && line.Status == InstanceStatus.Connected && !line.ListenOnly)
                return new LeadLine(line.InstanceName, null, line.SellerId != null && line.SellerId == lead.SellerId);
        }
        var sellerInst = lead.Seller?.EvolutionInstance
            ?? (lead.SellerId is null ? null : await _db.EvolutionInstances.AsNoTracking()
                .FirstOrDefaultAsync(i => i.SellerId == lead.SellerId, ct));
        if (sellerInst is not null && sellerInst.Status == InstanceStatus.Connected && !sellerInst.ListenOnly)
            return new LeadLine(sellerInst.InstanceName, null, true);
        if (lead.SellerId is not null && await _db.Devices.AnyAsync(d => d.SellerId == lead.SellerId, ct))
            return new LeadLine(null, lead.SellerId, true);
        return null;
    }

    /// <summary>Manda un texto al lead por la línea resuelta con <see cref="ResolveForLeadAsync"/>.</summary>
    public Task<bool> SendToLeadAsync(LeadLine line, string phone, string text, CancellationToken ct) =>
        line.InstanceName is not null
            ? _evo.SendTextAsync(line.InstanceName, phone, text, ct)
            : SendTextAsync(line.BridgeSellerId, null, phone, text, ct);

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
