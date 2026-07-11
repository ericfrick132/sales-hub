namespace SalesHub.Core.Domain.Entities;

/// <summary>
/// FAQ curada de SOPORTE por producto — la capa 1 del bot: si la consulta del cliente
/// matchea las keywords, se responde la Answer canónica TAL CUAL (sin IA, sin riesgo).
/// Crece con el uso: el vendedor puede promover una respuesta aprobada a FAQ desde la UI.
/// </summary>
public class ProductFaq
{
    public Guid Id { get; set; }

    public string ProductKey { get; set; } = string.Empty;

    /// <summary>La pregunta canónica (para la UI y como contexto del LLM en capa 2).</summary>
    public string Question { get; set; } = string.Empty;

    /// <summary>Keywords de match (case-insensitive, "contiene"). Si CUALQUIERA aparece
    /// en el mensaje del cliente, la FAQ matchea. Mantenerlas específicas para evitar
    /// falsos positivos (ej. "no puedo entrar", no "entrar").</summary>
    public List<string> Keywords { get; set; } = new();

    /// <summary>Respuesta canónica, ya escrita en el tono del sistema. Se manda tal cual.</summary>
    public string Answer { get; set; } = string.Empty;

    public bool Enabled { get; set; } = true;

    /// <summary>"manual" (cargada por admin) | "promoted" (promovida desde una conversación).</summary>
    public string Source { get; set; } = "manual";

    /// <summary>Cuántas veces se usó como respuesta (telemetría de utilidad).</summary>
    public int TimesUsed { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
