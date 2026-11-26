namespace SalesHub.Infrastructure.Options;

/// <summary>
/// Conversions API de Meta: el CRM le avisa a Meta cuando un lead de anuncio queda calificado y cuando
/// agenda la demo, para que la entrega de los anuncios aprenda a quién conviene traer. Sección <c>MetaCapi</c>.
///
/// No hace falta ninguna variable nueva en el servidor: el token es el mismo que ya lee los anuncios
/// (<c>Meta:AdsAccessToken</c>) y el pixel de cada app va acá, en appsettings.json. Solo si ese token no
/// tuviera permiso sobre el pixel habría que cargar uno propio (<c>MetaCapi__AccessToken</c>).
/// </summary>
public class MetaCapiOptions
{
    /// <summary>
    /// Interruptor general. Lo pisa el flag <c>meta-capi</c> del Centro de control (si tiene fila). Aun
    /// prendido, una app solo manda si tiene pixel acá y hay token.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Token para mandar eventos. Vacío = <c>Meta:AdsAccessToken</c> (el de la lectura de anuncios).</summary>
    public string? AccessToken { get; set; }

    /// <summary>Versión de la Graph API. Vacío = <c>Meta:GraphVersion</c> (o v21.0).</summary>
    public string? GraphVersion { get; set; }

    /// <summary>
    /// Código de "Probar eventos" del Administrador de eventos. Con valor, TODOS los envíos salen con ese
    /// código. Para una prueba suelta conviene <c>POST /api/meta-capi/test</c>, que lo recibe en el pedido.
    /// Ojo: según Meta, los eventos con test_event_code no se descartan (cuentan para medición).
    /// </summary>
    public string? TestEventCode { get; set; }

    /// <summary>Tope de cada llamada a Meta. Corto: el aviso corre en segundo plano y nunca frena la charla.</summary>
    public int TimeoutSeconds { get; set; } = 8;

    public EventNames Events { get; set; } = new();

    /// <summary>Pixel (dataset) de cada app, por productKey. Una app sin pixel no manda nada.</summary>
    public Dictionary<string, ProductSettings> Products { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public class EventNames
    {
        /// <summary>
        /// Calificado. "QualifiedLead" está en la lista de eventos de la Conversions API for Business
        /// Messaging; por system_generated viaja con el mismo nombre (evento personalizado).
        /// </summary>
        public StageNames Qualified { get; set; } = new() { Messaging = "QualifiedLead", System = "QualifiedLead" };

        /// <summary>
        /// Demo o llamada agendada. La lista de eventos de mensajería de Meta no tiene uno de turnos, así que
        /// va solo como "Schedule" (evento estándar, el mismo que manda la landing cuando agendan en la web)
        /// por system_generated. Para probar business_messaging, cargar un nombre en Messaging.
        /// </summary>
        public StageNames DemoScheduled { get; set; } = new() { Messaging = "", System = "Schedule" };
    }

    public class StageNames
    {
        /// <summary>Nombre del evento como business_messaging (solo con ctwa_clid). Vacío = no se intenta por mensajería.</summary>
        public string Messaging { get; set; } = string.Empty;
        /// <summary>Nombre del evento como system_generated. Vacío = esa etapa no sale por ese camino.</summary>
        public string System { get; set; } = string.Empty;
    }

    public class ProductSettings
    {
        /// <summary>Id del dataset (pixel) de la app.</summary>
        public string PixelId { get; set; } = string.Empty;
        /// <summary>Página de Facebook dueña del WhatsApp de los anuncios (va como page_id en business_messaging).</summary>
        public string? PageId { get; set; }
        /// <summary>Cuenta de WhatsApp Business (solo si el número está en la plataforma oficial). Opcional.</summary>
        public string? WhatsAppBusinessAccountId { get; set; }
    }
}
