namespace SalesHub.Core.Domain.Entities;

/// <summary>
/// Regla dura genérica para la IA de ventas. Se inyecta al final del system prompt
/// (con prioridad). Editable por CRUD desde el admin. ProductKey null = aplica a todas
/// las apps; si se setea, aplica solo a esa.
/// </summary>
public class AiRule
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Text { get; set; } = string.Empty;
    public string? ProductKey { get; set; }
    public bool IsActive { get; set; } = true;
    public int SortOrder { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
