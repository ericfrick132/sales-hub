using System.Text.Json.Nodes;

namespace SalesHub.Api.AdminMenu;

/// <summary>
/// El árbol de menú (DATA). Cada hoja mapea a un endpoint de la API. Sumar una acción = sumar una
/// entrada acá (el motor <see cref="AdminMenuRelay"/> es genérico). F1 arranca con 5 categorías;
/// el resto (posteos, vendedores, productos completos, leads, etc.) se van agregando en F2.
/// </summary>
public static class AdminMenuTree
{
    public static readonly MenuNode Root = new()
    {
        Title = "Configuración",
        Children = new()
        {
            Flags(),
            Onboarding(),
            Competidores(),
            IaVentas(),
            Transcripcion(),
        }
    };

    // ── 1. Flags (prender/apagar motores) — todos High (confirman) ──────────────
    private static MenuNode Flags() => new()
    {
        Title = "Interruptores (flags)", Icon = "🔀",
        Children = new()
        {
            FlagLeaf("whatsapp", "Envíos + IA de WhatsApp"),
            FlagLeaf("posteos", "Posteos automáticos"),
            FlagLeaf("instagram", "Instagram (follow/DM/scrape)"),
            FlagLeaf("onboarding", "Onboarding de ads"),
            FlagLeaf("captacion", "Captación de leads"),
            FlagLeaf("reengage", "Re-enganche B2B"),
            FlagLeaf("rebalance", "Rebalanceo de leads"),
            FlagLeaf("seo", "SEO"),
            FlagLeaf("voicenote", "Notas de voz IA"),
        }
    };

    private static MenuNode FlagLeaf(string key, string title) => new()
    {
        Title = title,
        Action = new ActionSpec
        {
            Method = "POST",
            PathTemplate = $"/api/flags/{key}",
            Impact = Impact.High,
            Inputs = new() { new InputField { Name = "enabled", Prompt = $"¿Prender {title}?", Type = InputType.Bool } },
            ConfirmText = $"cambiar el flag '{key}' ({title})",
            SuccessText = $"✅ listo, flag '{key}' actualizado.",
        }
    };

    // ── 3. Onboarding: editar una pregunta del alta ─────────────────────────────
    private static MenuNode Onboarding() => new()
    {
        Title = "Onboarding de ads", Icon = "🚀",
        Children = new()
        {
            new MenuNode
            {
                Title = "Editar una pregunta del alta",
                Action = new ActionSpec
                {
                    Method = "PUT",
                    Impact = Impact.Low,
                    SuccessText = "✅ pregunta actualizada. Ya está en vivo.",
                    Inputs = new()
                    {
                        new InputField { Name = "app", Prompt = "¿De qué app?", Type = InputType.Choice, Options = AppChoices },
                        new InputField { Name = "qidx", Prompt = "¿Qué pregunta cambio?", Type = InputType.Choice, Options = OnboardingQuestionChoices },
                        new InputField { Name = "text", Prompt = "Mandame el texto NUEVO de la pregunta (podés usar [NUEVO_MENSAJE] para splittear).", Type = InputType.Text },
                    },
                    Build = async (c, exec, ct) =>
                    {
                        var app = c["app"];
                        var configs = await exec.GetJsonAsync("/api/onboarding-configs", ct);
                        if (configs is null) return ($"/api/onboarding-configs/{app}", (object?)null);
                        foreach (var el in configs.Value.EnumerateArray())
                        {
                            if (el.TryGetProperty("productKey", out var pk) && pk.GetString() == app)
                            {
                                var obj = JsonNode.Parse(el.GetRawText())!.AsObject();
                                var qs = obj["questions"]!.AsArray();
                                if (int.TryParse(c["qidx"], out var i) && i >= 0 && i < qs.Count)
                                    qs[i] = c["text"];
                                return ($"/api/onboarding-configs/{app}", (object?)obj);
                            }
                        }
                        return ($"/api/onboarding-configs/{app}", (object?)null);
                    }
                }
            }
        }
    };

    // ── 5. Competidores: agregar ────────────────────────────────────────────────
    private static MenuNode Competidores() => new()
    {
        Title = "Competidores", Icon = "💡",
        Children = new()
        {
            new MenuNode
            {
                Title = "Agregar competidor",
                Action = new ActionSpec
                {
                    Method = "POST",
                    PathTemplate = "/api/competitors",
                    Impact = Impact.Low,
                    SuccessText = "✅ competidor agregado.",
                    Inputs = new()
                    {
                        new InputField { Name = "handle", Prompt = "¿Handle del competidor? (sin @)", Type = InputType.Text },
                        new InputField { Name = "vertical", Prompt = "¿De qué app/vertical?", Type = InputType.Choice, Options = AppChoices },
                        new InputField { Name = "purpose", Prompt = "¿Para qué sirve?", Type = InputType.Choice, Options = PurposeChoices },
                    },
                }
            }
        }
    };

    // ── 8. IA de ventas: agregar regla dura ─────────────────────────────────────
    private static MenuNode IaVentas() => new()
    {
        Title = "IA de ventas", Icon = "🧠",
        Children = new()
        {
            new MenuNode
            {
                Title = "Agregar regla dura (AiRule)",
                Action = new ActionSpec
                {
                    Method = "POST",
                    Impact = Impact.Low,
                    SuccessText = "✅ regla agregada.",
                    Inputs = new()
                    {
                        new InputField { Name = "text", Prompt = "Escribí la regla (ej: 'Nunca decir la palabra X').", Type = InputType.Text },
                        new InputField { Name = "scope", Prompt = "¿Aplica a?", Type = InputType.Choice, Options = AppChoicesWithAll },
                    },
                    Build = (c, exec, ct) => Task.FromResult(
                        ("/api/ai-rules", (object?)new
                        {
                            text = c["text"],
                            productKey = string.IsNullOrEmpty(c["scope"]) ? null : c["scope"],
                            isActive = true,
                            sortOrder = 0
                        }))
                }
            }
        }
    };

    // ── 11. Transcripción: on/off ───────────────────────────────────────────────
    private static MenuNode Transcripcion() => new()
    {
        Title = "Transcripción de audios", Icon = "🎤",
        Children = new()
        {
            new MenuNode
            {
                Title = "Prender/apagar",
                Action = new ActionSpec
                {
                    Method = "POST",
                    PathTemplate = "/api/transcription/enabled",
                    Impact = Impact.Low,
                    SuccessText = "✅ transcripción actualizada.",
                    Inputs = new() { new InputField { Name = "enabled", Prompt = "¿Prender la transcripción?", Type = InputType.Bool } },
                }
            }
        }
    };

    // ── Opciones dinámicas ──────────────────────────────────────────────────────
    private static async Task<List<Choice>> AppChoices(IReadOnlyDictionary<string, string> _, AdminApiExecutor exec, CancellationToken ct)
    {
        var list = new List<Choice>();
        var json = await exec.GetJsonAsync("/api/products", ct);
        if (json is null) return list;
        foreach (var el in json.Value.EnumerateArray())
        {
            var key = el.TryGetProperty("productKey", out var k) ? k.GetString() : null;
            if (string.IsNullOrWhiteSpace(key)) continue;
            var name = el.TryGetProperty("displayName", out var d) ? d.GetString() : key;
            list.Add(new Choice(name ?? key!, key!));
        }
        return list;
    }

    private static async Task<List<Choice>> AppChoicesWithAll(IReadOnlyDictionary<string, string> c, AdminApiExecutor exec, CancellationToken ct)
    {
        var list = new List<Choice> { new("Todas las apps", "") };
        list.AddRange(await AppChoices(c, exec, ct));
        return list;
    }

    private static Task<List<Choice>> PurposeChoices(IReadOnlyDictionary<string, string> _, AdminApiExecutor __, CancellationToken ___)
        => Task.FromResult(new List<Choice>
        {
            new("Inspiración (posteos)", "inspiration"),
            new("Fuente de follow (audiencia)", "followsource"),
            new("Ambas", "both"),
        });

    private static async Task<List<Choice>> OnboardingQuestionChoices(IReadOnlyDictionary<string, string> c, AdminApiExecutor exec, CancellationToken ct)
    {
        var list = new List<Choice>();
        if (!c.TryGetValue("app", out var app)) return list;
        var configs = await exec.GetJsonAsync("/api/onboarding-configs", ct);
        if (configs is null) return list;
        foreach (var el in configs.Value.EnumerateArray())
        {
            if (el.TryGetProperty("productKey", out var pk) && pk.GetString() == app
                && el.TryGetProperty("questions", out var qs) && qs.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                var i = 0;
                foreach (var q in qs.EnumerateArray())
                {
                    var t = (q.GetString() ?? "").Replace("[NUEVO_MENSAJE]", " / ");
                    list.Add(new Choice(t.Length > 45 ? t[..45] + "…" : t, i.ToString()));
                    i++;
                }
            }
        }
        return list;
    }
}
