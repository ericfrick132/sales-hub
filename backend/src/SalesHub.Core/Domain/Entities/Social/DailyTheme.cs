namespace SalesHub.Core.Domain.Entities.Social;

/// <summary>
/// "Tema del día": un texto que el usuario manda por WhatsApp (ej. "Argentina ganó
/// el partido") para teñir los posteos del día. El worker lo inyecta como pista de
/// inspiración en la generación mientras esté vigente (<see cref="ValidUntil"/>).
/// Fila única (Id = 1).
/// </summary>
public class DailyTheme
{
    public int Id { get; set; } = 1;

    /// <summary>El texto que dispara el ángulo del día. Vacío = sin tema activo.</summary>
    public string Text { get; set; } = string.Empty;

    /// <summary>App a la que aplica (null = todas).</summary>
    public string? ProductKey { get; set; }

    /// <summary>Hasta cuándo rige (normalmente fin del día AR). Pasado esto, se ignora.</summary>
    public DateTimeOffset? ValidUntil { get; set; }

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
