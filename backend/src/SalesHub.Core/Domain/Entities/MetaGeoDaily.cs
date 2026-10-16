namespace SalesHub.Core.Domain.Entities;

/// <summary>
/// Rendimiento diario de un anuncio de Meta en una provincia/región (insights level=ad,
/// breakdowns=country,region, time_increment=1). Lo carga MetaGeoSyncService y lo lee el mapa
/// de anuncios por provincia. Una fila por día, anuncio y región; la sync de los últimos 3 días
/// la pisa (Meta corrige la atribución). Ojo: <see cref="Reach"/> es del día y NO se puede sumar
/// entre días/anuncios — el alcance exacto de un rango se pide en vivo a Meta.
/// </summary>
public class MetaGeoDaily
{
    public Guid Id { get; set; } = Guid.NewGuid();
    /// <summary>Día en la zona horaria de la cuenta publicitaria (Argentina).</summary>
    public DateOnly Date { get; set; }
    public string AccountId { get; set; } = string.Empty;
    public string CampaignId { get; set; } = string.Empty;
    public string CampaignName { get; set; } = string.Empty;
    public string AdsetId { get; set; } = string.Empty;
    public string AdsetName { get; set; } = string.Empty;
    public string AdId { get; set; } = string.Empty;
    public string AdName { get; set; } = string.Empty;
    /// <summary>App del anuncio (por el nombre de la campaña, del anuncio o de meta_ads). null = no se reconoció.</summary>
    public string? ProductKey { get; set; }
    /// <summary>ISO2 del país ("AR", "UY").</summary>
    public string Country { get; set; } = string.Empty;
    /// <summary>Región con el nombre canónico (las provincias argentinas como las nombra el IGN, con tildes).</summary>
    public string Region { get; set; } = string.Empty;

    public long Reach { get; set; }
    public long Impressions { get; set; }
    /// <summary>Gasto en la moneda de la cuenta (USD).</summary>
    public decimal Spend { get; set; }
    public long Clicks { get; set; }
    public long UniqueClicks { get; set; }
    public long LinkClicks { get; set; }
    /// <summary>Acción video_view: reproducciones de 3 s (hook rate = VideoViews3s / Impressions).</summary>
    public long VideoViews3s { get; set; }
    /// <summary>onsite_conversion.total_messaging_connection: conversaciones de WhatsApp (el proxy que Meta da por región).</summary>
    public long MessagingConnections { get; set; }
    public long FirstReplies { get; set; }
    public long Depth2 { get; set; }
    public long Depth3 { get; set; }

    public DateTimeOffset SyncedAt { get; set; } = DateTimeOffset.UtcNow;
}
