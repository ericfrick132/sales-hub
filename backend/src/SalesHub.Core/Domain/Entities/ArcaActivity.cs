namespace SalesHub.Core.Domain.Entities;

/// <summary>
/// Una actividad del nomenclador de ARCA: Clasificador de Actividades Económicas (CLAE),
/// Formulario 883 (RG AFIP 3537/2013). Es el rubro con el que un negocio se inscribe en ARCA.
/// La tabla se carga sola al arrancar la API desde <c>SalesHub.Api/ArcaSeed/clae_f883.csv</c>
/// (ver <c>ArcaNomencladorImporter</c>) y la usa <c>ArcaLeadClassifier</c> para ponerle rubro a
/// cada lead.
/// </summary>
public class ArcaActivity
{
    /// <summary>Código CLAE de 6 dígitos (con ceros a la izquierda, ej. "011111").</summary>
    public string Code { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;

    /// <summary>Grupo (3 dígitos), ej. "931" Servicios para la práctica deportiva.</summary>
    public string GroupCode { get; set; } = string.Empty;
    public string GroupDescription { get; set; } = string.Empty;

    /// <summary>División (2 dígitos), ej. "93" Servicios deportivos, de entretenimiento y recreación.</summary>
    public string DivisionCode { get; set; } = string.Empty;
    public string DivisionDescription { get; set; } = string.Empty;

    /// <summary>Sección (letra), ej. "R" Servicios artísticos, culturales, deportivos y de esparcimiento.</summary>
    public string Section { get; set; } = string.Empty;
    public string SectionDescription { get; set; } = string.Empty;

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
