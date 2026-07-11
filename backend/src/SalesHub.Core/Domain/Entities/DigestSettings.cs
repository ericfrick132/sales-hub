namespace SalesHub.Core.Domain.Entities;

/// <summary>
/// Config global (una sola fila) del resumen diario por WhatsApp: a qué hora (Argentina)
/// se manda, por qué línea (instancia de Evolution) y a qué número. Si no hay línea
/// elegida, el resumen queda apagado (fail-safe). Si <see cref="Phone"/> está vacío se
/// usa el número maestro de inspiraciones (<see cref="Social.InspirationSettings.MasterPhone"/>).
/// </summary>
public class DigestSettings
{
    /// <summary>Fila única (Id = 1).</summary>
    public int Id { get; set; } = 1;

    public bool Enabled { get; set; }

    /// <summary>Instancia de Evolution por la que se manda el resumen. null = sin elegir.</summary>
    public string? InstanceName { get; set; }

    /// <summary>Número destino (solo dígitos). null = usar el número maestro de inspiraciones.</summary>
    public string? Phone { get; set; }

    /// <summary>Hora local Argentina (0-23) a la que se manda el resumen.</summary>
    public int SendHour { get; set; } = 21;

    /// <summary>Último envío automático exitoso: dedup de un envío por día que sobrevive reinicios.</summary>
    public DateTimeOffset? LastSentAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
