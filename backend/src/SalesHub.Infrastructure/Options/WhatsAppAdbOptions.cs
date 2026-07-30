namespace SalesHub.Infrastructure.Options;

public class WhatsAppAdbOptions
{
    /// <summary>Path al binario adb. Default: "adb" (asume que está en PATH).</summary>
    public string AdbPath { get; set; } = "adb";

    /// <summary>Serial del device Android (o IP:puerto para TCP/IP). Ej: "192.168.1.50:5555".
    /// Si es null/empty, usa el primer device disponible (adb -d).</summary>
    public string? DeviceSerial { get; set; }

    /// <summary>Timeout en segundos para comandos adb individuales.</summary>
    public int CommandTimeoutSeconds { get; set; } = 30;

    /// <summary>Delay post-apertura de chat (ms). Da tiempo a WhatsApp para cargar.</summary>
    public int ChatOpenDelayMs { get; set; } = 2500;

    /// <summary>Delay post-escritura antes de enviar (ms). Simula pausa humana.</summary>
    public int PreSendDelayMs { get; set; } = 400;
}
