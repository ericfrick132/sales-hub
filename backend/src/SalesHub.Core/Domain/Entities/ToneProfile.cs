namespace SalesHub.Core.Domain.Entities;

/// <summary>
/// TONO DE CONVERSACIÓN editable: la voz con la que escribe TODO lo que compone la IA en
/// sales-hub (respuestas de venta, nudges, soporte, asides del onboarding). Un registro
/// global (Scope = "global") + overrides opcionales por producto (Scope = productKey).
/// Se inyecta como base de estilo del system prompt; si no hay ninguno cargado, el código
/// cae al texto default (el estilo argentino informal de siempre).
/// </summary>
public class ToneProfile
{
    public Guid Id { get; set; }

    /// <summary>"global" o un productKey (override por producto, pisa al global).</summary>
    public string Scope { get; set; } = "global";

    /// <summary>El tono en sí, texto libre que va al system prompt como bloque ESTILO.</summary>
    public string Content { get; set; } = string.Empty;

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
