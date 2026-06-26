namespace SalesHub.Core.Domain.Entities;

/// <summary>
/// Config global (una sola fila) del relay de transcripción: on/off y a qué línea
/// (instancia de Evolution) escucha. El relay sólo se dispara para audios que llegan
/// a <see cref="InstanceName"/> y de un número de la allowlist (<see cref="TranscriptionPhone"/>).
/// Si no hay línea elegida, el relay queda apagado (fail-safe).
/// </summary>
public class TranscriptionSettings
{
    /// <summary>Fila única (Id = 1).</summary>
    public int Id { get; set; } = 1;

    public bool Enabled { get; set; }

    /// <summary>Nombre de la instancia de Evolution habilitada (la línea de WhatsApp). null = sin elegir.</summary>
    public string? InstanceName { get; set; }

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
