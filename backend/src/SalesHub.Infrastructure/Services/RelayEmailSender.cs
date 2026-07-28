using System.Net.Http.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using SalesHub.Core.Abstractions;

namespace SalesHub.Infrastructure.Services;

/// <summary>
/// IEmailSender vía relay HTTPS: el droplet tiene el SMTP saliente bloqueado por
/// DigitalOcean (25/465/587), así que el hop SMTP lo hace una app propia en App
/// Platform que sí puede (gymhero, endpoint bot-send-email). Config: Email:RelayUrl
/// + Email:RelayKey (X-Bot-Key, el mismo secreto BotRegister__Key de bot-register).
/// </summary>
public class RelayEmailSender : IEmailSender
{
    private readonly HttpClient _http;
    private readonly IConfiguration _config;
    private readonly ILogger<RelayEmailSender> _log;

    public RelayEmailSender(HttpClient http, IConfiguration config, ILogger<RelayEmailSender> log)
    {
        _http = http; _config = config; _log = log;
    }

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(_config["Email:RelayUrl"])
        && !string.IsNullOrWhiteSpace(_config["Email:RelayKey"]);

    public async Task<bool> SendAsync(string to, string subject, string htmlBody, string? fromName = null, CancellationToken ct = default)
    {
        if (!IsConfigured) return false;
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, _config["Email:RelayUrl"]);
            req.Headers.Add("X-Bot-Key", _config["Email:RelayKey"]);
            req.Content = JsonContent.Create(new { to, subject, html = htmlBody, fromName });
            using var res = await _http.SendAsync(req, ct);
            if (!res.IsSuccessStatusCode)
            {
                _log.LogWarning("Relay de email a {To} devolvió HTTP {Status}", to, (int)res.StatusCode);
                return false;
            }
            var body = await res.Content.ReadFromJsonAsync<RelayResponse>(cancellationToken: ct);
            if (body?.Success == true)
            {
                _log.LogInformation("Email enviado (relay) a {To}: {Subject}", to, subject);
                return true;
            }
            _log.LogWarning("Relay de email a {To} falló: {Error}", to, body?.Error ?? "sin detalle");
            return false;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Relay de email a {To} falló", to);
            return false;
        }
    }

    private sealed record RelayResponse(bool Success, string? Error);
}
