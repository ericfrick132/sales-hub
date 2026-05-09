namespace SalesHub.Core.Domain.Entities;

/// <summary>
/// Round-robin counter por (producto, índice de step) para rotar entre las
/// variantes de audio. Cada vez que un lead recibe ese step, se incrementa
/// atómicamente y se elige el audio en posición LastIndex.
/// </summary>
public class MessageStepRotation
{
    public Guid ProductId { get; set; }
    /// <summary>Categoría override del producto, o "" para la cadencia default.</summary>
    public string Category { get; set; } = string.Empty;
    public int StepIndex { get; set; }
    public int LastIndex { get; set; }
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
