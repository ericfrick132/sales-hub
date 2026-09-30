namespace SalesHub.Core.Domain.Entities;

/// <summary>
/// Opinión de una persona sobre lo que habría hecho el bot con un mensaje del lead (/simulacion):
/// bien / mal, el tipo y la acción correctos y lo que habría respondido ella. Alimenta el set de
/// oro del backtest (tools/diccionario) y las respuestas del diccionario. Una por mensaje.
/// </summary>
public class IntentFeedback
{
    public Guid Id { get; set; }

    /// <summary>El mensaje del lead (ConversationMessage entrante) evaluado.</summary>
    public Guid MessageId { get; set; }
    public Guid LeadId { get; set; }

    /// <summary>Lo que había decidido el bot cuando se marcó (foto: el diccionario cambia).</summary>
    public string? IntentKey { get; set; }
    public string? IntentAction { get; set; }
    public string? SimulatedReply { get; set; }

    /// <summary>"bien" o "mal".</summary>
    public string Verdict { get; set; } = "bien";

    /// <summary>Tipo correcto, si el del bot estaba mal.</summary>
    public string? CorrectKey { get; set; }

    /// <summary>Qué debería haber hecho: "respuesta", "sin_respuesta" o "ia_humano".</summary>
    public string? CorrectAction { get; set; }

    /// <summary>Lo que habría respondido la persona.</summary>
    public string? BetterReply { get; set; }

    public string? Note { get; set; }

    public Guid? CreatedBySellerId { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
