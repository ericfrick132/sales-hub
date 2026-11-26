namespace SalesHub.Core.Domain.Entities;

/// <summary>
/// Aviso a Meta (Conversions API) de que un lead de anuncio llegó a una etapa del embudo ("qualified",
/// "demo_scheduled"). Una fila por lead + etapa (+ una aparte para la prueba con test_event_code): es lo
/// que garantiza que cada etapa sale una sola vez, y deja ver qué contestó Meta en cada envío.
/// No guarda el token ni el teléfono o el mail del lead (viajan con hash y no quedan acá).
/// </summary>
public class MetaConversionEvent
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid LeadId { get; set; }
    public string ProductKey { get; set; } = string.Empty;

    /// <summary>"qualified" o "demo_scheduled".</summary>
    public string Stage { get; set; } = string.Empty;

    /// <summary>Envío de prueba (POST /api/meta-capi/test, con test_event_code). No ocupa la fila del envío real.</summary>
    public bool IsTest { get; set; }

    /// <summary>Nombre del evento con el que salió (el del último intento).</summary>
    public string EventName { get; set; } = string.Empty;

    /// <summary>event_id estable por lead y etapa ("lead-{id}-qualified"): la prueba y el real usan el mismo, Meta los deduplica.</summary>
    public string EventId { get; set; } = string.Empty;

    /// <summary>action_source del último intento (business_messaging o system_generated). null = no llegó a salir.</summary>
    public string? ActionSource { get; set; }

    /// <summary>pending (en curso) · sent (Meta lo aceptó) · failed (rechazado o sin respuesta) · skipped (no había con qué identificarlo).</summary>
    public string Status { get; set; } = "pending";

    /// <summary>Código HTTP del último intento. null = no hubo respuesta (timeout, red).</summary>
    public int? HttpStatus { get; set; }

    /// <summary>Qué contestó Meta cuando no lo aceptó (o por qué no salió), acortado y sin el token.</summary>
    public string? Error { get; set; }

    /// <summary>Meta rechazó el intento como business_messaging y se reintentó como system_generated.</summary>
    public bool FallbackUsed { get; set; }

    /// <summary>Respuesta de Meta al intento rechazado (el de business_messaging), cuando hubo reintento.</summary>
    public int? FirstHttpStatus { get; set; }
    public string? FirstError { get; set; }

    /// <summary>Con qué se identificó al lead en el último intento ("ctwa_clid,page_id,ph"): los nombres, nunca los valores.</summary>
    public string? MatchKeys { get; set; }

    /// <summary>fbtrace_id de la respuesta de Meta (para rastrear un envío con soporte de Meta).</summary>
    public string? FbTraceId { get; set; }

    /// <summary>De dónde salió el aviso: bot, crm, test, retry.</summary>
    public string? Trigger { get; set; }

    /// <summary>Cuántas veces se le pegó a Meta por esta fila (suma los reintentos).</summary>
    public int Attempts { get; set; }

    /// <summary>Cuándo llegó el lead a la etapa: es el event_time que se le manda a Meta (la prueba usa la hora del envío).</summary>
    public DateTimeOffset OccurredAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Cuándo lo aceptó Meta.</summary>
    public DateTimeOffset? SentAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
