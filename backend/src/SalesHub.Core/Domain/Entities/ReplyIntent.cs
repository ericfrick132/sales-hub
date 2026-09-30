namespace SalesHub.Core.Domain.Entities;

/// <summary>
/// Un tipo de mensaje del lead del diccionario de respuestas sin IA (ver
/// docs/bases-de-venta-2026-09.md §11): el patrón que lo detecta sobre el texto normalizado,
/// qué hace el bot y la respuesta sugerida. Se prueban en orden (<see cref="SortOrder"/>) y gana
/// el primero que coincide. Se edita en /diccionario.
/// </summary>
public class ReplyIntent
{
    public Guid Id { get; set; }

    /// <summary>Clave estable, ej. "precio". Se guarda en ConversationMessage.IntentKey.</summary>
    public string Key { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    /// <summary>Regex sobre el texto normalizado (IntentText.Normalize).</summary>
    public string Pattern { get; set; } = string.Empty;

    /// <summary>Solo aplica a mensajes de hasta N palabras (null = sin límite).</summary>
    public int? MaxWords { get; set; }

    /// <summary>Qué hace el bot con este tipo (ej. "no_responder", "responder_precio", "agendar_llamada").</summary>
    public string Action { get; set; } = string.Empty;

    /// <summary>Respuesta sugerida con placeholders ({vendedor}, {producto}, {precio}...). null = depende del guion.</summary>
    public string? Reply { get; set; }

    /// <summary>
    /// Respuesta propia de una app (productKey → texto), que pisa a <see cref="Reply"/>: cada app
    /// tiene su línea y su guion ("un sistema para gimnasios" no le sirve a TurnosPro).
    /// </summary>
    public Dictionary<string, string> ReplyByProduct { get; set; } = new();

    public string? Note { get; set; }

    /// <summary>Ejemplos reales (para la pantalla).</summary>
    public List<string> Examples { get; set; } = new();

    public int SortOrder { get; set; }

    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Si el bot responde solo con <see cref="Reply"/>. Apagado = modo sombra: se clasifica y se
    /// mide, pero la respuesta sigue siendo la de siempre.
    /// </summary>
    public bool AutoReply { get; set; }

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
