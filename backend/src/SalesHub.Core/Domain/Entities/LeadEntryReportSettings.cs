namespace SalesHub.Core.Domain.Entities;

/// <summary>
/// Config (una sola fila) del reporte diario por WhatsApp de cuántos leads entraron a cada
/// teléfono. "Entró a un teléfono" = el primer mensaje que ese contacto le escribió a ESA
/// línea; se cuenta en el día (hora Argentina) de ese primer mensaje.
/// </summary>
public class LeadEntryReportSettings
{
    /// <summary>Fila única (Id = 1).</summary>
    public int Id { get; set; } = 1;

    public bool Enabled { get; set; } = true;

    /// <summary>A quién se manda, sólo dígitos con código de país (ej. "541169370050").</summary>
    public string RecipientPhone { get; set; } = "541169370050";

    /// <summary>Hora de Argentina (0-23) a partir de la cual sale el reporte del día anterior.</summary>
    public int SendHour { get; set; } = 9;

    /// <summary>
    /// Por qué línea sale. null = la primera conectada (prefiere una que pueda enviar; si todas
    /// son de solo escucha usa una igual: es un aviso interno al dueño, no un mensaje a un lead).
    /// </summary>
    public string? SendInstanceName { get; set; }

    /// <summary>Día (Argentina) que cubrió el último reporte enviado: dedup que sobrevive reinicios.</summary>
    public DateOnly? LastReportedDay { get; set; }
    public DateTimeOffset? LastSentAt { get; set; }

    /// <summary>Último intento (salga o no): espacia los reintentos cuando no hay línea viva.</summary>
    public DateTimeOffset? LastAttemptAt { get; set; }

    /// <summary>Por qué falló el último intento (null si salió).</summary>
    public string? LastError { get; set; }

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
