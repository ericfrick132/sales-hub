namespace SalesHub.Core.Domain.Entities;

/// <summary>
/// Una toma de calibración de voz: Eric graba en su self-chat el guion que el bot le
/// mandó, y queda apareada guion↔audio (por WhatsappMessageId se descarga de Evolution).
/// Sirve para medir su prosodia real, mejorar el clon TTS y acumular material para el
/// Professional Voice Clone.
/// </summary>
public class VoiceCalibrationTake
{
    public Guid Id { get; set; }
    public string InstanceName { get; set; } = string.Empty;
    /// <summary>Clave del guion (ej. "cal-03").</summary>
    public string ScriptKey { get; set; } = string.Empty;
    /// <summary>El texto exacto que se le pidió leer.</summary>
    public string ScriptText { get; set; } = string.Empty;
    /// <summary>Id del mensaje de audio en WhatsApp/Evolution (para descargar la toma).</summary>
    public string? WhatsappMessageId { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
