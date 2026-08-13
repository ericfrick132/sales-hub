namespace SalesHub.Core.Domain.Entities;

public enum LeadNoteKind
{
    /// <summary>Nota escrita a mano por una persona.</summary>
    Note = 0,
    /// <summary>Rastro automático de un cambio de etapa (quién lo movió y de dónde a dónde).</summary>
    StageChange = 1,
    /// <summary>Anotación del sistema (recordatorio fijado, tarea cumplida).</summary>
    System = 2
}

/// <summary>
/// Nota del CRM sobre un lead. Es una BITÁCORA: cada entrada queda con su fecha y su
/// autor, a diferencia de <see cref="Lead.Notes"/>, que es un único string que además
/// pisan los workers (state-guard, repair-phones) y por eso no sirve para el historial
/// comercial.
/// </summary>
public class LeadNote
{
    public Guid Id { get; set; }

    public Guid LeadId { get; set; }
    public Lead? Lead { get; set; }

    /// <summary>Quién la escribió. null = la escribió el sistema.</summary>
    public Guid? SellerId { get; set; }
    public Seller? Seller { get; set; }

    public LeadNoteKind Kind { get; set; } = LeadNoteKind.Note;

    public string Text { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
