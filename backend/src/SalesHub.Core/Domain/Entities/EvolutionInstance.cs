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

    public InstanceStatus Status { get; set; } = InstanceStatus.Disconnected;
    public DateTimeOffset? LastStatusCheckAt { get; set; }
    public DateTimeOffset? ConnectedAt { get; set; }
    public DateTimeOffset? DisconnectedAt { get; set; }

    public string? LastQrCodeBase64 { get; set; }
    public DateTimeOffset? QrCodeGeneratedAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
