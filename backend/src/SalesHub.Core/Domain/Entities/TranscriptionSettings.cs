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

    /// <summary>
    /// Modo batch: en vez de transcribir cada audio suelto, junta TODO lo que el número
    /// autorizado manda seguido (imágenes + texto + audios) dentro de una ventana de
    /// <see cref="BatchWindowSeconds"/> segundos sin actividad, y responde un único PDF
    /// (imágenes en orden → texto → transcripción de los audios). Pensado para mandar un
    /// requerimiento de cambio completo en varios mensajes y recibirlo armado en un PDF.
    /// Si está apagado, sigue el relay clásico audio→texto.
    /// </summary>
    public bool BatchMode { get; set; }

    /// <summary>Segundos de inactividad entre mensajes para cerrar el batch (default 10).</summary>
    public int BatchWindowSeconds { get; set; } = 10;

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
