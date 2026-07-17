using System.Text;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using SalesHub.Core.Abstractions;
using SalesHub.Infrastructure.Persistence;
using SalesHub.Infrastructure.Services;

namespace SalesHub.Api.AdminMenu;

/// <summary>
/// Bot de configuración por WhatsApp para el MAESTRO: navega un menú numerado (categoría →
/// sub-opción → hoja) y en la hoja pide los datos que falten y ejecuta el cambio contra la propia
/// API (vía <see cref="AdminApiExecutor"/>). Determinístico, sin LLM. Mismo gating del maestro que
/// el intake de inspiración (config <c>inspiration_settings</c>: línea + MasterPhone). Se engancha
/// en el webhook ANTES del intake; devuelve false (deja pasar) salvo que el maestro esté en modo
/// menú o mande el trigger "menu"/"config".
/// </summary>
public class AdminMenuRelay
{
    private const string Prefix = "⚙️"; // prefijo de las respuestas del bot (loop-guard en self-chat)
    private static readonly TimeSpan SessionTtl = TimeSpan.FromMinutes(10);

    private readonly ApplicationDbContext _db;
    private readonly IEvolutionClient _evo;
    private readonly AdminApiExecutor _exec;
    private readonly ILogger<AdminMenuRelay> _log;

    public AdminMenuRelay(ApplicationDbContext db, IEvolutionClient evo, AdminApiExecutor exec, ILogger<AdminMenuRelay> log)
    {
        _db = db; _evo = evo; _exec = exec; _log = log;
    }

    // ── Estado en memoria (single-user, mismo patrón que InspirationIntakeRelay) ──
    private static readonly object Gate = new();
    private static List<MenuNode>? _stack;     // pila de navegación (root en [0]); null = fuera del menú
    private static FormState? _form;           // form de hoja en progreso
    private static DateTimeOffset _until;       // TTL sliding de la sesión

    private sealed class FormState
    {
        public required ActionSpec Spec;
        public int Idx;
        public readonly Dictionary<string, string> Collected = new();
        public List<Choice>? CurrentChoices;    // opciones del input Choice actual (para resolver "N")
        public bool AwaitingConfirm;
        public string? PendingPath;
        public object? PendingBody;
    }

    private static readonly Regex NonDigit = new(@"\D", RegexOptions.Compiled);

    public async Task<bool> TryHandleAsync(ConversationService.IncomingMessage incoming, CancellationToken ct)
    {
        var cfg = await GetConfigAsync(ct);
        if (!cfg.Enabled || cfg.InstanceName is null || cfg.MasterSuffix is null) return false;
        if (!string.Equals(incoming.InstanceName, cfg.InstanceName, StringComparison.OrdinalIgnoreCase)) return false;

        var phone = NormalizeDigits(incoming.FromPhone ?? ExtractPhone(incoming.FromJid));
        if (phone is null || phone.Length < 6 || Suffix(phone) != cfg.MasterSuffix) return false;
        if (incoming.FromMe)
        {
            // fromMe sólo vale en self-chat real (owner == maestro); si no, es tráfico ajeno → dejar pasar.
            var owner = NormalizeDigits(ExtractPhone(incoming.SenderJid));
            if (owner is null || Suffix(owner) != cfg.MasterSuffix) return false;
            // Loop-guard: nuestras propias respuestas vuelven como fromMe con el prefijo.
            if (incoming.Text.TrimStart().StartsWith(Prefix, StringComparison.Ordinal)) return true;
        }

        var text = incoming.Text.Trim();
        if (text.Length == 0) return false;
        var lower = text.ToLowerInvariant();

        bool inMenu;
        lock (Gate) { inMenu = _stack is not null && DateTimeOffset.UtcNow < _until; if (!inMenu) { _stack = null; _form = null; } }

        // Fuera del menú: sólo reaccionamos al trigger; el resto lo maneja inspiración/etc.
        if (!inMenu)
        {
            if (lower is not ("menu" or "menú" or "config" or "configurar" or "/menu")) return false;
            lock (Gate) { _stack = new List<MenuNode> { AdminMenuTree.Root }; _form = null; _until = DateTimeOffset.UtcNow + SessionTtl; }
            await ShowCurrentAsync(incoming, ct);
            return true;
        }

        lock (Gate) { _until = DateTimeOffset.UtcNow + SessionTtl; }

        // Comandos globales de navegación.
        switch (lower)
        {
            case "salir" or "listo" or "chau" or "x":
                lock (Gate) { _stack = null; _form = null; }
                await ReplyAsync(incoming, "salí del menú. Mandá \"menu\" cuando quieras volver.", ct);
                return true;
            case "cancelar":
                lock (Gate) { _form = null; }
                await ReplyAsync(incoming, "cancelado.", ct);
                await ShowCurrentAsync(incoming, ct);
                return true;
            case "inicio" or "menu" or "menú":
                lock (Gate) { _stack = new List<MenuNode> { AdminMenuTree.Root }; _form = null; }
                await ShowCurrentAsync(incoming, ct);
                return true;
            case "0" or "atras" or "atrás" or "volver" when _form is null:
                lock (Gate) { if (_stack!.Count > 1) _stack.RemoveAt(_stack.Count - 1); }
                await ShowCurrentAsync(incoming, ct);
                return true;
        }

        if (_form is not null)
        {
            await HandleFormInputAsync(incoming, text, lower, ct);
            return true;
        }

        // Navegación: elegir una opción numerada del menú actual.
        await HandleNavAsync(incoming, text, ct);
        return true;
    }

    // ── Navegación ────────────────────────────────────────────────────────────
    private async Task HandleNavAsync(ConversationService.IncomingMessage incoming, string text, CancellationToken ct)
    {
        MenuNode current;
        lock (Gate) { current = _stack!.Last(); }
        var children = await current.ChildrenAsync(_exec, ct);

        if (!int.TryParse(text, out var n) || n < 1 || n > children.Count)
        {
            await ReplyAsync(incoming, "no te seguí — mandá el número de una opción (o \"0\" atrás, \"salir\").", ct);
            await ShowCurrentAsync(incoming, ct);
            return;
        }

        var chosen = children[n - 1];
        if (chosen.IsLeaf)
        {
            lock (Gate) { _form = new FormState { Spec = chosen.Action! }; }
            await AdvanceFormAsync(incoming, ct); // arranca pidiendo el 1er input (o ejecuta si no hay)
        }
        else
        {
            lock (Gate) { _stack!.Add(chosen); }
            await ShowCurrentAsync(incoming, ct);
        }
    }

    // ── Forms de hoja ───────────────────────────────────────────────────────────
    private async Task HandleFormInputAsync(ConversationService.IncomingMessage incoming, string text, string lower, CancellationToken ct)
    {
        FormState f;
        lock (Gate) { f = _form!; }

        if (f.AwaitingConfirm)
        {
            if (lower is "si" or "sí" or "s" or "dale" or "ok")
            {
                await ExecuteAsync(incoming, f, ct);
            }
            else if (lower is "no" or "n")
            {
                lock (Gate) { _form = null; }
                await ReplyAsync(incoming, "listo, no hice nada.", ct);
                await ShowCurrentAsync(incoming, ct);
            }
            else
            {
                await ReplyAsync(incoming, "¿confirmás? contestá \"sí\" o \"no\".", ct);
            }
            return;
        }

        var input = f.Spec.Inputs[f.Idx];
        string value;
        switch (input.Type)
        {
            case InputType.Choice:
                if (!int.TryParse(text, out var ci) || f.CurrentChoices is null || ci < 1 || ci > f.CurrentChoices.Count)
                {
                    await ReplyAsync(incoming, "elegí el número de una opción 👆", ct);
                    return;
                }
                value = f.CurrentChoices[ci - 1].Value;
                break;
            case InputType.Bool:
                if (lower is "si" or "sí" or "s" or "true" or "1") value = "true";
                else if (lower is "no" or "n" or "false" or "0") value = "false";
                else { await ReplyAsync(incoming, "contestá \"sí\" o \"no\".", ct); return; }
                break;
            case InputType.Number:
                if (!long.TryParse(text, out _)) { await ReplyAsync(incoming, "mandame un número.", ct); return; }
                value = text;
                break;
            default:
                value = text;
                break;
        }

        lock (Gate) { f.Collected[input.Name] = value; f.Idx++; }
        await AdvanceFormAsync(incoming, ct);
    }

    /// <summary>Pide el siguiente input, o si ya están todos arma path+body y confirma/ejecuta.</summary>
    private async Task AdvanceFormAsync(ConversationService.IncomingMessage incoming, CancellationToken ct)
    {
        FormState f;
        lock (Gate) { f = _form!; }

        if (f.Idx < f.Spec.Inputs.Count)
        {
            var input = f.Spec.Inputs[f.Idx];
            if (input.Type == InputType.Choice)
            {
                var choices = input.Options is null ? new List<Choice>() : await input.Options(f.Collected, _exec, ct);
                if (choices.Count == 0)
                {
                    lock (Gate) { _form = null; }
                    await ReplyAsync(incoming, "no hay opciones disponibles para esto ahora 😕.", ct);
                    await ShowCurrentAsync(incoming, ct);
                    return;
                }
                lock (Gate) { f.CurrentChoices = choices; }
                var sb = new StringBuilder(input.Prompt).Append('\n');
                for (var i = 0; i < choices.Count; i++) sb.Append($"{i + 1}. {choices[i].Label}\n");
                await ReplyAsync(incoming, sb.ToString().TrimEnd(), ct);
            }
            else
            {
                var hint = input.Type == InputType.Bool ? " (sí/no)" : "";
                await ReplyAsync(incoming, $"{input.Prompt}{hint}", ct);
            }
            return;
        }

        // Todos los inputs juntados → armar path + body.
        var spec = f.Spec;
        string path; object? body;
        if (spec.Build is not null)
        {
            (path, body) = await spec.Build(f.Collected, _exec, ct);
        }
        else
        {
            path = FillTemplate(spec.PathTemplate, f.Collected);
            body = DefaultBody(spec, f.Collected);
        }
        lock (Gate) { f.PendingPath = path; f.PendingBody = body; }

        if (spec.Impact == Impact.High)
        {
            lock (Gate) { f.AwaitingConfirm = true; }
            var what = spec.ConfirmText ?? $"{spec.Method} {path}";
            await ReplyAsync(incoming, $"⚠️ {what}\n¿confirmás? (sí/no)", ct);
        }
        else
        {
            await ExecuteAsync(incoming, f, ct);
        }
    }

    private async Task ExecuteAsync(ConversationService.IncomingMessage incoming, FormState f, CancellationToken ct)
    {
        var (status, body) = await _exec.SendAsync(f.Spec.Method, f.PendingPath!, f.PendingBody, ct);
        lock (Gate) { _form = null; }

        if (AdminApiExecutor.IsOk(status))
        {
            _log.LogInformation("AdminMenu ejecutó {Method} {Path} → {Status}", f.Spec.Method, f.PendingPath, status);
            await ReplyAsync(incoming, f.Spec.SuccessText ?? "✅ hecho.", ct);
        }
        else
        {
            _log.LogWarning("AdminMenu falló {Method} {Path} → {Status}: {Body}", f.Spec.Method, f.PendingPath, status, body);
            var snippet = string.IsNullOrWhiteSpace(body) ? "" : $": {(body.Length > 200 ? body[..200] : body)}";
            await ReplyAsync(incoming, $"❌ no salió (status {status}){snippet}", ct);
        }
        await ShowCurrentAsync(incoming, ct);
    }

    // ── Render del menú actual ──────────────────────────────────────────────────
    private async Task ShowCurrentAsync(ConversationService.IncomingMessage incoming, CancellationToken ct)
    {
        MenuNode current; int depth;
        lock (Gate) { current = _stack!.Last(); depth = _stack.Count; }
        var children = await current.ChildrenAsync(_exec, ct);

        var sb = new StringBuilder();
        sb.Append(depth == 1 ? "🗂️ *Configuración*" : $"*{current.Label}*").Append('\n');
        if (children.Count == 0) sb.Append("(sin opciones)\n");
        for (var i = 0; i < children.Count; i++)
            sb.Append($"{i + 1}. {children[i].Label}\n");
        sb.Append(depth > 1 ? "\n_0 atrás · inicio · salir_" : "\n_elegí un número · salir_");
        await ReplyAsync(incoming, sb.ToString(), ct);
    }

    // ── Helpers de body/path ────────────────────────────────────────────────────
    private static string FillTemplate(string template, IReadOnlyDictionary<string, string> vals)
    {
        foreach (var (k, v) in vals) template = template.Replace("{" + k + "}", Uri.EscapeDataString(v));
        return template;
    }

    /// <summary>Body por defecto: cada input que NO sea un {placeholder} del path, tipado según InputType.</summary>
    private static object? DefaultBody(ActionSpec spec, IReadOnlyDictionary<string, string> vals)
    {
        if (spec.Method.ToUpperInvariant() is "GET" or "DELETE") return null;
        var d = new Dictionary<string, object?>();
        foreach (var input in spec.Inputs)
        {
            if (spec.PathTemplate.Contains("{" + input.Name + "}")) continue; // es param de path
            if (!vals.TryGetValue(input.Name, out var raw)) continue;
            d[input.Name] = input.Type switch
            {
                InputType.Bool => raw == "true",
                InputType.Number => long.TryParse(raw, out var l) ? l : 0,
                _ => raw,
            };
        }
        return d.Count > 0 ? d : null;
    }

    private Task ReplyAsync(ConversationService.IncomingMessage incoming, string text, CancellationToken ct)
        => _evo.SendTextAsync(incoming.InstanceName, incoming.FromJid, $"{Prefix} {text}", ct);

    // ── Gating del maestro (replica el patrón de InspirationIntakeRelay) ─────────
    private sealed record ConfigSnapshot(bool Enabled, string? InstanceName, string? MasterSuffix);
    private static ConfigSnapshot? _cache;
    private static DateTimeOffset _cacheUntil;

    private async Task<ConfigSnapshot> GetConfigAsync(CancellationToken ct)
    {
        if (_cache is not null && DateTimeOffset.UtcNow < _cacheUntil) return _cache;
        var s = await _db.InspirationSettings.AsNoTracking().FirstOrDefaultAsync(ct);
        var digits = s?.MasterPhone is null ? null : NormalizeDigits(s.MasterPhone);
        _cache = new ConfigSnapshot(s?.Enabled == true, s?.InstanceName,
            digits is null || digits.Length < 6 ? null : Suffix(digits));
        _cacheUntil = DateTimeOffset.UtcNow.AddSeconds(15);
        return _cache;
    }

    private static string? NormalizeDigits(string? phone)
        => string.IsNullOrWhiteSpace(phone) ? null : NonDigit.Replace(phone, "");
    private static string Suffix(string digits) => digits.Length >= 8 ? digits[^8..] : digits;
    private static string? ExtractPhone(string? jid)
    {
        if (string.IsNullOrWhiteSpace(jid)) return null;
        var at = jid.IndexOf('@');
        return at >= 0 ? jid[..at] : jid;
    }
}
