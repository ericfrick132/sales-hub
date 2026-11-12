namespace SalesHub.Core.Domain.Entities;

/// <summary>
/// Seguidor de la cuenta de Instagram de una app, leído por el celular desde la lista de
/// seguidores. Es la cola de "escribirles a los seguidores": cuando el DM sale, queda enganchado
/// al lead que se crea (o al que ya existía con ese @usuario).
/// </summary>
public class IgFollower
{
    public Guid Id { get; set; }

    public string ProductKey { get; set; } = string.Empty;

    /// <summary>@usuario en minúsculas y sin arroba.</summary>
    public string Username { get; set; } = string.Empty;

    public DateTimeOffset FirstSeenAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset LastSeenAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Posición en la lista la última vez que se lo vio (0 = arriba de todo, el más nuevo).</summary>
    public int Rank { get; set; }

    /// <summary>Cuándo se le dio al celular; si no reporta, vuelve a la cola.</summary>
    public DateTimeOffset? ClaimedAt { get; set; }

    public DateTimeOffset? DmSentAt { get; set; }

    /// <summary>Por qué falló el DM (perfil que no existe, no acepta mensajes, envío sin confirmar).</summary>
    public string? Error { get; set; }

    /// <summary>Por qué no se le escribe: ya es cliente, ya tiene una charla, está excluido.</summary>
    public string? SkippedReason { get; set; }

    public Guid? LeadId { get; set; }
}
