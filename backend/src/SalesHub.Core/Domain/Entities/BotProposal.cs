namespace SalesHub.Core.Domain.Entities;

/// <summary>
/// Mejora del bot que propone el análisis diario de los chats (BotLearningService): un texto del guion
/// (onboarding_configs) o una respuesta del diccionario (reply_intents) que convendría cambiar, con los
/// chats de ejemplo. Eric la aprueba o la descarta desde el dashboard (tarjetas); aprobar aplica el texto
/// nuevo y guarda el anterior para el rollback. Nada se aplica sin aprobación.
/// </summary>
public class BotProposal
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Día analizado (Argentina).</summary>
    public DateOnly Day { get; set; }
    public string ProductKey { get; set; } = string.Empty;

    /// <summary>"config" (campo de OnboardingConfig), "intent" (respuesta de un ReplyIntent) o "idea" (sin aplicar solo).</summary>
    public string Kind { get; set; } = "config";
    /// <summary>config: nombre del campo ("KnownBusinessGreeting", "Questions[0]"); intent: Key del ReplyIntent.</summary>
    public string Target { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;
    /// <summary>Por qué: qué suena mal o qué se pierde, en criollo.</summary>
    public string Rationale { get; set; } = string.Empty;
    public string? OldValue { get; set; }
    public string? NewValue { get; set; }
    /// <summary>Chats de ejemplo: [{leadId, leadName, context, botSaid, better}] (jsonb).</summary>
    public string? ExamplesJson { get; set; }
    /// <summary>En cuántos chats del día pasó.</summary>
    public int Occurrences { get; set; }

    /// <summary>pending, approved, rejected, rolled_back o stale (el texto cambió por otro lado antes de decidir).</summary>
    public string Status { get; set; } = "pending";
    public DateTimeOffset? DecidedAt { get; set; }
    public Guid? DecidedBy { get; set; }
    public DateTimeOffset? AppliedAt { get; set; }
    public DateTimeOffset? RolledBackAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>Una corrida del análisis diario (una por día analizado), para no repetirla ni pagarla dos veces.</summary>
public class BotLearningRun
{
    public DateOnly Day { get; set; }
    public DateTimeOffset RanAt { get; set; } = DateTimeOffset.UtcNow;
    public int Conversations { get; set; }
    public int Proposals { get; set; }
    public string? Error { get; set; }
}
