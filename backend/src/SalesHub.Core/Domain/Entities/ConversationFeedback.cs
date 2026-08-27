namespace SalesHub.Core.Domain.Entities;

/// <summary>
/// Calificación humana de una conversación (👍/👎 + comentario). Se usa para mostrar en el
/// panel del lead y, sobre todo, para ENTRENAR al agente: los últimos comentarios del
/// producto se inyectan al prompt como aprendizajes ("evitá X", "esto funcionó").
/// </summary>
public class ConversationFeedback
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid LeadId { get; set; }
    public Lead? Lead { get; set; }
    public string ProductKey { get; set; } = string.Empty;
    public Guid? SellerId { get; set; }
    public Seller? Seller { get; set; }
    /// <summary>1 = 👍, -1 = 👎, 0 = solo comentario.</summary>
    public int Rating { get; set; }
    public string? Note { get; set; }
    /// <summary>Último mensaje nuestro al momento de calificar (contexto para el aprendizaje).</summary>
    public string? RatedMessage { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
