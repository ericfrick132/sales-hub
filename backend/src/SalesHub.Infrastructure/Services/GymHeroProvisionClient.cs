using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace SalesHub.Infrastructure.Services;

/// <summary>Crea la cuenta de GymHero al cierre del onboarding (igual que el nodo de n8n).</summary>
public interface IGymHeroProvisionClient
{
    /// <summary>POST bot-register. Devuelve el accessUrl si se creó, null si falló.</summary>
    Task<string?> RegisterAsync(string gymName, string email, string? contactName, CancellationToken ct);
}

public class GymHeroProvisionClient : IGymHeroProvisionClient
{
    private readonly HttpClient _http;
    private readonly ILogger<GymHeroProvisionClient> _log;
    private readonly string _url;

    public GymHeroProvisionClient(HttpClient http, IConfiguration config, ILogger<GymHeroProvisionClient> log)
    {
        _http = http;
        _log = log;
        _url = config["GymHero:BotRegisterUrl"] ?? "https://gymhero.fitness/api/tenants/bot-register";
    }

    public async Task<string?> RegisterAsync(string gymName, string email, string? contactName, CancellationToken ct)
    {
        try
        {
            var resp = await _http.PostAsJsonAsync(_url, new
            {
                gymName,
                email,
                contactName = contactName ?? "Cliente",
                utmSource = "whatsapp-bot",
                utmMedium = "whatsapp",
                utmCampaign = "chatbot-gymhero",
            }, ct);

            if (!resp.IsSuccessStatusCode)
            {
                _log.LogWarning("bot-register falló: {Status}", resp.StatusCode);
                return null;
            }

            var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            if (doc.RootElement.TryGetProperty("data", out var data)
                && data.TryGetProperty("accessUrl", out var au) && au.ValueKind == JsonValueKind.String)
                return au.GetString();

            _log.LogWarning("bot-register OK pero sin accessUrl en la respuesta");
            return null;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "bot-register threw");
            return null;
        }
    }
}
