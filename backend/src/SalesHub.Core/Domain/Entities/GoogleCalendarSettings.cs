namespace SalesHub.Core.Domain.Entities;

/// <summary>
/// Config (una sola fila) del Google Calendar donde el bot agenda las demos: de ahí saca los huecos
/// libres y ahí crea el evento con Meet, invitando al lead por mail. La cuenta se conecta una vez
/// con OAuth desde la config del bot ("Conectar Google Calendar"); queda guardado el refresh token.
/// </summary>
public class GoogleCalendarSettings
{
    /// <summary>Fila única (Id = 1).</summary>
    public int Id { get; set; } = 1;

    /// <summary>Cliente OAuth de Google Cloud. Si el .env trae Google__OAuthClientId/Secret, pisan a estos.</summary>
    public string? ClientId { get; set; }
    public string? ClientSecret { get; set; }

    /// <summary>Refresh token de la cuenta conectada. Null = no conectado (el bot usa el link de demo).</summary>
    public string? RefreshToken { get; set; }

    /// <summary>Mail de la cuenta conectada (solo para mostrar).</summary>
    public string? Email { get; set; }

    /// <summary>Calendario donde se mira y se crea ("primary" = el principal de la cuenta).</summary>
    public string CalendarId { get; set; } = "primary";

    /// <summary>Franja en hora Argentina en la que se ofrecen demos: [StartHour, EndHour). La demo termina antes de EndHour.</summary>
    public int StartHour { get; set; } = 10;
    public int EndHour { get; set; } = 19;

    /// <summary>Días hábiles (0 = domingo … 6 = sábado), separados por coma.</summary>
    public string Days { get; set; } = "1,2,3,4,5";

    /// <summary>Duración de la demo y paso entre horarios posibles.</summary>
    public int SlotMinutes { get; set; } = 30;

    /// <summary>Margen mínimo desde ahora hasta el primer horario ofrecido.</summary>
    public int MinNoticeMinutes { get; set; } = 120;

    /// <summary>State del OAuth en curso (anti-CSRF del callback) y cuándo se generó.</summary>
    public string? PendingState { get; set; }
    public DateTimeOffset? PendingStateAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
