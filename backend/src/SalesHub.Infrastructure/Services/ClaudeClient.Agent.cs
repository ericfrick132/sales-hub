using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace SalesHub.Infrastructure.Services;

/// <summary>Un turno de la charla real con el lead: "user" = el lead, "assistant" = nosotros.</summary>
public record ClaudeTurn(string Role, string Text)
{
    public static ClaudeTurn User(string text) => new("user", text);
    public static ClaudeTurn Assistant(string text) => new("assistant", text);
}

/// <summary>
/// Un tool que el agente puede llamar. <see cref="InputSchema"/> es el JSON Schema del input
/// (objeto anónimo o JsonObject; los nombres se serializan tal cual). Con <see cref="Strict"/>
/// el schema tiene que llevar <c>additionalProperties = false</c> y <c>required</c>.
/// El handler devuelve el texto que ve el modelo; si tira excepción, el modelo recibe el error
/// marcado como <c>is_error</c> y puede corregirse.
/// </summary>
public sealed class ClaudeAgentTool
{
    public required string Name { get; init; }
    public required string Description { get; init; }
    public required object InputSchema { get; init; }
    public bool Strict { get; init; }
    public required Func<JsonElement, CancellationToken, Task<string>> Handler { get; init; }
}

public sealed class ClaudeAgentRequest
{
    /// <summary>
    /// Bloques de system en orden, de lo más estable a lo menos (reglas → conocimiento del producto).
    /// El último lleva el breakpoint de caché, así que acá NO va nada que cambie por lead.
    /// </summary>
    public required IReadOnlyList<string> SystemBlocks { get; init; }

    /// <summary>La charla en orden cronológico. Tiene que terminar en un turno del lead.</summary>
    public required IReadOnlyList<ClaudeTurn> History { get; init; }

    public IReadOnlyList<ClaudeAgentTool> Tools { get; init; } = [];
    public string? Model { get; init; }
    public int? MaxTokens { get; init; }

    /// <summary>low/medium/high... Solo se manda si viene seteado (Haiku 4.5 no lo acepta).</summary>
    public string? Effort { get; init; }

    /// <summary>Tope de idas y vueltas con tools por respuesta, para que un loop no queme crédito.</summary>
    public int MaxToolRounds { get; init; } = 5;

    public string Feature { get; init; } = "agent";
}

public record ClaudeToolCall(string Name, JsonElement Input, string Result, bool IsError);

/// <summary>
/// <see cref="Text"/> es lo que el agente quiere contestarle al lead (null si no escribió nada).
/// <see cref="ToolCalls"/> trae todo lo que ejecutó en el camino, para que el caller decida
/// (ej. si llamó a pasar_a_humano, no mandar el texto).
/// </summary>
public record ClaudeAgentResult(string? Text, IReadOnlyList<ClaudeToolCall> ToolCalls, string StopReason);

public partial class ClaudeClient
{
    /// <summary>
    /// Conversación de varios turnos con tools: manda el historial, ejecuta los tools que pida el
    /// modelo y repite hasta que conteste texto (o se agote <see cref="ClaudeAgentRequest.MaxToolRounds"/>).
    /// Cada ida a la API registra su uso. Devuelve null si no se pudo llamar (sin key, sin crédito,
    /// error HTTP o historial inválido).
    /// </summary>
    public async Task<ClaudeAgentResult?> RunAgentAsync(ClaudeAgentRequest req, CancellationToken ct = default)
    {
        if (!IsConfigured)
        {
            _log.LogWarning("Claude ApiKey no configurada — no se puede correr el agente");
            return null;
        }

        var messages = BuildMessages(req.History);
        if (messages is null)
        {
            _log.LogWarning("Agente ({Feature}): el historial está vacío o no termina en un turno del lead — no se llama", req.Feature);
            return null;
        }

        var usedModel = string.IsNullOrWhiteSpace(req.Model) ? _opts.Model : req.Model;
        var maxTokens = req.MaxTokens ?? _opts.MaxTokens;
        var toolsByName = req.Tools.ToDictionary(t => t.Name);
        var calls = new List<ClaudeToolCall>();

        for (var round = 0; round <= req.MaxToolRounds; round++)
        {
            if (IsCircuitOpen(out var reopensAt))
            {
                _log.LogDebug("Claude en pausa por falta de crédito hasta {ReopensAt:HH:mm:ss} — agente cortado (feature {Feature})", reopensAt, req.Feature);
                return null;
            }

            var body = BuildAgentBody(req, usedModel, maxTokens, messages);
            JsonNode? root;
            try
            {
                var resp = await _http.PostAsJsonAsync("messages", body, ct);
                var raw = await resp.Content.ReadAsStringAsync(ct);
                if (!resp.IsSuccessStatusCode)
                {
                    if (TripCircuitIfOutOfCredit(raw)) return null;
                    _log.LogWarning("Agente ({Feature}) falló: {Status} {Body}", req.Feature, resp.StatusCode, raw);
                    return null;
                }
                CloseCircuit();
                root = JsonNode.Parse(raw);
                using (var doc = JsonDocument.Parse(raw))
                    await LogUsageAsync(doc.RootElement, usedModel, req.Feature, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                _log.LogWarning(ex, "Agente ({Feature}) threw", req.Feature);
                return null;
            }

            var stop = root?["stop_reason"]?.GetValue<string>() ?? "unknown";
            var content = root?["content"] as JsonArray ?? [];
            var text = string.Join("\n", content
                    .Where(b => b?["type"]?.GetValue<string>() == "text")
                    .Select(b => b!["text"]?.GetValue<string>()?.Trim())
                    .Where(s => !string.IsNullOrWhiteSpace(s)));

            if (stop != "tool_use")
            {
                if (stop == "max_tokens")
                    _log.LogWarning("Agente ({Feature}) TRUNCADO por max_tokens ({Max})", req.Feature, maxTokens);
                else if (stop == "refusal")
                    _log.LogWarning("Agente ({Feature}): el modelo rechazó la respuesta ({Details})", req.Feature, root?["stop_details"]?.ToJsonString());
                return new ClaudeAgentResult(stop == "refusal" || text.Length == 0 ? null : text, calls, stop);
            }

            if (round == req.MaxToolRounds)
            {
                _log.LogWarning("Agente ({Feature}) sigue pidiendo tools después de {N} rondas — se corta", req.Feature, req.MaxToolRounds);
                return new ClaudeAgentResult(null, calls, "tool_rounds_exceeded");
            }

            // El turno del modelo vuelve tal cual (incluye bloques de thinking, que la API exige
            // de vuelta sin tocar), y TODOS los resultados van juntos en un solo turno de usuario.
            messages.Add(new JsonObject { ["role"] = "assistant", ["content"] = content.DeepClone() });
            var results = new JsonArray();
            foreach (var block in content.Where(b => b?["type"]?.GetValue<string>() == "tool_use"))
            {
                var id = block!["id"]!.GetValue<string>();
                var name = block["name"]!.GetValue<string>();
                var input = block["input"]?.Deserialize<JsonElement>() ?? default;
                var (result, isError) = await ExecuteToolAsync(toolsByName, name, input, req.Feature, ct);
                calls.Add(new ClaudeToolCall(name, input, result, isError));

                var tr = new JsonObject { ["type"] = "tool_result", ["tool_use_id"] = id, ["content"] = result };
                if (isError) tr["is_error"] = true;
                results.Add(tr);
            }
            messages.Add(new JsonObject { ["role"] = "user", ["content"] = results });
        }

        return new ClaudeAgentResult(null, calls, "tool_rounds_exceeded");
    }

    private async Task<(string Result, bool IsError)> ExecuteToolAsync(
        Dictionary<string, ClaudeAgentTool> tools, string name, JsonElement input, string feature, CancellationToken ct)
    {
        if (!tools.TryGetValue(name, out var tool))
            return ($"No existe el tool '{name}'.", true);
        try
        {
            return (await tool.Handler(input, ct), false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _log.LogWarning(ex, "Agente ({Feature}): el tool {Tool} falló", feature, name);
            return ($"Error ejecutando {name}: {ex.Message}", true);
        }
    }

    /// <summary>
    /// Pasa la charla al formato de la API: junta turnos seguidos del mismo lado (en WhatsApp el
    /// lead manda varios mensajes al hilo) y, si arrancamos nosotros (opener), antepone un turno
    /// de lead sintético porque la API exige que el primero sea "user". Null si no termina en el
    /// lead: los modelos nuevos no aceptan que el último turno sea nuestro.
    /// </summary>
    private static List<JsonNode>? BuildMessages(IReadOnlyList<ClaudeTurn> history)
    {
        var merged = new List<(string Role, string Text)>();
        foreach (var t in history)
        {
            if (string.IsNullOrWhiteSpace(t.Text)) continue;
            var role = t.Role == "assistant" ? "assistant" : "user";
            if (merged.Count > 0 && merged[^1].Role == role)
                merged[^1] = (role, merged[^1].Text + "\n" + t.Text.Trim());
            else
                merged.Add((role, t.Text.Trim()));
        }
        if (merged.Count == 0 || merged[^1].Role != "user") return null;
        if (merged[0].Role == "assistant")
            merged.Insert(0, ("user", "[El lead todavía no escribió; la charla arranca con nuestro primer mensaje.]"));

        return merged
            .Select(m => (JsonNode)new JsonObject
            {
                ["role"] = m.Role,
                ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = m.Text })
            })
            .ToList();
    }

    private static JsonObject BuildAgentBody(ClaudeAgentRequest req, string model, int maxTokens, List<JsonNode> messages)
    {
        // Breakpoint 1: fin del system (tools + system quedan cacheados juntos, van antes en el prefijo).
        var system = new JsonArray();
        for (var i = 0; i < req.SystemBlocks.Count; i++)
        {
            var block = new JsonObject { ["type"] = "text", ["text"] = req.SystemBlocks[i] };
            if (i == req.SystemBlocks.Count - 1) block["cache_control"] = new JsonObject { ["type"] = "ephemeral" };
            system.Add(block);
        }

        // Breakpoint 2: último bloque del último turno, así cada ronda de tools relee la anterior
        // desde caché. Se marca sobre una copia para no arrastrar marcas viejas (tope de 4).
        var msgs = new JsonArray(messages.Select(m => m.DeepClone()).ToArray());
        if (msgs[^1]?["content"] is JsonArray { Count: > 0 } last && last[^1] is JsonObject lastBlock)
            lastBlock["cache_control"] = new JsonObject { ["type"] = "ephemeral" };

        var body = new JsonObject
        {
            ["model"] = model,
            ["max_tokens"] = maxTokens,
            ["system"] = system,
            ["messages"] = msgs,
        };

        if (req.Tools.Count > 0)
        {
            body["tools"] = new JsonArray(req.Tools.Select(t =>
            {
                var def = new JsonObject
                {
                    ["name"] = t.Name,
                    ["description"] = t.Description,
                    ["input_schema"] = t.InputSchema as JsonNode ?? JsonSerializer.SerializeToNode(t.InputSchema),
                };
                if (t.Strict) def["strict"] = true;
                return (JsonNode)def;
            }).ToArray());
        }

        if (!string.IsNullOrWhiteSpace(req.Effort))
            body["output_config"] = new JsonObject { ["effort"] = req.Effort };

        return body;
    }
}
