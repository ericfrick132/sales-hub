namespace SalesHub.Core.Domain.Entities;

/// <summary>
/// Config (una sola fila) de la campaña de remarketing: el bot le escribe a leads que ya nos
/// hablaron y se enfriaron, para coordinar una llamada. El tope es POR LÍNEA y POR DÍA (hora
/// Argentina) para no quemar el número: a WhatsApp un chat de hace meses le parece casi frío.
/// Se edita en /remarketing.
/// </summary>
public class RemarketingSettings
{
    /// <summary>Fila única (Id = 1).</summary>
    public int Id { get; set; } = 1;

    /// <summary>Arranca apagada: prenderla es una decisión explícita (post-ban 30/07).</summary>
    public bool Enabled { get; set; }

    /// <summary>Mensajes de remarketing por línea (vendedor que manda) por día.</summary>
    public int PerLinePerDay { get; set; } = 15;

    /// <summary>Días mínimos sin actividad en la charla para entrar a la cola.</summary>
    public int MinIdleDays { get; set; } = 14;

    /// <summary>Tope de antigüedad (días desde el último mensaje). null = sin tope.</summary>
    public int? MaxIdleDays { get; set; }

    /// <summary>Ventana de envío, hora Argentina [start, end).</summary>
    public int SendHourStart { get; set; } = 10;
    public int SendHourEnd { get; set; } = 19;

    /// <summary>
    /// Por qué vendedores (líneas) sale. El lead pasa a ser de ese vendedor: el dueño original
    /// muchas veces ya no está, y el que coordina la llamada es el que escribió.
    /// </summary>
    public List<Guid> SenderSellerIds { get; set; } = new();

    /// <summary>Productos incluidos. Vacío = todos.</summary>
    public List<string> ProductKeys { get; set; } = new();

    /// <summary>
    /// Si true, Claude lee la charla, descarta clientes/proveedores y personaliza el opener. Apagado por
    /// defecto: la etapa se detecta por palabras y sale el opener fijo (mínima IA).
    /// </summary>
    public bool PersonalizeWithAi { get; set; }

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// Un mensaje de remarketing encolado a un lead. Es el contador del tope diario por línea,
/// el candado de "a este ya le escribimos" (nunca dos veces) y la base de la métrica de
/// respuesta por etapa.
/// </summary>
public class RemarketingAttempt
{
    public Guid Id { get; set; }

    public Guid LeadId { get; set; }
    public Lead? Lead { get; set; }

    /// <summary>Vendedor (línea) por el que sale.</summary>
    public Guid SellerId { get; set; }

    /// <summary>Etapa en la que se había enfriado la charla (ver RemarketingService.Stages).</summary>
    public string Stage { get; set; } = string.Empty;

    public int Score { get; set; }

    public string Message { get; set; } = string.Empty;
    public bool PersonalizedWithAi { get; set; }

    public Guid? OutboxId { get; set; }

    public DateTimeOffset EnqueuedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Primera respuesta del lead después de este intento.</summary>
    public DateTimeOffset? RepliedAt { get; set; }
}
