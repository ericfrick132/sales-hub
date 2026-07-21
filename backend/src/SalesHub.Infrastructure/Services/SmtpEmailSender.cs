using System.Net;
using System.Net.Mail;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using SalesHub.Core.Abstractions;

namespace SalesHub.Infrastructure.Services;

/// <summary>
/// SMTP simple (mismo esquema de config que usa gymhero): Email:SmtpServer/SmtpPort/Username/
/// Password/FromAddress/FromName/EnableSsl. Sin config queda IsConfigured=false y todo sigue
/// saliendo por WhatsApp como siempre — cargar las credenciales en el .env del droplet la prende.
/// </summary>
public class SmtpEmailSender : IEmailSender
{
    private readonly IConfiguration _config;
    private readonly ILogger<SmtpEmailSender> _log;

    public SmtpEmailSender(IConfiguration config, ILogger<SmtpEmailSender> log)
    {
        _config = config; _log = log;
    }

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(_config["Email:SmtpServer"])
        && !string.IsNullOrWhiteSpace(_config["Email:Username"])
        && !string.IsNullOrWhiteSpace(_config["Email:Password"])
        && !string.IsNullOrWhiteSpace(_config["Email:FromAddress"]);

    public async Task<bool> SendAsync(string to, string subject, string htmlBody, CancellationToken ct = default)
    {
        if (!IsConfigured) return false;
        try
        {
            using var client = new SmtpClient(_config["Email:SmtpServer"])
            {
                Port = _config.GetValue("Email:SmtpPort", 587),
                Credentials = new NetworkCredential(_config["Email:Username"], _config["Email:Password"]),
                EnableSsl = _config.GetValue("Email:EnableSsl", true),
                Timeout = 30000,
            };
            using var message = new MailMessage
            {
                From = new MailAddress(_config["Email:FromAddress"]!, _config["Email:FromName"] ?? "Equipo"),
                Subject = subject,
                Body = htmlBody,
                IsBodyHtml = true,
            };
            message.To.Add(to);
            await client.SendMailAsync(message, ct);
            _log.LogInformation("Email enviado a {To}: {Subject}", to, subject);
            return true;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Email a {To} falló — se cae al flujo por WhatsApp", to);
            return false;
        }
    }
}
