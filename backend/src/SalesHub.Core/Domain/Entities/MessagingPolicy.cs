using System.ComponentModel.DataAnnotations;

namespace SalesHub.Core.Domain.Entities;

/// <summary>
/// Qué tipo de mensajes automáticos tiene permitido mandar el sistema para cada ORIGEN de
/// lead (ver <see cref="MessagingSourceGroups"/>). Es más fino que los flags de runner:
/// el flag "whatsapp" apaga TODO (envíos + respuestas del bot); esto permite, por ejemplo,
/// cortar los mensajes nuevos y seguir respondiendo sólo a los de Meta Lead Ads.
///
/// Si no hay fila para un grupo, TODO está permitido — así el comportamiento por defecto
/// (y el de una base sin migrar la config) es el histórico.
///
/// Los gates son de ENVÍO, no de encolado: lo que quedó en el outbox se mantiene
/// Scheduled y sale solo cuando el switch se vuelve a prender.
/// </summary>
public class MessagingPolicy
{
    /// <summary>Clave del grupo de origen (<see cref="MessagingSourceGroups"/>).</summary>
    [Key]
    [MaxLength(40)]
    public string SourceGroup { get; set; } = string.Empty;

    /// <summary>Primer contacto: el opener/step 0 a un lead que nunca recibió nada nuestro.</summary>
    public bool AllowOutreach { get; set; } = true;

    /// <summary>
    /// Seguimiento proactivo a leads YA contactados: pasos siguientes de la cadencia,
    /// re-enganches por silencio y nudges post-alta.
    /// </summary>
    public bool AllowFollowup { get; set; } = true;

    /// <summary>El bot lee lo que escribe el lead y contesta (o deja la sugerencia).</summary>
    public bool AllowReply { get; set; } = true;

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
