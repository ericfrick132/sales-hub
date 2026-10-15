namespace SalesHub.Core.Domain.Entities;

/// <summary>
/// DMs en frío por Instagram de una app (una fila por ProductKey). Los manda el userscript desde
/// el Chrome donde está logueada la cuenta de IG de la app; el backend reparte de a uno y es el que
/// hace cumplir el tope diario, el horario y la pausa entre DMs (la extensión solo pregunta).
/// Se edita en Apps → Automatizaciones → DMs por Instagram.
/// </summary>
public class IgDmSettings
{
    public string ProductKey { get; set; } = string.Empty;

    /// <summary>Arranca apagado: prenderlo es una decisión explícita.</summary>
    public bool Enabled { get; set; }

    /// <summary>Usuario de IG que manda (sin @). Si la extensión está logueada con otra cuenta, no recibe nada.</summary>
    public string? AccountUsername { get; set; }

    public int DailyCap { get; set; } = 10;
    public int MinGapMinutes { get; set; } = 6;
    public int MaxGapMinutes { get; set; } = 15;

    /// <summary>Franja de envío, hora Argentina: [desde, hasta).</summary>
    public int FromHour { get; set; } = 10;
    public int ToHour { get; set; } = 19;

    /// <summary>Texto del DM. Variables: {negocio} (nombre del lead), {app} (nombre de la app).</summary>
    public string Template { get; set; } = string.Empty;

    /// <summary>Instagram mostró un aviso de bloqueo: no se manda nada hasta esta fecha.</summary>
    public DateTimeOffset? PausedUntil { get; set; }
    public string? PausedReason { get; set; }

    /// <summary>Próximo envío permitido: la pausa al azar entre DMs, sorteada al mandar el anterior.</summary>
    public DateTimeOffset? NextAllowedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
