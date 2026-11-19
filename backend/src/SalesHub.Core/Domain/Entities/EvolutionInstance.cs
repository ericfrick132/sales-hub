using SalesHub.Core.Domain.Enums;

namespace SalesHub.Core.Domain.Entities;

public class EvolutionInstance
{
    public Guid Id { get; set; }

    /// <summary>Dueño humano de la línea (vendedor). null si es una línea de APP.</summary>
    public Guid? SellerId { get; set; }
    public Seller? Seller { get; set; }

    /// <summary>App PRINCIPAL de la línea (ej. "gymhero"). Es el fallback: con qué producto
    /// se taggea un chat cuando el texto no delata de cuál viene. null si es línea de vendedor.</summary>
    public string? ProductKey { get; set; }

    /// <summary>
    /// Las OTRAS apps que atiende este mismo número. Un celu suele recibir consultas de
    /// varios productos: si el mensaje nombra alguna de estas, el chat se taggea con esa;
    /// si no nombra ninguna, cae a <see cref="ProductKey"/>.
    /// </summary>
    public List<string> ExtraProductKeys { get; set; } = new();

    public string InstanceName { get; set; } = string.Empty;

    /// <summary>
    /// Nombre con el que se reconoce el teléfono en /devices y en el reporte diario de leads
    /// (ej. "Celu Rosario"). null en las líneas viejas: se muestra el número o el nombre de
    /// la instancia.
    /// </summary>
    public string? Label { get; set; }
    public string? ConnectedPhoneNumber { get; set; }

    /// <summary>
    /// Línea de SOLO ESCUCHA: entra todo lo que recibe (queda centralizado en
    /// Conversaciones) pero no sale NADA por ella — ni cadencia, ni bot, ni respuesta
    /// manual. Es un candado a nivel del cliente de WhatsApp, no una convención: existe
    /// para poder vincular un número y trackear sin arriesgar un ban por envío.
    /// </summary>
    public bool ListenOnly { get; set; }

    /// <summary>
    /// Proxy de salida de ESTA línea (1 IP por número). Formato: <c>scheme://user:pass@host:port</c>
    /// o <c>host:port:user:pass</c>. null = usa el proxy global (Evolution:ProxyUrl) o ninguno.
    /// Se aplica al crear/asegurar la instancia (Evolution POST /proxy/set).
    /// </summary>
    public string? ProxyUrl { get; set; }

    /// <summary>
    /// Al vincular el teléfono, cargar también los chats de ANTES del escaneo (el historial que
    /// WhatsApp le pasa al dispositivo vinculado y Evolution guarda). Lo elige quien lo agrega.
    /// </summary>
    public bool ImportHistory { get; set; }

    /// <summary>
    /// Pasadas de importación hechas desde la última vinculación: el historial llega en tandas
    /// después de escanear, así que va una a los pocos minutos y otra a la media hora. Vuelve a 0
    /// al desvincular o al prender <see cref="ImportHistory"/>.
    /// </summary>
    public int HistoryImportPasses { get; set; }
    public DateTimeOffset? HistoryImportStartedAt { get; set; }
    public DateTimeOffset? HistoryImportedAt { get; set; }

    /// <summary>Mensajes nuevos que entraron por la importación (suma de las pasadas).</summary>
    public int HistoryImportedMessages { get; set; }

    /// <summary>Chats a recorrer y chats ya recorridos en la pasada en curso: la barra de progreso.</summary>
    public int HistoryImportTotalChats { get; set; }
    public int HistoryImportDoneChats { get; set; }

    /// <summary>Prospectos que dio de alta la importación de este teléfono (suma de las pasadas).</summary>
    public int HistoryImportProspects { get; set; }

    /// <summary>
    /// Vendedores que se reparten los PROSPECTOS del historial de este teléfono (round-robin
    /// estable por número: el mismo contacto siempre cae en el mismo). Cada chat 1:1 que carga
    /// la importación se convierte en un lead de origen <see cref="LeadSource.Remarketing"/>
    /// asignado a uno de ellos y listo para llamar desde el CRM.
    /// Vacío = comportamiento viejo (el chat entra a Conversaciones como WhatsApp entrante y
    /// lo agarra el dueño de la app).
    /// </summary>
    public List<Guid> ProspectSellerIds { get; set; } = new();

    /// <summary>
    /// Filtro de basura: si el nombre del contacto o alguno de sus mensajes contiene alguna de
    /// estas palabras, ese chat NO se carga (ni lead ni mensajes). Sirve para dejar afuera
    /// delivery, bancos, familia, etc. Se compara sin tildes y sin distinguir mayúsculas.
    /// </summary>
    public List<string> ProspectSkipWords { get; set; } = new();

    /// <summary>
    /// Línea OUTBOUND: apenas se escanea el QR le escribe sola a los mejores leads del CRM (por
    /// <see cref="Lead.ProspectScore"/>), hasta este tope de CONTACTOS EN TOTAL Y PARA SIEMPRE (no por
    /// día). 0 = no es outbound. El contador es la tabla outbound_contacts: desvincular o volver a
    /// escanear no lo resetea; para mandar más se agrega otro teléfono.
    /// </summary>
    public int OutboundCap { get; set; }

    /// <summary>Puntaje mínimo de prospecto para que la línea outbound le escriba a un lead.</summary>
    public int OutboundMinScore { get; set; } = 50;

    /// <summary>Rubros a los que les escribe (claves de ProspectScorer.Rubros: "unas", "pestanas"...). Vacío = todos.</summary>
    public List<string> OutboundRubros { get; set; } = new();

    /// <summary>Nombre de pila con el que se presenta el mensaje ("soy mateo de TurnosPro"). Sin nombre no manda.</summary>
    public string? OutboundSenderName { get; set; }

    /// <summary>Mensaje de apertura. null = el de siempre (OutboundService.DefaultTemplate).</summary>
    public string? OutboundTemplate { get; set; }

    /// <summary>Outbound en pausa (no manda nada nuevo hasta que se reanude).</summary>
    public DateTimeOffset? OutboundPausedAt { get; set; }

    public InstanceStatus Status { get; set; } = InstanceStatus.Disconnected;
    public DateTimeOffset? LastStatusCheckAt { get; set; }
    public DateTimeOffset? ConnectedAt { get; set; }
    public DateTimeOffset? DisconnectedAt { get; set; }

    /// <summary>
    /// Última vez que el monitor vio la línea conectada EN VIVO (connectionState = open). Es lo que
    /// habilita al vigilante a reconectarla sola: solo se reintenta una línea que estuvo viva hace
    /// poco, nunca una vieja o abandonada. null al cerrar sesión a propósito desde sales.
    /// </summary>
    public DateTimeOffset? LastSeenOpenAt { get; set; }

    /// <summary>Último <c>instance/connect</c> que mandó el vigilante (para espaciar los reintentos).</summary>
    public DateTimeOffset? LastReconnectAttemptAt { get; set; }

    /// <summary>
    /// El vigilante intentó reconectar y Evolution devolvió un QR: WhatsApp desvinculó el
    /// dispositivo y hace falta escanear de nuevo. Mientras esté seteado no se reintenta.
    /// </summary>
    public DateTimeOffset? NeedsQrSince { get; set; }

    /// <summary>Ya se avisó al maestro que esta línea está caída (una vez por caída; se limpia al volver).</summary>
    public DateTimeOffset? DownAlertedAt { get; set; }

    public string? LastQrCodeBase64 { get; set; }
    public DateTimeOffset? QrCodeGeneratedAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
