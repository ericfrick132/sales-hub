namespace SalesHub.Core.Domain.Entities;

/// <summary>
/// Anuncio de Meta sincronizado (MetaAdsSyncService). Cada anuncio tiene una intención distinta y
/// un mensaje pre-armado distinto ("Tengo una barbería y quiero ver cómo TurnosPro contesta mi
/// WhatsApp"): con esto el bot sabe de qué anuncio viene el lead aunque Meta no mande el referral
/// (en las líneas por Evolution casi nunca llega), y adapta el guion al rubro y al ángulo.
/// App, rubro y ángulo salen del nombre del anuncio: "TurnosPro - barberia - bot WhatsApp".
/// </summary>
public class MetaAd
{
    /// <summary>Id del anuncio en Meta (PK).</summary>
    public string AdId { get; set; } = string.Empty;
    public string AccountId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    /// <summary>effective_status de Meta (ACTIVE, PAUSED, CAMPAIGN_PAUSED…).</summary>
    public string Status { get; set; } = string.Empty;

    public string? ProductKey { get; set; }
    /// <summary>Rubro del anuncio ("barberia", "padel").</summary>
    public string? Rubro { get; set; }
    /// <summary>Ángulo normalizado: "bot-whatsapp", "antes-despues" u otro slug del nombre.</summary>
    public string? Angle { get; set; }
    /// <summary>Cómo nombrar el negocio en una pregunta ("la barberia", "el estudio de tatuajes"). Sale del pre-armado.</summary>
    public string? BusinessRef { get; set; }

    /// <summary>Mensaje pre-armado que le queda escrito al lead al tocar el anuncio.</summary>
    public string? PrefillText { get; set; }
    public string? Title { get; set; }
    public string? Body { get; set; }
    /// <summary>Número de WhatsApp al que apunta el anuncio.</summary>
    public string? DestinationPhone { get; set; }
    /// <summary>Video del creativo (el de la biblioteca de la cuenta). La URL para reproducirlo vence: se pide en vivo (MetaAdMediaService).</summary>
    public string? VideoId { get; set; }

    public DateTimeOffset? AdCreatedAt { get; set; }
    public DateTimeOffset SyncedAt { get; set; } = DateTimeOffset.UtcNow;
}
