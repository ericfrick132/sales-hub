namespace SalesHub.Core.Abstractions;

/// <summary>
/// Sender de mails transaccionales (link de acceso del onboarding, etc.). La motivación:
/// mandar links por WhatsApp dispara los filtros anti-spam de Meta — el link viaja por mail
/// y por WhatsApp solo se avisa "te lo mandé por mail".
/// </summary>
public interface IEmailSender
{
    /// <summary>False si falta config SMTP (feature apagada) — el caller cae al flujo por WhatsApp.</summary>
    bool IsConfigured { get; }

    /// <param name="fromName">Nombre visible del remitente — el producto del lead (ej. "TurnosPro"),
    /// para que el mail no salga firmado por otra app. Null = default del sender.</param>
    Task<bool> SendAsync(string to, string subject, string htmlBody, string? fromName = null, CancellationToken ct = default);
}
