namespace SalesHub.Core.Domain.Entities;

/// <summary>
/// Negocio sacado de un listado público de la competencia (Wellhub, ATC, Fresha, AgendaPro…) para
/// armar públicos de Meta. NO es un <see cref="Lead"/> y vive en su propia tabla a propósito: el
/// sender, la cadencia, el bot y el reparto a vendedores leen solo <c>leads</c>, así que nada de
/// acá se contacta nunca. Usarlo para contactarlos necesita revisión legal antes (ley 25.326).
/// Se carga y se sigue en Marketing → Competencia → Públicos (ver <c>AudienceLeadService</c>).
/// </summary>
public class AudienceLead
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Archivo de origen, ej. "wellhub-ar", "atc-sin-uso", "crossfy-activos".</summary>
    public string Dataset { get; set; } = string.Empty;
    /// <summary>Competidor del que sale el listado ("Wellhub", "ATC Sports").</summary>
    public string Competitor { get; set; } = string.Empty;
    /// <summary>App a la que apunta el público (gymhero, turnospro, playcrew).</summary>
    public string ProductKey { get; set; } = string.Empty;
    /// <summary>"usa" = cliente activo del competidor; "baja" = se dio de baja o no lo usa.</summary>
    public string Segment { get; set; } = "usa";
    /// <summary>Id del negocio en el competidor (único por dataset).</summary>
    public string ExternalId { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;
    public string? Category { get; set; }
    public string? Address { get; set; }
    public string? City { get; set; }
    public string? Province { get; set; }
    public string? PostalCode { get; set; }
    /// <summary>ISO2 ("AR", "MX", "UY").</summary>
    public string? Country { get; set; }
    public double? Latitude { get; set; }
    public double? Longitude { get; set; }

    /// <summary>E.164 (+549…, +52…), listo para el público de Meta.</summary>
    public string? Phone { get; set; }
    /// <summary>celular / fijo / otro / desconocido, por la numeración de ENACOM o del IFT.</summary>
    public string? PhoneType { get; set; }
    public bool WhatsappProbable { get; set; }
    /// <summary>De dónde salió el teléfono: "competidor", "cruce:&lt;dataset&gt;", "lead", "google_places".</summary>
    public string? PhoneSource { get; set; }

    public string? Instagram { get; set; }
    public string? Website { get; set; }
    public string? Email { get; set; }
    public string? PlaceId { get; set; }
    /// <summary>Resultado de la extensión "Buscar WhatsApp" (Instagram → web): ok / google (no lo encontró) / error.</summary>
    public string? FinderStatus { get; set; }
    public DateTimeOffset? FinderCheckedAt { get; set; }
    /// <summary>
    /// Historial de cada paso de la búsqueda, con fecha: [{step, result, detail, at}]. Pasos: ig_profile,
    /// ig_search, web (de la extensión), leads, google (del servidor). Incluye bloqueos y errores.
    /// </summary>
    public string? FinderLog { get; set; }
    /// <summary>Batch de la extensión al que se asignó (los batches se arman a mano desde Públicos).</summary>
    public Guid? FinderBatchId { get; set; }
    /// <summary>Última búsqueda en Google Places (con o sin resultado), para no pagarla dos veces.</summary>
    public DateTimeOffset? PlacesCheckedAt { get; set; }

    /// <summary>Registro completo del listado (deportes, canchas, actividades, rating…).</summary>
    public string? RawJson { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
