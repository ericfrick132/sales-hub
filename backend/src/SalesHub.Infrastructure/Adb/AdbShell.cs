using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SalesHub.Infrastructure.Options;

namespace SalesHub.Infrastructure.Adb;

/// <summary>
/// Wrapper de bajo nivel para ejecutar comandos adb contra un device Android.
/// Todo el blocking I/O ocurre acá; los callers (WhatsAppAdbClient) orquestan.
/// </summary>
public class AdbShell
{
    private readonly WhatsAppAdbOptions _opts;
    private readonly ILogger<AdbShell> _log;

    public AdbShell(IOptions<WhatsAppAdbOptions> opts, ILogger<AdbShell> log)
    {
        _opts = opts.Value;
        _log = log;
    }

    /// <summary>Ejecuta "adb shell &lt;command&gt;" y devuelve stdout. Lanza si exit code != 0.</summary>
    public async Task<string> ShellAsync(string command, CancellationToken ct = default)
    {
        var args = BuildAdbArgs($"shell {command}");
        return await RunAsync(args, ct);
    }

    /// <summary>Ejecuta "adb shell &lt;command&gt;" sin esperar resultado (fire-and-forget).</summary>
    public async Task ShellDetachedAsync(string command, CancellationToken ct = default)
    {
        var args = BuildAdbArgs($"shell {command}");
        await RunAsync(args, ct, throwOnError: false);
    }

    /// <summary>Ejecuta comandos del host adb (no shell), ej. "adb devices", "adb connect".</summary>
    public async Task<string> AdbAsync(string command, CancellationToken ct = default)
    {
        var args = BuildAdbArgs(command);
        return await RunAsync(args, ct);
    }

    /// <summary>Abre un deep-link WhatsApp. Ej: "https://wa.me/5491122334455".</summary>
    public async Task OpenWhatsAppUrlAsync(string url, CancellationToken ct = default)
    {
        var escaped = EscapeShellArg(url);
        await ShellAsync($"am start -a android.intent.action.VIEW -d {escaped}", ct);
        await Task.Delay(_opts.ChatOpenDelayMs, ct);
    }

    /// <summary>Escribe texto carácter por carácter en el campo de input activo.
    /// Usa "input text" de adb que maneja espacios y la mayoría de caracteres.</summary>
    public async Task TypeTextAsync(string text, CancellationToken ct = default)
    {
        // "adb shell input text" no maneja bien algunos caracteres. Los escapamos
        // reemplazando espacios por %s (la sintaxis de input text). También escapamos
        // comillas y caracteres especiales que el shell podría interpretar.
        var safe = text
            .Replace(" ", "%s")
            .Replace("\"", "\\\"")
            .Replace("'", "\\'")
            .Replace("&", "\\&")
            .Replace("<", "\\<")
            .Replace(">", "\\>")
            .Replace("|", "\\|")
            .Replace(";", "\\;")
            .Replace("$", "\\$")
            .Replace("`", "\\`")
            .Replace("\n", " ")    // input text no soporta newlines; los aplanamos
            .Replace("\r", "")
            .Replace("\t", " ");

        // Rompemos en chunks de 200 chars para evitar overflow del buffer de input
        const int chunkSize = 200;
        for (var i = 0; i < safe.Length; i += chunkSize)
        {
            var chunk = safe.Substring(i, Math.Min(chunkSize, safe.Length - i));
            if (chunk.Length == 0) continue;
            // Usamos single-quote wrapping para proteger el texto del shell
            // pero escapamos cualquier single-quote interno
            var escaped = chunk.Replace("'", "'\\''");
            await ShellAsync($"input text '{escaped}'", ct);
            // Micro-pausa entre chunks para que el UI thread no se trabe
            if (i + chunkSize < safe.Length)
                await Task.Delay(80, ct);
        }
    }

    /// <summary>Presiona Enter (keyevent 66) para enviar el mensaje.</summary>
    public async Task PressSendAsync(CancellationToken ct = default)
    {
        await Task.Delay(_opts.PreSendDelayMs, ct);
        await ShellAsync("input keyevent 66", ct);
    }

    /// <summary>Presiona Back (keyevent 4) para volver a la lista de chats.</summary>
    public async Task PressBackAsync(CancellationToken ct = default)
    {
        await ShellAsync("input keyevent 4", ct);
    }

    /// <summary>Verifica que el device esté conectado vía adb.</summary>
    public async Task<bool> IsDeviceConnectedAsync(CancellationToken ct = default)
    {
        try
        {
            var output = await AdbAsync("devices", ct);
            // "adb devices" output: first line "List of devices attached", then "serial\tdevice"
            var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            foreach (var line in lines.Skip(1))
            {
                if (line.Contains('\t') && line.EndsWith("device"))
                    return true;
            }
            return false;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "adb devices failed");
            return false;
        }
    }

    /// <summary>Toma un screenshot y lo guarda en el device. Devuelve la ruta en el device.</summary>
    public async Task<string> ScreenshotAsync(CancellationToken ct = default)
    {
        var path = $"/sdcard/adb_screenshot_{DateTimeOffset.UtcNow:yyyyMMddHHmmss}.png";
        await ShellAsync($"screencap -p {path}", ct);
        return path;
    }

    /// <summary>Dumpea el UI hierarchy (uiautomator) a /sdcard/window_dump.xml y devuelve el contenido.</summary>
    public async Task<string> DumpUiAsync(CancellationToken ct = default)
    {
        await ShellAsync("uiautomator dump /sdcard/window_dump.xml", ct);
        // El dump va a stdout en algunas versiones; en otras al archivo. Probamos ambos.
        try
        {
            return await ShellAsync("cat /sdcard/window_dump.xml", ct);
        }
        catch
        {
            return string.Empty;
        }
    }

    // ── private helpers ──

    private string BuildAdbArgs(string command)
    {
        var serial = _opts.DeviceSerial;
        if (!string.IsNullOrWhiteSpace(serial))
            return $"-s {serial} {command}";
        return command;
    }

    private async Task<string> RunAsync(string args, CancellationToken ct, bool throwOnError = true)
    {
        using var proc = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = _opts.AdbPath,
                Arguments = args,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };

        var tcs = new TaskCompletionSource<(int code, string stdout, string stderr)>();
        proc.Exited += (_, _) =>
        {
            var stdout = proc.StandardOutput.ReadToEnd();
            var stderr = proc.StandardError.ReadToEnd();
            tcs.TrySetResult((proc.ExitCode, stdout, stderr));
        };

        proc.Start();

        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(_opts.CommandTimeoutSeconds));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

        linked.Token.Register(() =>
        {
            if (!proc.HasExited)
            {
                try { proc.Kill(entireProcessTree: true); } catch { /* best-effort */ }
                tcs.TrySetCanceled();
            }
        });

        var (code, stdout, stderr) = await tcs.Task;

        if (throwOnError && code != 0)
            throw new AdbException($"adb exited {code}: {args}\nstderr: {stderr.Trim()}");

        return stdout;
    }

    private static string EscapeShellArg(string arg)
    {
        // Single-quote wrapping: seguro para casi todo. Escapamos ' internos como '\''.
        return $"'{arg.Replace("'", "'\\''")}'";
    }
}

public class AdbException : Exception
{
    public AdbException(string message) : base(message) { }
}
