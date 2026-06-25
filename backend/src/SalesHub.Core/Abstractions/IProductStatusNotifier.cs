namespace SalesHub.Core.Abstractions;

/// <summary>
/// Avisa al producto de origen (GymHero/TurnosPro) cuando cambia el estado de venta de un lead
/// (Interesado/Demo/Cerrado/Perdido), para que el producto lo refleje. No-op si el lead no tiene
/// ExternalId o el producto no tiene StatusWebhookUrl configurado.
/// </summary>
public interface IProductStatusNotifier
{
    Task NotifyAsync(string productKey, string? externalId, string status, bool won, CancellationToken ct = default);
}
