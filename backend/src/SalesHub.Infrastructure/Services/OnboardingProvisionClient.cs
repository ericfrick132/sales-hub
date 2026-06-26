using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace SalesHub.Infrastructure.Services;

/// <summary>Crea la cuenta de la app al cierre del onboarding (bot-register, genérico por app).</summary>
public interface IOnboardingProvisionClient
{
    /// <summary>POST al endpoint de provisión de la app. Devuelve el accessUrl si se creó, null si falló.</summary>
    Task<string?> RegisterAsync(string url, string nameField, string businessName, string email,
        string? contactName, string productKey, CancellationToken ct);
}

public class OnboardingProvisionClient : IOnboardingProvisionClient
{
    private readonly HttpClient _http;
    private readonly ILogger<OnboardingProvisionClient> _log;

    public OnboardingProvisionClient(HttpClient http, ILogger<OnboardingProvisionClient> log)
    {
        _http = http; _log = log;
    }

    public async Task<string?> RegisterAsync(string url, string nameField, string businessName, string email,
        string? contactName, string productKey, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(url)) { _log.LogWarning("Onboarding sin ProvisionUrl para {Product}", productKey); return null; }
        try
        {
            // Body genérico: el campo del nombre del negocio es configurable por app (gymName, businessName, …).
            var payload = new Dictionary<string, object?>
            {
                [string.IsNullOrWhiteSpace(nameField) ? "name" : nameField] = businessName,
                ["email"] = email,
                ["contactName"] = contactName ?? "Cliente",
                ["productKey"] = productKey,
                ["utmSource"] = "whatsapp-bot",
                ["utmMedium"] = "whatsapp",
                ["utmCampaign"] = "chatbot-" + productKey,
            };
            var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
            var resp = await _http.PostAsync(url, content, ct);
            if (!resp.IsSuccessStatusCode)
            {
                _log.LogWarning("bot-register {Product} falló: {Status}", productKey, resp.StatusCode);
                return null;
            }
            var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            if (doc.RootElement.TryGetProperty("data", out var data)
                && data.TryGetProperty("accessUrl", out var au) && au.ValueKind == JsonValueKind.String)
                return au.GetString();
            // algunos endpoints devuelven accessUrl en la raíz
            if (doc.RootElement.TryGetProperty("accessUrl", out var au2) && au2.ValueKind == JsonValueKind.String)
                return au2.GetString();
            _log.LogWarning("bot-register {Product} OK pero sin accessUrl", productKey);
            return null;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "bot-register {Product} threw", productKey);
            return null;
        }
    }
}
