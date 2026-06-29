namespace SalesHub.Core.Domain.Entities;

public class Product
{
    public Guid Id { get; set; }

    public string ProductKey { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public bool Active { get; set; } = true;

    public string Country { get; set; } = "AR";
    public string CountryName { get; set; } = "Argentina";
    public string RegionCode { get; set; } = "ar";
    public string Language { get; set; } = "es";
    public string PhonePrefix { get; set; } = "54";

    public List<string> Categories { get; set; } = new();
    // Plantillas de respuesta rápida que el seller ve como dropdown en el chat.
    // Cada string es un texto listo para mandar (sin placeholders por ahora).
    public List<string> ReplyTemplates { get; set; } = new();
    // Cadencia de outreach inicial. Cuando el lead se asigna y queda Queued,
    // se enqueuean estos N pasos en orden con su delay relativo al anterior.
    // Se cortan automáticamente si el lead responde antes de que se envíen
    // todos. Si está vacío, fallback al MessageTemplate + OpenerTemplate
    // legacy (compat con productos viejos).
    public List<MessageStep> MessageSteps { get; set; } = new();
    // Overrides por categoría de búsqueda (lead.SearchCategory). Si una
    // categoría tiene override con steps configurados, esos steps se usan
    // en lugar del MessageSteps default. Si no hay override, cae al default.
    // Útil para que "yoga" tenga audios y textos distintos a "gimnasio"
    // dentro del mismo producto.
    public List<CategoryCadence> CategoryCadences { get; set; } = new();
    public string MessageTemplate { get; set; } = string.Empty;
    // Mensaje "opener" opcional. Si está, se manda primero (ej. "buenas") y el
    // mensaje principal sale después con el delay normal del seller. Vacío = un solo mensaje.
    public string OpenerTemplate { get; set; } = string.Empty;

    public string CheckoutUrl { get; set; } = string.Empty;
    public string PriceDisplay { get; set; } = string.Empty;

    public int DailyLimit { get; set; } = 60;
    public List<int> TriggerHours { get; set; } = new();
    // Ventana de horario en la que se permite enviar para este producto (0-24,
    // hora local del seller). Default 0/24 = sin restricción a nivel producto;
    // queda solo la del seller. Si Start>=End el sistema asume sin restricción.
    public int SendHourStart { get; set; } = 0;
    public int SendHourEnd { get; set; } = 24;

    // Cap of NEW leads per day from the free Google Places pipeline. 0 = no per-product cap
    // (only the global Google:PlacesDailyCap of runs/day applies).
    public int GooglePlacesDailyLeadCap { get; set; } = 60;

    public bool RequiresAssistedSale { get; set; } = false;

    // Piloto automático: si está en true, el agente de IA RESPONDE SOLO por WhatsApp
    // (auto-envía la respuesta y re-engancha al lead que se queda callado) en vez de
    // dejar una sugerencia para que el vendedor la mande. Off por default — control
    // total, se prende por producto y se puede cortar al toque.
    public bool AutoPilot { get; set; } = true;

    // Re-enganche proactivo automático: con AutoPilot + AutoReengage en true, el bot
    // ADEMÁS de responder inbound manda los nudges de re-enganche solo (capeados por el
    // límite diario del vendedor). Apagado por default — responder inbound es de bajo
    // riesgo (como una persona), pero los mensajes proactivos son los que más banean,
    // así que se opta-in por producto.
    public bool AutoReengage { get; set; } = true;

    // Transporte gestionado por la APP: si true, sales-hub NO manda el WhatsApp de este
    // producto por Evolution. Deja los mensajes en el outbox para que la app los baje
    // (GET /api/hub/outbound), los mande por SU propia Evolution y ackee (/hub/outbound/ack).
    // El pacing/cap y el cerebro (IA/onboarding) se quedan en sales-hub. Default false =
    // sales-hub manda como siempre (no rompe nada).
    public bool AppManagedTransport { get; set; } = false;

    // Instrucciones de venta para el agente de IA que sugiere respuestas en
    // Conversaciones (tono, objeciones comunes, cuándo mandar precio/checkout).
    // Texto libre por vertical. Vacío = el agente usa solo las instrucciones base.
    public string AiSalesPlaybook { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    public ICollection<Lead> Leads { get; set; } = new List<Lead>();
}
