namespace SalesHub.Core.Abstractions;

public record WhatsappCheckResult(string Number, bool Exists, string? Jid);

public record InstanceConnectionInfo(string Status, string? PhoneNumber, string? QrBase64);

public interface IEvolutionClient
{
    Task<InstanceConnectionInfo> GetInstanceStatusAsync(string instanceName, CancellationToken ct = default);

    /// <summary>Mapa instancia -> numero de WhatsApp vinculado (owner jid sin dominio), via fetchInstances.</summary>
    Task<Dictionary<string, string>> GetInstanceOwnersAsync(CancellationToken ct = default);

    /// <summary>
    /// Archiva (o desarchiva) el chat en el WhatsApp de la línea. Pensado para líneas que
    /// comparten teléfono con el uso personal: el tráfico de ventas queda en Archivados.
    /// OJO: en WhatsApp un chat archivado vuelve a la lista principal al llegar un mensaje
    /// nuevo, salvo que el teléfono tenga "Mantener chats archivados" activado.
    /// </summary>
    Task<bool> ArchiveChatAsync(string instanceName, string jid, string lastMessageId, bool lastFromMe, bool archive = true, CancellationToken ct = default);
    /// <summary>Crea (si no existe) y asegura webhook + proxy de la instancia. <paramref name="proxyUrl"/>
    /// opcional: el proxy de salida de esta línea; si es null cae al global (Evolution:ProxyUrl).</summary>
    Task<InstanceConnectionInfo> EnsureInstanceAsync(string instanceName, CancellationToken ct = default, string? proxyUrl = null);
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

    /// <summary>Baja y desencripta el media de un mensaje inbound. Evolution hace
    /// la cripto de WhatsApp por nosotros. <paramref name="messageJson"/> es el
    /// JSON del mensaje (key + message), tal como llegó por webhook. Devuelve los
    /// bytes desencriptados o null si falla.</summary>
    Task<byte[]?> GetMediaBase64Async(string instanceName, string messageJson, CancellationToken ct = default);

    /// <summary>Chats de la instancia (POST /chat/findChats). Trae remoteJid + última actividad;
    /// el sync de conversaciones lo usa para saber qué chats tuvieron movimiento reciente.</summary>
    Task<IReadOnlyList<EvolutionChatSummary>> FindChatsAsync(string instanceName, CancellationToken ct = default);

    /// <summary>Mensajes de un chat (POST /chat/findMessages), paginados del más nuevo al más
    /// viejo. Cada record tiene la misma forma que el "data" del webhook messages.upsert
    /// (key + message + messageTimestamp + pushName).</summary>
    Task<EvolutionMessagesPage> FindMessagesAsync(string instanceName, string remoteJid, int page, int pageSize, CancellationToken ct = default);
}

public record EvolutionChatSummary(string RemoteJid, DateTimeOffset? UpdatedAt);

public record EvolutionMessagesPage(int Total, int Pages, int CurrentPage, IReadOnlyList<System.Text.Json.JsonElement> Records);

public record PreparedVoiceNote(byte[] OggBytes, int DurationSeconds);
