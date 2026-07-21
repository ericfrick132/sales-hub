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

    Task<bool> SendAsync(string to, string subject, string htmlBody, CancellationToken ct = default);
}
