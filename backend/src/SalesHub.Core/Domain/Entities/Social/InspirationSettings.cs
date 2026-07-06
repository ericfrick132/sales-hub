namespace SalesHub.Core.Domain.Entities.Social;

/// <summary>
/// Config global (una sola fila) del intake de inspiraciones por WhatsApp:
/// el número MAESTRO (el del usuario) manda imágenes/ideas a la línea
/// <see cref="InstanceName"/> y el bot le repregunta para qué tema/app son.
/// Si no hay línea o número maestro elegidos, el intake queda apagado (fail-safe).
/// </summary>
public class InspirationSettings
{
    /// <summary>Fila única (Id = 1).</summary>
    public int Id { get; set; } = 1;

    public bool Enabled { get; set; }

    /// <summary>Instancia de Evolution que escucha (la línea de WhatsApp). null = apagado.</summary>
    public string? InstanceName { get; set; }

    /// <summary>Número maestro autorizado a mandar inspiraciones (solo ese número). null = apagado.</summary>
    public string? MasterPhone { get; set; }

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
