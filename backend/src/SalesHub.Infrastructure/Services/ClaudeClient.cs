using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SalesHub.Infrastructure.Options;

namespace SalesHub.Infrastructure.Services;

/// <summary>
/// Cliente mínimo de la API de Claude (Anthropic Messages API). Una sola
/// llamada: system prompt cacheado + un turno de usuario → texto. Devuelve null
/// si falla o no hay key configurada.
/// </summary>
public class ClaudeClient
{
    private readonly HttpClient _http;
    private readonly ClaudeOptions _opts;
    private readonly ILogger<ClaudeClient> _log;

    public ClaudeClient(HttpClient http, IOptions<ClaudeOptions> opts, ILogger<ClaudeClient> log)
    {
        _http = http;
        _opts = opts.Value;
        _log = log;
        _http.BaseAddress = new Uri(_opts.BaseUrl.TrimEnd('/') + "/");
        _http.Timeout = TimeSpan.FromSeconds(_opts.TimeoutSeconds);
        if (!string.IsNullOrWhiteSpace(_opts.ApiKey))
        {
            _http.DefaultRequestHeaders.Add("x-api-key", _opts.ApiKey);
            _http.DefaultRequestHeaders.Add("anthropic-version", "2023-06-01");
        }
    }

    /// <summary>True si hay API key — los callers no procesan nada si está sin configurar.</summary>
    public bool IsConfigured => !string.IsNullOrWhiteSpace(_opts.ApiKey);

    /// <summary>
    /// Una llamada: <paramref name="systemPrompt"/> va como bloque cacheado
    /// (ephemeral) — conviene que sea la parte estática (playbook + producto)
    /// para abaratar llamadas repetidas. <paramref name="userMessage"/> es el
    /// turno variable (la conversación). Devuelve el texto generado o null.
    /// </summary>
    public Task<string?> CompleteAsync(string systemPrompt, string userMessage, CancellationToken ct = default)
        => CompleteAsync(systemPrompt, userMessage, _opts.MaxTokens, null, ct);

    /// <summary>
    /// Igual que la sobrecarga corta pero permite subir <paramref name="maxTokens"/>
    /// (artículos largos) y elegir un <paramref name="model"/> distinto al default
    /// (ej. Sonnet para contenido de calidad). Usado por el motor de SEO/GEO.
    /// </summary>
    public async Task<string?> CompleteAsync(string systemPrompt, string userMessage, int maxTokens, string? model, CancellationToken ct = default)
    {
        if (!IsConfigured)
        {
            _log.LogWarning("Claude ApiKey no configurada — no se puede completar");
            return null;
        }

        var body = new
        {
            model = string.IsNullOrWhiteSpace(model) ? _opts.Model : model,
            max_tokens = maxTokens,
            system = new[]
            {
                new
                {
                    type = "text",
                    text = systemPrompt,
                    cache_control = new { type = "ephemeral" }
                }
            },
            messages = new[]
            {
                new { role = "user", content = userMessage }
            }
        };

        try
        {
            var resp = await _http.PostAsJsonAsync("messages", body, ct);
            if (!resp.IsSuccessStatusCode)
            {
                var err = await resp.Content.ReadAsStringAsync(ct);
                _log.LogWarning("Claude completion failed: {Status} {Body}", resp.StatusCode, err);
                return null;
            }
            var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            // content: [ { type: "text", text: "..." }, ... ]
            if (doc.RootElement.TryGetProperty("content", out var content)
                && content.ValueKind == JsonValueKind.Array)
            {
                foreach (var block in content.EnumerateArray())
                {
                    if (block.TryGetProperty("type", out var t) && t.GetString() == "text"
                        && block.TryGetProperty("text", out var txt))
                    {
                        var s = txt.GetString();
                        return string.IsNullOrWhiteSpace(s) ? null : s.Trim();
                    }
                }
            }
            _log.LogWarning("Claude completion: respuesta sin bloque de texto");
            return null;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Claude completion threw");
            return null;
        }
    }
}
