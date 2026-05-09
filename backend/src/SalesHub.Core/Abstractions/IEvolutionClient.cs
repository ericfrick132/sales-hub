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
    /// <summary>Manda presence "recording" (graba audio…) — igual que typing pero la versión de notas de voz.</summary>
    Task SetPresenceRecordingAsync(string instanceName, string jid, int durationSeconds, CancellationToken ct = default);
    Task MarkAllChatsReadAsync(string instanceName, CancellationToken ct = default);
    Task<bool> SendTextAsync(string instanceName, string jid, string message, CancellationToken ct = default);
    /// <summary>Manda un archivo (imagen / pdf / cualquier mime) con caption opcional. Recibe el contenido en bytes.</summary>
    Task<bool> SendMediaAsync(string instanceName, string jid, byte[] content, string mimeType, string fileName, string? caption, CancellationToken ct = default);

    /// <summary>Manda audio como nota de voz (PTT) usando el endpoint sendWhatsAppAudio.
    /// Evolution convierte a OGG/Opus si hace falta. Recibe el archivo en bytes (mp3/m4a/ogg/wav).</summary>
    Task<bool> SendVoiceNoteAsync(string instanceName, string jid, byte[] audio, CancellationToken ct = default);

    /// <summary>Convierte el audio a OGG/Opus mono y devuelve los bytes listos
    /// para mandar + la duración real en segundos (ffprobe). Útil para mostrar
    /// el indicador "grabando audio…" en el chat por exactamente la duración
    /// del audio antes de enviarlo.</summary>
    Task<PreparedVoiceNote> PrepareVoiceNoteAsync(byte[] input, CancellationToken ct = default);

    /// <summary>Envía bytes ya convertidos a OGG/Opus (output de PrepareVoiceNoteAsync)
    /// como nota de voz, sin re-transcodear.</summary>
    Task<bool> SendPreparedVoiceNoteAsync(string instanceName, string jid, byte[] ogg, CancellationToken ct = default);
}

public record PreparedVoiceNote(byte[] OggBytes, int DurationSeconds);
