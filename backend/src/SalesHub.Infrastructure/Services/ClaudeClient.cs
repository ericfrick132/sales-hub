using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SalesHub.Core.Abstractions;
using SalesHub.Core.Domain.Entities;
using SalesHub.Infrastructure.Options;
using SalesHub.Infrastructure.Persistence;

namespace SalesHub.Infrastructure.Services;

/// <summary>
/// Cliente mínimo de la API de Claude (Anthropic Messages API). Una sola
/// llamada: system prompt cacheado + un turno de usuario → texto. Devuelve null
/// si falla o no hay key configurada. Cada llamada exitosa registra su uso
/// (tokens + costo USD) en ai_usage_logs, etiquetado por <c>feature</c>, para
/// el widget de gasto del dashboard.
/// </summary>
/// <summary>Imagen para un turno con visión (JPEG/PNG/WebP/GIF).</summary>
public record ClaudeImage(string MimeType, byte[] Data);

public class ClaudeClient
{
    private readonly HttpClient _http;
    private readonly ClaudeOptions _opts;
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<ClaudeClient> _log;

    public ClaudeClient(HttpClient http, IOptions<ClaudeOptions> opts, IServiceScopeFactory scopes, ILogger<ClaudeClient> log)
    {
        _http = http;
        _opts = opts.Value;
        _scopes = scopes;
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
        => CompleteAsync(systemPrompt, userMessage, _opts.MaxTokens, null, "unknown", ct);

    /// <summary>Igual, pero etiquetando dónde se gasta (feature) para el tracking de costo.</summary>
    public Task<string?> CompleteAsync(string systemPrompt, string userMessage, string feature, CancellationToken ct = default)
        => CompleteAsync(systemPrompt, userMessage, _opts.MaxTokens, null, feature, ct);

    /// <summary>
    /// Igual que la sobrecarga corta pero permite subir <paramref name="maxTokens"/>
    /// (artículos largos) y elegir un <paramref name="model"/> distinto al default
    /// (ej. Sonnet para contenido de calidad). Usado por el motor de SEO/GEO.
    /// </summary>
    public Task<string?> CompleteAsync(string systemPrompt, string userMessage, int maxTokens, string? model, CancellationToken ct = default)
        => CompleteAsync(systemPrompt, userMessage, maxTokens, model, "unknown", ct);

    /// <summary>Sobrecarga completa: maxTokens + model + feature (etiqueta de gasto).</summary>
    public Task<string?> CompleteAsync(string systemPrompt, string userMessage, int maxTokens, string? model, string feature, CancellationToken ct = default)
        => CompleteAsync(systemPrompt, userMessage, null, maxTokens, model, feature, ct);

    /// <summary>Con visión y maxTokens/model default.</summary>
    public Task<string?> CompleteAsync(string systemPrompt, string userMessage, IReadOnlyList<ClaudeImage>? images, string feature, CancellationToken ct = default)
        => CompleteAsync(systemPrompt, userMessage, images, _opts.MaxTokens, null, feature, ct);

    /// <summary>
    /// Igual, pero con VISIÓN: las <paramref name="images"/> van como bloques
    /// base64 antes del texto del turno de usuario. Usado por el loop de
    /// inspiración de Posteos para que Claude MIRE la referencia (estilo visual)
    /// además de leerla.
    /// </summary>
    public async Task<string?> CompleteAsync(string systemPrompt, string userMessage, IReadOnlyList<ClaudeImage>? images, int maxTokens, string? model, string feature, CancellationToken ct = default)
    {
        if (!IsConfigured)
        {
            _log.LogWarning("Claude ApiKey no configurada — no se puede completar");
            return null;
        }

        if (IsCircuitOpen(out var reopensAt))
        {
            _log.LogDebug("Claude en pausa por falta de crédito hasta {ReopensAt:HH:mm:ss} — no se llama (feature {Feature})", reopensAt, feature);
            return null;
        }

        var usedModel = string.IsNullOrWhiteSpace(model) ? _opts.Model : model;

        // Sin imágenes el content es string plano (formato histórico); con
        // imágenes es un array de bloques image+text.
        object userContent = images is { Count: > 0 }
            ? images.Select(i => (object)new
                {
                    type = "image",
                    source = new { type = "base64", media_type = i.MimeType, data = Convert.ToBase64String(i.Data) }
                })
                .Append(new { type = "text", text = userMessage })
                .ToArray()
            : userMessage;

        var body = new
        {
            model = usedModel,
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
                new { role = "user", content = userContent }
            }
        };

        try
        {
            var resp = await _http.PostAsJsonAsync("messages", body, ct);
            if (!resp.IsSuccessStatusCode)
            {
                var err = await resp.Content.ReadAsStringAsync(ct);
                if (TripCircuitIfOutOfCredit(err)) return null;
                _log.LogWarning("Claude completion failed: {Status} {Body}", resp.StatusCode, err);
                return null;
            }
            CloseCircuit();
            var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync(ct), cancellationToken: ct);

            // Registrar el uso (tokens + costo) — nos cobraron aunque no haya bloque de texto.
            await LogUsageAsync(doc.RootElement, usedModel, feature, ct);

            // Cortado por tope de salida: el texto llega truncado (JSON de posteos roto).
            // Lo avisamos fuerte para verlo en logs; el caller decide si el parse sobrevive.
            if (doc.RootElement.TryGetProperty("stop_reason", out var sr) && sr.GetString() == "max_tokens")
                _log.LogWarning("Claude completion TRUNCADA por max_tokens ({Max}) — subir el presupuesto del caller (feature {Feature})", maxTokens, feature);

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

    // ── Cortacircuitos por saldo agotado ───────────────────────────────────
    // Cuando la cuenta se queda sin crédito, la API responde 400 a TODO. Los workers
    // que llaman por lote no marcan el ítem como procesado si falla, así que al tick
    // siguiente reintentan el mismo batch entero: se llegó a ~100 requests por minuto
    // rebotando. Mientras no hay saldo eso solo ensucia los logs, pero apenas se
    // recarga la cuenta ese mismo loop empieza a quemar crédito real a esa velocidad.
    // Ante el error de saldo cortamos en seco por un rato y ni siquiera salimos a la red.
    private static readonly TimeSpan OutOfCreditCooldown = TimeSpan.FromMinutes(30);
    private static long _pausedUntilTicks;

    private static bool IsCircuitOpen(out DateTimeOffset reopensAt)
    {
        var ticks = Interlocked.Read(ref _pausedUntilTicks);
        reopensAt = new DateTimeOffset(ticks, TimeSpan.Zero);
        return ticks > DateTimeOffset.UtcNow.Ticks;
    }

    /// <summary>Vuelve a habilitar las llamadas apenas una responde OK (saldo recargado).</summary>
    private void CloseCircuit()
    {
        if (Interlocked.Exchange(ref _pausedUntilTicks, 0) > 0)
            _log.LogInformation("Claude respondió OK — se reanudan las llamadas");
    }

    /// <summary>
    /// Detecta el error de saldo y abre el cortacircuitos. Devuelve true si lo manejó
    /// (para no volver a loguear el mismo cuerpo de error en cada intento).
    /// </summary>
    private bool TripCircuitIfOutOfCredit(string errorBody)
    {
        if (string.IsNullOrEmpty(errorBody)) return false;
        var isOutOfCredit = errorBody.Contains("credit balance is too low", StringComparison.OrdinalIgnoreCase)
            || errorBody.Contains("insufficient_quota", StringComparison.OrdinalIgnoreCase);
        if (!isOutOfCredit) return false;

        var until = DateTimeOffset.UtcNow.Add(OutOfCreditCooldown);
        // Solo el primero loguea Y avisa: los que ya venían en vuelo no repiten el mensaje.
        // (Si al expirar el cooldown la cuenta sigue en cero, el próximo fallo re-abre el
        // circuito y vuelve a avisar → recordatorio cada ~30 min mientras esté sin saldo.)
        if (Interlocked.Exchange(ref _pausedUntilTicks, until.UtcTicks) <= DateTimeOffset.UtcNow.Ticks)
        {
            _log.LogError("Claude sin crédito — se pausan TODAS las llamadas por {Min} min (hasta {Until:HH:mm:ss} UTC). Recargar en Plans & Billing.",
                OutOfCreditCooldown.TotalMinutes, until);
            _ = NotifyOutOfCreditAsync(); // fire-and-forget: no bloquea el request que falló
        }
        return true;
    }

    /// <summary>Avisa por WhatsApp al número maestro que la cuenta se quedó sin crédito.</summary>
    private async Task NotifyOutOfCreditAsync()
    {
        try
        {
            using var scope = _scopes.CreateScope();
            var alerter = scope.ServiceProvider.GetService<IAdminAlerter>();
            if (alerter is null) return;
            const string msg = "⚠️ Claude se quedó SIN CRÉDITO. Se pausaron TODAS las llamadas de IA "
                + "(posteos, respuestas del bot, transcripciones, etc.) por 30 min. "
                + "Recargá para reactivar: https://console.anthropic.com/settings/billing";
            await alerter.AlertAsync(msg, CancellationToken.None);
        }
        catch (Exception ex) { _log.LogWarning(ex, "No pude enviar el aviso de Claude sin crédito"); }
    }

    /// <summary>Lee usage.* de la respuesta, calcula el costo USD y lo persiste. Best-effort.</summary>
    private async Task LogUsageAsync(JsonElement root, string model, string feature, CancellationToken ct)
    {
        try
        {
            int input = 0, output = 0, cacheRead = 0, cacheCreate = 0;
            if (root.TryGetProperty("usage", out var u) && u.ValueKind == JsonValueKind.Object)
            {
                if (u.TryGetProperty("input_tokens", out var i) && i.ValueKind == JsonValueKind.Number) input = i.GetInt32();
                if (u.TryGetProperty("output_tokens", out var o) && o.ValueKind == JsonValueKind.Number) output = o.GetInt32();
                if (u.TryGetProperty("cache_read_input_tokens", out var cr) && cr.ValueKind == JsonValueKind.Number) cacheRead = cr.GetInt32();
                if (u.TryGetProperty("cache_creation_input_tokens", out var cc) && cc.ValueKind == JsonValueKind.Number) cacheCreate = cc.GetInt32();
            }

            var entry = new AiUsageLog
            {
                Feature = string.IsNullOrWhiteSpace(feature) ? "unknown" : feature,
                Model = model,
                InputTokens = input,
                OutputTokens = output,
                CacheReadTokens = cacheRead,
                CacheCreationTokens = cacheCreate,
                CostUsd = ClaudePricing.CostUsd(model, input, output, cacheRead, cacheCreate),
            };

            using var scope = _scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            db.Set<AiUsageLog>().Add(entry);
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "No pude registrar el uso de Claude");
        }
    }
}
