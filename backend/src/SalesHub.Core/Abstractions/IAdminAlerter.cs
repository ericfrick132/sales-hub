namespace SalesHub.Core.Abstractions;

/// <summary>
/// Aviso operativo al humano maestro (WhatsApp al MasterPhone del intake, por la
/// misma línea que ya usan las alertas de lead caliente / soporte). Para eventos
/// que necesitan intervención (ej. Claude sin crédito). Best-effort: nunca tira
/// excepción hacia arriba. Devuelve true si el mensaje se pudo enviar.
/// </summary>
public interface IAdminAlerter
{
    Task<bool> AlertAsync(string message, CancellationToken ct = default);
}
