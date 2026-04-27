namespace SalesHub.Core.Abstractions;

public record WhatsappCheckResult(string Number, bool Exists, string? Jid);

public record InstanceConnectionInfo(string Status, string? PhoneNumber, string? QrBase64);

public interface IEvolutionClient
{
    Task<InstanceConnectionInfo> GetInstanceStatusAsync(string instanceName, CancellationToken ct = default);
    Task<InstanceConnectionInfo> EnsureInstanceAsync(string instanceName, CancellationToken ct = default);
    Task<string?> GetQrCodeAsync(string instanceName, CancellationToken ct = default);
    Task LogoutInstanceAsync(string instanceName, CancellationToken ct = default);

    Task<IReadOnlyList<WhatsappCheckResult>> CheckNumbersAsync(string instanceName, IEnumerable<string> phoneNumbers, CancellationToken ct = default);

    Task SetPresenceTypingAsync(string instanceName, string jid, int durationSeconds, CancellationToken ct = default);
    Task MarkAllChatsReadAsync(string instanceName, CancellationToken ct = default);
    Task<bool> SendTextAsync(string instanceName, string jid, string message, CancellationToken ct = default);
}
