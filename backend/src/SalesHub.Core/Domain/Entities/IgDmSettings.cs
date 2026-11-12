namespace SalesHub.Core.Domain.Entities;

/// <summary>
/// DMs por Instagram de una app (una fila por ProductKey). Los manda la extensión de Chrome donde
/// está logueada la cuenta de IG de la app o un celular Android del bridge (<see cref="Sender"/>);
/// en los dos casos el backend reparte de a uno y es el que hace cumplir el tope del día, el
/// horario y la pausa entre DMs (el que tipea solo pregunta qué sigue).
/// Se edita en Apps → Automatizaciones → DMs por Instagram.
/// </summary>
public class IgDmSettings
{
    public const string SenderExtension = "extension";
    public const string SenderDevice = "device";

    public string ProductKey { get; set; } = string.Empty;

    /// <summary>Arranca apagado: prenderlo es una decisión explícita.</summary>
    public bool Enabled { get; set; }

    /// <summary>Usuario de IG que manda (sin @). Si la extensión está logueada con otra cuenta, no recibe nada.</summary>
    public string? AccountUsername { get; set; }

    /// <summary>Tope fijo en frío por día. Con <see cref="ColdRamp"/> cargada manda la rampa.</summary>
    public int DailyCap { get; set; } = 10;
    public int MinGapMinutes { get; set; } = 6;
    public int MaxGapMinutes { get; set; } = 15;

    /// <summary>Franja de envío, hora Argentina: [desde, hasta).</summary>
    public int FromHour { get; set; } = 10;
    public int ToHour { get; set; } = 19;

    /// <summary>
    /// Texto del DM en frío. Variables: {negocio} (nombre del lead), {app} (nombre de la app).
    /// Varias versiones separadas por una línea con "---": sale una al azar por lead.
    /// </summary>
    public string Template { get; set; } = string.Empty;

    /// <summary>Instagram mostró un aviso de bloqueo: no se manda nada hasta esta fecha.</summary>
    public DateTimeOffset? PausedUntil { get; set; }
    public string? PausedReason { get; set; }

    /// <summary>Próximo envío permitido: la pausa al azar entre DMs, sorteada al mandar el anterior.</summary>
    public DateTimeOffset? NextAllowedAt { get; set; }

    // ── Quién tipea ────────────────────────────────────────────────────────────────────────

    /// <summary><see cref="SenderExtension"/> (Chrome) o <see cref="SenderDevice"/> (celular del bridge).</summary>
    public string Sender { get; set; } = SenderExtension;

    /// <summary>Celular que manda cuando <see cref="Sender"/> es device. Un celular manda una sola app.</summary>
    public Guid? DeviceId { get; set; }

    // ── Rampa ──────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Tope en frío por semana, separado por comas ("10,20,30,40"): la semana 1 usa el primero y
    /// el último vale de ahí en adelante. Vacío = <see cref="DailyCap"/> fijo.
    /// </summary>
    public string? ColdRamp { get; set; }

    /// <summary>Día (Argentina) en que arrancó la semana 1 de la rampa.</summary>
    public DateOnly? RampStartedOn { get; set; }

    /// <summary>Después de un aviso de Instagram los topes van a la mitad hasta esta fecha.</summary>
    public DateTimeOffset? ThrottledUntil { get; set; }

    // ── Seguidores ─────────────────────────────────────────────────────────────────────────

    /// <summary>Escribirles también a los seguidores de la cuenta (solo con celular).</summary>
    public bool FollowersEnabled { get; set; }

    /// <summary>Tope a seguidores por semana, mismo formato que <see cref="ColdRamp"/>.</summary>
    public string? FollowerRamp { get; set; }

    /// <summary>Texto para seguidores (mismas reglas de versiones que <see cref="Template"/>). Variable: {app}.</summary>
    public string FollowerTemplate { get; set; } = string.Empty;

    /// <summary>Usuarios a los que nunca se les escribe (equipo, cuentas propias), separados por coma, espacio o línea.</summary>
    public string? FollowerExclude { get; set; }

    /// <summary>Última vez que el celular leyó la lista de seguidores.</summary>
    public DateTimeOffset? FollowerScanAt { get; set; }

    /// <summary>Pedido manual de leer la lista de seguidores en la próxima consulta del celular.</summary>
    public bool FollowerScanRequested { get; set; }

    /// <summary>Cuántas pantallas baja en la lista: crece sola cuando ya no aparecen seguidores nuevos.</summary>
    public int FollowerScanSwipes { get; set; } = DefaultFollowerScanSwipes;
    public const int DefaultFollowerScanSwipes = 12;
    /// <summary>Techo de pantallas por lectura (unos 5 minutos de celular): más abajo no se llega.</summary>
    public const int MaxFollowerScanSwipes = 60;

    // ── Salud ──────────────────────────────────────────────────────────────────────────────

    /// <summary>Día (Argentina) en que el celular confirmó que su Instagram está en <see cref="AccountUsername"/>.</summary>
    public DateOnly? AccountVerifiedOn { get; set; }

    /// <summary>Fallas seguidas del celular que no son culpa del destinatario: a la tercera se frena.</summary>
    public int ConsecutiveFailures { get; set; }

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
