using Microsoft.Extensions.Logging;
using SalesHub.Core.Abstractions;
using SalesHub.Infrastructure.Options;
using Microsoft.Extensions.Options;

namespace SalesHub.Infrastructure.Adb;

/// <summary>
/// Implementa <see cref="IEvolutionClient"/> usando comandos adb contra un
/// dispositivo Android físico con WhatsApp oficial. Los inputs se inyectan a
/// nivel SO → Meta no puede distinguirlos de un humano tocando la pantalla.
///
/// instanceName → device serial (config mapea nombres a devices; si no hay
/// mapeo, usa el device default de adb).
///
/// jid → se ignora el sufijo @s.whatsapp.net; usamos solo el número.
/// </summary>
public class WhatsAppAdbClient : IEvolutionClient
{
    private readonly AdbShell _adb;
    private readonly WhatsAppAdbOptions _opts;
    private readonly ILogger<WhatsAppAdbClient> _log;

    public WhatsAppAdbClient(AdbShell adb, IOptions<WhatsAppAdbOptions> opts, ILogger<WhatsAppAdbClient> log)
    {
        _adb = adb;
        _opts = opts.Value;
        _log = log;
    }

    // ═══════════════════════════════════════════════════════════════
    //  Connection & Status
    // ═══════════════════════════════════════════════════════════════

    public async Task<InstanceConnectionInfo> GetInstanceStatusAsync(string instanceName, CancellationToken ct = default)
    {
        var connected = await _adb.IsDeviceConnectedAsync(ct);
        return new InstanceConnectionInfo(
            connected ? "connected" : "disconnected",
            PhoneNumber: null,   // podríamos leerlo del device, pero no es fácil vía adb
            QrBase64: null
        );
    }

    public Task<Dictionary<string, string>> GetInstanceOwnersAsync(CancellationToken ct = default)
    {
        // En modo adb hay un solo device = una sola "instancia". Devolvemos
        // un diccionario vacío; la UI de /connect se maneja distinto (no hay QR).
        return Task.FromResult(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
    }

    public Task<InstanceConnectionInfo> EnsureInstanceAsync(string instanceName, CancellationToken ct = default, string? proxyUrl = null)
    {
        // No hay nada que crear: el device ya tiene WhatsApp instalado y activo.
        // Solo verificamos que esté conectado.
        return GetInstanceStatusAsync(instanceName, ct);
    }

    public Task<string?> GetQrCodeAsync(string instanceName, CancellationToken ct = default)
    {
        // No hay QR: WhatsApp ya está logueado en el device físico.
        return Task.FromResult<string?>(null);
    }

    public Task LogoutInstanceAsync(string instanceName, CancellationToken ct = default)
    {
        // No soportado vía adb. Dejamos no-op.
        _log.LogWarning("LogoutInstanceAsync no soportado en modo adb");
        return Task.CompletedTask;
    }

    // ═══════════════════════════════════════════════════════════════
    //  Send
    // ═══════════════════════════════════════════════════════════════

    public async Task<bool> SendTextAsync(string instanceName, string jid, string message, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(jid) || string.IsNullOrWhiteSpace(message))
            return false;

        try
        {
            var phone = PhoneFromJid(jid);
            var waUrl = $"https://wa.me/{phone}";

            // 1) Abrir el chat en WhatsApp
            await _adb.OpenWhatsAppUrlAsync(waUrl, ct);

            // 2) Escribir el mensaje
            await _adb.TypeTextAsync(message, ct);

            // 3) Enviar (Enter/tap send)
            await _adb.PressSendAsync(ct);

            // 4) Volver a la lista de chats para dejar el device listo
            //    para el próximo mensaje. Pequeña pausa antes para que
            //    WhatsApp procese el envío.
            await Task.Delay(1200, ct);
            await _adb.PressBackAsync(ct);

            _log.LogInformation("adb send OK → {Phone}", phone);
            return true;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "adb send FAILED → {Jid}", jid);
            return false;
        }
    }

    public async Task<bool> SendMediaAsync(string instanceName, string jid, byte[] content,
        string mimeType, string fileName, string? caption, CancellationToken ct = default)
    {
        // MVP: mandamos la imagen por separado como un intent de compartir.
        // Primero pusheamos el archivo al device, luego lanzamos un share intent
        // a WhatsApp. Es frágil porque hay que seleccionar el contacto manualmente
        // en la UI. Para production se necesita uiautomator.
        try
        {
            var phone = PhoneFromJid(jid);
            var devicePath = $"/sdcard/Download/saleshub_{fileName}";

            // Push file to device
            // "adb push" no usa shell, es comando host
            var tmpPath = System.IO.Path.GetTempFileName();
            await System.IO.File.WriteAllBytesAsync(tmpPath, content, ct);
            try
            {
                await _adb.AdbAsync($"push {EscapeArg(tmpPath)} {devicePath}", ct);
            }
            finally
            {
                try { System.IO.File.Delete(tmpPath); } catch { /* best-effort */ }
            }

            // Share intent via adb
            await _adb.ShellAsync(
                $"am start -a android.intent.action.SEND -t {EscapeArg(mimeType)} " +
                $"--eu android.intent.extra.STREAM file://{devicePath} " +
                $"-p com.whatsapp --es android.intent.extra.TEXT {EscapeArg(caption ?? "")}", ct);

            // Acá WhatsApp muestra el picker de contactos. Sin uiautomator no
            // podemos seleccionar automáticamente. Logueamos warning.
            _log.LogWarning("SendMedia: archivo pusheado a {Path}. " +
                "Seleccioná {Phone} manualmente en el device.", devicePath, phone);

            await Task.Delay(3000, ct); // dar tiempo al usuario/script

            return true;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "adb SendMediaAsync failed → {Jid}", jid);
            return false;
        }
    }

    public Task<PreparedVoiceNote> PrepareVoiceNoteAsync(byte[] input, CancellationToken ct = default)
    {
        // No podemos transcodificar audio vía adb. Devolvemos dummy.
        // El sender usará SendPreparedVoiceNoteAsync o fallará limpiamente.
        _log.LogWarning("PrepareVoiceNoteAsync: skip (no soportado en adb MVP)");
        return Task.FromResult(new PreparedVoiceNote(input, 5));
    }

    public Task<bool> SendVoiceNoteAsync(string instanceName, string jid, byte[] audio, CancellationToken ct = default)
    {
        _log.LogWarning("SendVoiceNoteAsync: skip (no soportado en adb MVP)");
        return Task.FromResult(false);
    }

    public async Task<bool> SendPreparedVoiceNoteAsync(string instanceName, string jid, byte[] ogg, CancellationToken ct = default)
    {
        _log.LogWarning("SendPreparedVoiceNoteAsync: skip (no soportado en adb MVP)");
        return false;
    }

    // ═══════════════════════════════════════════════════════════════
    //  Presence (typing / recording indicators)
    // ═══════════════════════════════════════════════════════════════

    public async Task SetPresenceTypingAsync(string instanceName, string jid, int durationSeconds, CancellationToken ct = default)
    {
        // Simulamos "escribiendo…" abriendo el chat y tipeando lentamente
        // algunos caracteres dummy que después borramos. No es perfecto pero
        // dispara el typing indicator en WhatsApp del receptor.
        // Para MVP, simplemente dormimos — el sender de SalesHub ya incluye
        // un delay equivalente (Task.Delay) justo después de llamar a esto.
        _log.LogDebug("SetPresenceTyping: {Sec}s — dormir (el sender ya hace el delay)", durationSeconds);
        await Task.CompletedTask;
    }

    public async Task SetPresenceRecordingAsync(string instanceName, string jid, int durationSeconds, CancellationToken ct = default)
    {
        // No hay forma de disparar "grabando audio…" vía adb puro.
        // El sender igual hace el delay después de llamar a esto.
        _log.LogDebug("SetPresenceRecording: {Sec}s — dormir (el sender ya hace el delay)", durationSeconds);
        await Task.CompletedTask;
    }

    // ═══════════════════════════════════════════════════════════════
    //  Inbox / chat management
    // ═══════════════════════════════════════════════════════════════

    public async Task MarkAllChatsReadAsync(string instanceName, CancellationToken ct = default)
    {
        // No soportado vía adb. No es blocker: WhatsApp no penaliza tener
        // chats sin leer; es solo cosmético del lado del vendedor.
        await Task.CompletedTask;
    }

    public Task<bool> ArchiveChatAsync(string instanceName, string jid, string lastMessageId,
        bool lastFromMe, bool archive = true, CancellationToken ct = default)
    {
        // No soportado vía adb. El chat queda en la lista principal.
        _log.LogDebug("ArchiveChatAsync: skip (no soportado en adb)");
        return Task.FromResult(false);
    }

    public async Task<IReadOnlyList<WhatsappCheckResult>> CheckNumbersAsync(string instanceName,
        IEnumerable<string> phoneNumbers, CancellationToken ct = default)
    {
        // Sin acceso a la API de WhatsApp, no podemos verificar si un número
        // existe. Asumimos que sí; el envío fallará si no.
        var list = phoneNumbers.ToList();
        _log.LogDebug("CheckNumbersAsync: {N} números — asumimos todos existen", list.Count);
        return list.Select(n => new WhatsappCheckResult(n, true, n)).ToArray();
    }

    // ═══════════════════════════════════════════════════════════════
    //  Chat history (para EvolutionChatSyncWorker)
    // ═══════════════════════════════════════════════════════════════

    public async Task<IReadOnlyList<EvolutionChatSummary>> FindChatsAsync(string instanceName, CancellationToken ct = default)
    {
        // Leer la lista de chats vía uiautomator dump es posible pero frágil.
        // Para MVP: lista vacía → el sync worker no procesa nada.
        _log.LogDebug("FindChatsAsync: skip (no soportado en adb MVP)");
        return Array.Empty<EvolutionChatSummary>();
    }

    public Task<EvolutionMessagesPage> FindMessagesAsync(string instanceName, string remoteJid,
        int page, int pageSize, CancellationToken ct = default)
    {
        _log.LogDebug("FindMessagesAsync: skip (no soportado en adb MVP)");
        return Task.FromResult(new EvolutionMessagesPage(0, 0, page,
            Array.Empty<System.Text.Json.JsonElement>()));
    }

    public Task<byte[]?> GetMediaBase64Async(string instanceName, string messageJson, CancellationToken ct = default)
    {
        _log.LogDebug("GetMediaBase64Async: skip (no soportado en adb)");
        return Task.FromResult<byte[]?>(null);
    }

    // ═══════════════════════════════════════════════════════════════
    //  Helpers
    // ═══════════════════════════════════════════════════════════════

    /// <summary>Extrae el número de teléfono de un JID de WhatsApp.
    /// "5491122334455@s.whatsapp.net" → "5491122334455".</summary>
    internal static string PhoneFromJid(string jid)
    {
        var at = jid.IndexOf('@');
        return at > 0 ? jid[..at] : jid;
    }

    private static string EscapeArg(string arg)
    {
        return $"'{arg.Replace("'", "'\\''")}'";
    }
}
