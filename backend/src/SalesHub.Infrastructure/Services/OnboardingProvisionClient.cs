using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace SalesHub.Infrastructure.Services;

/// <summary>Crea la cuenta de la app al cierre del onboarding (bot-register, genérico por app).</summary>
public interface IOnboardingProvisionClient
{
    /// <summary>POST al endpoint de provisión de la app. Devuelve el accessUrl si se creó, null si falló.
    /// <paramref name="extraJson"/> son params extra a mergear en el body (ej. {"accountType":"gymowner"}).</summary>
    Task<string?> RegisterAsync(string url, string nameField, string businessName, string email,
        string? contactName, string productKey, CancellationToken ct, string? extraJson = null,
        SalesHub.Core.Domain.Entities.Lead? lead = null);
}

public class OnboardingProvisionClient : IOnboardingProvisionClient
{
    private readonly HttpClient _http;
    private readonly ILogger<OnboardingProvisionClient> _log;
    private readonly string? _botKey;

    public OnboardingProvisionClient(HttpClient http, IConfiguration config, ILogger<OnboardingProvisionClient> log)
    {
        _http = http; _log = log; _botKey = config["BotRegister:Key"];
    }

    public async Task<string?> RegisterAsync(string url, string nameField, string businessName, string email,
        string? contactName, string productKey, CancellationToken ct, string? extraJson = null,
        SalesHub.Core.Domain.Entities.Lead? lead = null)
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
            // Atribución del anuncio que originó al lead (CTWA: externalAdReply; form de leads: ad_id).
            // La app la guarda en el tenant y la manda a Meta CAPI cuando el negocio paga.
            if (lead is not null && (!string.IsNullOrWhiteSpace(lead.AdId) || !string.IsNullOrWhiteSpace(lead.CtwaClid)))
            {
                payload["metaAdId"] = lead.AdId;
                payload["ctwaClid"] = lead.CtwaClid;
                payload["utmContent"] = lead.AdId;
                payload["phone"] = payload.TryGetValue("phone", out var ph) && ph is not null ? ph : lead.WhatsappPhone;
                payload["attributionSource"] = lead.CtwaClid is not null ? "ctwa"
                    : lead.Source == SalesHub.Core.Domain.Enums.LeadSource.MetaLeadAd ? "leadgen" : "ctwa";
            }
            else if (lead is not null)
            {
                payload["attributionSource"] = "hub";
            }
            // Params extra por persona (ej. {"accountType":"gymowner"}) → se mergean al body.
            if (!string.IsNullOrWhiteSpace(extraJson))
            {
                try
                {
                    using var extra = JsonDocument.Parse(extraJson);
                    foreach (var p in extra.RootElement.EnumerateObject())
                        payload[p.Name] = p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() : p.Value.ToString();
                }
                catch (Exception ex) { _log.LogWarning(ex, "Onboarding extra JSON inválido para {Product}", productKey); }
            }
            var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
            using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };
            if (!string.IsNullOrEmpty(_botKey)) req.Headers.Add("X-Bot-Key", _botKey);
            var resp = await _http.SendAsync(req, ct);
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
