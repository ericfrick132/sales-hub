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

    /// <summary>
    /// Otro número del negocio: el que aparece en su Instagram/web cuando ya teníamos uno distinto
    /// (del listado del competidor o de Google). Se muestra en la tab Competencia para llamar.
    /// </summary>
    public string? SecondaryPhone { get; set; }
    /// <summary>De dónde salió el secundario (instagram_link, instagram_bio, web_whatsapp, web_tel…).</summary>
    public string? SecondaryPhoneSource { get; set; }

    // ── Llamadas en frío (tab Competencia del CRM) ─────────────────────────────────────
    /// <summary>Último resultado: no_atiende, volver, demo, interesado, no_interesa, equivocado. null = nunca se llamó.</summary>
    public string? CallStatus { get; set; }
    /// <summary>Motivo de baja del competidor o por qué no le interesa (lo que contó en la llamada).</summary>
    public string? CallReason { get; set; }
    public string? CallNotes { get; set; }
    public int CallCount { get; set; }
    public DateTimeOffset? LastCallAt { get; set; }
    public Guid? LastCallBy { get; set; }
    /// <summary>Cuándo volver a llamar (resultado "volver") o la demo agendada.</summary>
    public DateTimeOffset? NextCallAt { get; set; }
    /// <summary>Lead del Pipeline creado al mostrar interés o agendar demo (origen Competencia).</summary>
    public Guid? LeadId { get; set; }

    /// <summary>Registro completo del listado (deportes, canchas, actividades, rating…).</summary>
    public string? RawJson { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
