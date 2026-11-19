using SalesHub.Core.Domain.Enums;

namespace SalesHub.Core.Domain.Entities;

public class Lead
{
    public Guid Id { get; set; }

    public string ProductKey { get; set; } = string.Empty;
    public Product? Product { get; set; }

    public LeadSource Source { get; set; }
    public string? ExternalId { get; set; }
    public string? PlaceId { get; set; }

    public string Name { get; set; } = string.Empty;
    public string? Address { get; set; }
    public string? City { get; set; }
    public string? Province { get; set; }
    public string Country { get; set; } = "AR";

    public string? RawPhone { get; set; }
    public string? WhatsappPhone { get; set; }
    public string? WhatsappJid { get; set; }
    public bool WhatsappValidated { get; set; }

    public string? Website { get; set; }
    public string? InstagramHandle { get; set; }
    /// <summary>
    /// Teléfono sacado del perfil de Instagram del negocio (link wa.me, botón de contacto o número
    /// en la bio), normalizado. Lo carga la captura de Maps además del teléfono de Maps.
    /// </summary>
    public string? InstagramPhone { get; set; }
    /// <summary>DM en frío por Instagram (userscript): cuándo se lo dio a la extensión, cuándo salió y por qué falló.</summary>
    public DateTimeOffset? IgDmClaimedAt { get; set; }
    public DateTimeOffset? IgDmSentAt { get; set; }
    public string? IgDmError { get; set; }
    /// <summary>
    /// IGSID (Instagram-scoped id) del contacto que nos escribió por DM a una cuenta propia de IG
    /// (Messenger Platform). Es a quién se le contesta por la Graph API; no es un teléfono.
    /// </summary>
    public string? InstagramScopedId { get; set; }
    public string? FacebookUrl { get; set; }

    public double? Rating { get; set; }
    public int? TotalReviews { get; set; }
    public double? Latitude { get; set; }
    public double? Longitude { get; set; }
    public string? BusinessStatus { get; set; }
    public List<string> Types { get; set; } = new();

    public string? SearchQuery { get; set; }
    public string? SearchCategory { get; set; }
    public string? RawDataJson { get; set; }

    /// <summary>
    /// Rubro del negocio según el nomenclador de ARCA (código CLAE F.883 de 6 dígitos, ver
    /// <see cref="ArcaActivity"/>). Lo pone <c>ArcaLeadClassifier</c> a partir de la categoría de
    /// búsqueda, los tipos de Google, el nombre o el producto; null = no se pudo deducir.
    /// </summary>
    public string? ArcaActivityCode { get; set; }

    /// <summary>
    /// Qué regla le puso el rubro, para poder auditarlo: "categoria:gimnasio", "tipo:gym",
    /// "nombre:pilates", "busqueda:...", "producto:gymhero", "sin-match" o "manual"
    /// (elegido a mano: el clasificador no lo vuelve a tocar).
    /// </summary>
    public string? ArcaActivityRule { get; set; }

    /// <summary>Versión de las reglas con que se clasificó; al cambiar las reglas se reclasifica solo.</summary>
    public string? ArcaRulesVersion { get; set; }

    public string? LocalityGid2 { get; set; }
    public Locality? Locality { get; set; }

    /// <summary>
    /// Score de la CONVERSACIÓN (lo pone el agente al analizar la charla, 0-100) y la prioridad en
    /// la cola de re-enganche. No mide qué tan bueno es el negocio: eso es <see cref="ProspectScore"/>.
    /// </summary>
    public int Score { get; set; }

    /// <summary>
    /// Puntaje de PROSPECTO (0-100): qué tan buen cliente es o puede llegar a ser el negocio, antes de
    /// hablarle — zona, reseñas de Google Maps, web/Instagram, de dónde salió el número, rubro y si ya
    /// usa un sistema de la competencia. Lo calcula <c>ProspectScorer</c>; con pocos envíos (línea
    /// outbound con tope diario) se le escribe solo a los de arriba. null = todavía no se calculó.
    /// </summary>
    public int? ProspectScore { get; set; }

    /// <summary>El desglose del puntaje (JSON: criterio, puntos, máximo y por qué), para mostrarlo y auditarlo.</summary>
    public string? ProspectScoreDetail { get; set; }
    public DateTimeOffset? ProspectScoredAt { get; set; }

    /// <summary>Rubro detectado para el puntaje y el mensaje (clave de ProspectScorer.Rubros: "unas", "psicologia"...).</summary>
    public string? ProspectRubro { get; set; }

    /// <summary>Cuándo se "nutrió" el lead para el puntaje (se leyó su web, se cruzó con la competencia).</summary>
    public DateTimeOffset? ProspectEnrichedAt { get; set; }

    /// <summary>
    /// Sistema de turnos/reservas de la competencia que ya usa (AgendaPro, Fresha, Booksy...), detectado en
    /// su web o en los listados de la competencia. null = no se le encontró ninguno.
    /// </summary>
    public string? BookingPlatform { get; set; }

    /// <summary>
    /// De dónde salió el número: "whatsapp-publicado" (link wa.me en su web), "whatsapp-instagram" (wa.me
    /// en la bio o los links de su Instagram, o la bio dice que los turnos son por WhatsApp), "maps-celular",
    /// "maps-fijo", "web", "instagram". Los dos "whatsapp-*" son los únicos donde "vi que toman los turnos
    /// por WhatsApp" está verificado.
    /// </summary>
    public string? PhoneOrigin { get; set; }

    /// <summary>Cuándo se leyó su perfil de Instagram (con la sesión del Chrome de Eric). null = nunca.</summary>
    public DateTimeOffset? InstagramCheckedAt { get; set; }
    public int? InstagramFollowers { get; set; }

    /// <summary>El link de la bio de Instagram (wa.me, Linktree, AgendaPro...), para mostrar y auditar.</summary>
    public string? InstagramBioLink { get; set; }

    public Guid? SellerId { get; set; }
    public Seller? Seller { get; set; }
    public DateTimeOffset? AssignedAt { get; set; }

    public LeadStatus Status { get; set; } = LeadStatus.New;

    public string? RenderedMessage { get; set; }
    public string? WhatsappLink { get; set; }

    public DateTimeOffset? QueuedAt { get; set; }
    public DateTimeOffset? SentAt { get; set; }
    public DateTimeOffset? FirstReplyAt { get; set; }
    public DateTimeOffset? DemoScheduledAt { get; set; }
    public DateTimeOffset? ClosedAt { get; set; }

    public string? Notes { get; set; }

    // Respuesta sugerida por la IA para el próximo turno (modo asistido). Se
    // regenera en cada inbound nuevo y se limpia cuando el vendedor responde.
    public string? AiSuggestedReply { get; set; }
    public DateTimeOffset? AiSuggestedReplyAt { get; set; }

    // Re-enganche automático: cuántos "nudges" de seguimiento le mandó el bot
    // cuando se quedó callado, y cuándo fue el último (para capear y espaciar).
    public int NudgeCount { get; set; }
    public DateTimeOffset? LastNudgeAt { get; set; }

    /// <summary>
    /// Takeover humano: si tiene valor, el bot NO responde ni re-engancha en esta
    /// conversación. Se setea mandando "-" desde el celu (o automáticamente al
    /// detectar un mensaje manual), se limpia con "+" o desde la UI.
    /// </summary>
    public DateTimeOffset? BotMutedAt { get; set; }

    /// <summary>
    /// Cuándo avisamos al vendedor de que este lead está CALIENTE (señal de compra fuerte:
    /// mandó audio + interés claro) para que tome la charla con un audio personal — el patrón
    /// que cierra ventas. Dedup: no re-avisamos hasta que pase la ventana de cooldown.
    /// </summary>
    public DateTimeOffset? HotAlertedAt { get; set; }

    /// <summary>
    /// Cuándo avisamos que este chat se pasó del SLA de respuesta (el lead escribió y
    /// nadie contestó a tiempo). Dedup: guarda el momento del mensaje sin responder que
    /// disparó el aviso, así una ráfaga nueva vuelve a alertar pero la misma no repite.
    /// </summary>
    public DateTimeOffset? SlaAlertedAt { get; set; }

    /// <summary>
    /// CRM: próxima acción comprometida ("llamarlo el martes"). Es lo que ordena el día
    /// del que vende — sin esto un lead avanza de etapa y después nadie se acuerda de él.
    /// </summary>
    public DateTimeOffset? NextActionAt { get; set; }

    /// <summary>Qué hay que hacer en esa próxima acción (texto libre y corto).</summary>
    public string? NextActionNote { get; set; }

    /// <summary>
    /// Cuándo un admin eligió a mano el vendedor de este lead. Mientras tenga valor, el
    /// reparto automático (rebalanceo, "Reasignar todo") no se lo saca: sin esto, un lead
    /// sin contactar asignado a mano volvía al pool o a otra línea en el próximo tick.
    /// Se limpia cuando el lead se suelta al pool.
    /// </summary>
    public DateTimeOffset? ManualAssignedAt { get; set; }

    /// <summary>
    /// Vendedor que originó el lead cuando después lo tomó otro: la cold caller que lo llamó,
    /// coordinó la demo y se lo pasó al que la da (ver <see cref="Seller.DemoHandoffSellerId"/>).
    /// Los ganados y los objetivos se le cuentan a este, no al dueño actual. null = el lead es
    /// de quien lo tiene.
    /// </summary>
    public Guid? OriginSellerId { get; set; }

    /// <summary>Tags libres del inbox (ej. "respondio", "caliente"). Los pone el pitch o el humano.</summary>
    public List<string> Tags { get; set; } = new();
    /// <summary>Atribución del anuncio (externalAdReply de WhatsApp en un click-to-WhatsApp).</summary>
    public string? AdId { get; set; }
    public string? AdTitle { get; set; }
    public string? AdSourceUrl { get; set; }
    public string? CtwaClid { get; set; }
    /// <summary>Último mensaje ENTRANTE: define la ventana de 24 h para responder (estilo Meta).</summary>
    public DateTimeOffset? LastInboundAt { get; set; }
    /// <summary>Conversación cerrada desde el inbox (se oculta hasta que el lead vuelva a escribir).</summary>
    public DateTimeOffset? ConversationClosedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Último pedido de demo/llamada desde el formulario de una landing (precios o popup de salida).
    /// Un lead que ya existía y vuelve a pedir queda con la fecha del pedido nuevo: es lo que ordena
    /// la vista "Pedidos web" del CRM y desde cuándo corre la promesa de llamar en 10 minutos.
    /// </summary>
    public DateTimeOffset? WebRequestAt { get; set; }

    /// <summary>El último pedido web tal como llegó (nombre, mail, negocio, plan, página), sin el honeypot.</summary>
    public string? WebRequestJson { get; set; }
}
