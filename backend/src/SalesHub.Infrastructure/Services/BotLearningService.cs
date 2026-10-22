using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SalesHub.Core.Domain.Entities;
using SalesHub.Infrastructure.Persistence;

namespace SalesHub.Infrastructure.Services;

/// <summary>
/// Bot que aprende (pedido de Eric 22/10/2026). El análisis NO corre acá ni gasta API: lo hace la rutina
/// /bot-review de Claude Code en la Mac de Eric (LaunchAgent 7:00), que baja los chats del bot del día con
/// <see cref="ExportAsync"/>, los juzga y sube las propuestas con <see cref="ImportAsync"/>. Cada propuesta es
/// un cambio CONCRETO a un texto que usa el bot (guion de onboarding_configs o respuesta del diccionario) y
/// queda como tarjeta en el dashboard; aprobar aplica el texto y guarda el anterior, el rollback lo vuelve atrás.
/// </summary>
public class BotLearningService
{
    /// <summary>Campos de texto del guion que una propuesta puede cambiar. Las listas van con índice ("Questions[0]").</summary>
    private static readonly string[] ConfigFields =
    {
        "Intro", "PersonaQuestion", "EmailPrompt", "ReengageIntro", "SuccessMessage", "ClosingMessage",
        "PostSignupCheckin", "TrialDiscountNudge", "FirstStepsMessage", "HandoffMessage", "DemoPitch",
        "KnownBusinessQuestion", "KnownBusinessGreeting", "PricesMessage", "StartQuestion", "DemoOffer",
    };
    private static readonly string[] ConfigLists = { "Questions", "ReengageQuestions", "FollowUpMessages" };

    private const int MaxConversationsPerProduct = 40;
    private static readonly Regex Placeholder = new(@"\{[^{}]+\}", RegexOptions.Compiled);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly ApplicationDbContext _db;
    private readonly ILogger<BotLearningService> _log;

    public BotLearningService(ApplicationDbContext db, ILogger<BotLearningService> log)
    {
        _db = db; _log = log;
    }

    public static readonly TimeZoneInfo ArTz = SafeArTz();
    public static DateOnly TodayAr() => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, ArTz).DateTime);

    public record Example(Guid LeadId, string? LeadName, string? Context, string? BotSaid, string? Better);

    // ── exportar / importar (la rutina /bot-review) ─────────────────────────────────────

    public record ChatMessage(string At, string Who, string Text);
    public record Chat(Guid LeadId, string Name, string Status, string? AdTitle, bool EndedWithUs, List<ChatMessage> Messages);
    public record ProductExport(string ProductKey, Dictionary<string, string> Editable, Dictionary<string, string> Intents, List<Chat> Chats);
    public record Export(DateOnly Day, List<ProductExport> Products, List<string> PendingTargets, List<string> RejectedValues);

    /// <summary>
    /// Chats del bot de <paramref name="day"/> (Argentina): los que tienen onboarding y ese día tuvieron ida y vuelta,
    /// con 4 mensajes previos de contexto; por app, con los textos editables del guion y las respuestas del
    /// diccionario usadas. Incluye lo pendiente/rechazado para no volver a proponerlo.
    /// </summary>
    public async Task<Export> ExportAsync(DateOnly day, CancellationToken ct = default)
    {
        var offset = ArTz.GetUtcOffset(day.ToDateTime(TimeOnly.MinValue));
        var from = new DateTimeOffset(day.ToDateTime(TimeOnly.MinValue), offset);
        var to = from.AddDays(1);

        var botLeads = _db.Set<LeadOnboarding>().Select(o => o.LeadId);
        var leadIds = await _db.ConversationMessages.AsNoTracking()
            .Where(m => m.Timestamp >= from && m.Timestamp < to && botLeads.Contains(m.LeadId))
            .GroupBy(m => m.LeadId)
            .Where(g => g.Any(m => m.Direction == MessageDirection.Outbound) && g.Any(m => m.Direction == MessageDirection.Inbound))
            .Select(g => g.Key)
            .ToListAsync(ct);
        var leads = await _db.Leads.AsNoTracking().Where(l => leadIds.Contains(l.Id) && l.ProductKey != "")
            .Select(l => new { l.Id, l.Name, l.ProductKey, l.Status, l.AdTitle }).ToListAsync(ct);

        var products = new List<ProductExport>();
        foreach (var group in leads.GroupBy(l => l.ProductKey))
        {
            var cfg = await _db.OnboardingConfigs.AsNoTracking().FirstOrDefaultAsync(c => c.ProductKey == group.Key, ct);
            if (cfg is null) continue;
            var picked = group.Take(MaxConversationsPerProduct).ToList();
            var ids = picked.Select(l => l.Id).ToList();
            var msgs = await _db.ConversationMessages.AsNoTracking()
                .Where(m => ids.Contains(m.LeadId) && m.Timestamp >= from.AddDays(-2) && m.Timestamp < to)
                .OrderBy(m => m.Timestamp)
                .Select(m => new { m.LeadId, m.Direction, m.Text, m.Timestamp, m.IntentKey })
                .ToListAsync(ct);
            var chats = new List<Chat>();
            foreach (var l in picked)
            {
                var mine = msgs.Where(m => m.LeadId == l.Id).ToList();
                var firstOfDay = mine.FindIndex(m => m.Timestamp >= from);
                var slice = mine.Skip(Math.Max(0, firstOfDay - 4)).Take(40).ToList();
                if (slice.Count == 0) continue;
                chats.Add(new Chat(l.Id, l.Name, l.Status.ToString(), l.AdTitle,
                    slice[^1].Direction == MessageDirection.Outbound,
                    slice.Select(m => new ChatMessage(TimeZoneInfo.ConvertTime(m.Timestamp, ArTz).ToString("dd/MM HH:mm"),
                        m.Direction == MessageDirection.Inbound ? "lead" : "nosotros", Trunc(m.Text, 500) ?? "")).ToList()));
            }
            var intents = await IntentRepliesAsync(group.Key, msgs.Select(m => m.IntentKey).OfType<string>().Distinct().ToList(), ct);
            products.Add(new ProductExport(group.Key, Editable(cfg), intents, chats));
        }
        var pending = await _db.BotProposals.Where(p => p.Status == "pending")
            .Select(p => p.ProductKey + ":" + p.Kind + ":" + p.Target).ToListAsync(ct);
        var rejected = await _db.BotProposals.Where(p => p.Status == "rejected" && p.NewValue != null)
            .Select(p => p.ProductKey + ":" + p.NewValue).ToListAsync(ct);
        return new Export(day, products, pending, rejected);
    }

    public record ExampleIn(string? LeadId, string? Context, string? BotSaid, string? Better);
    public record ProposalIn(string ProductKey, string? Kind, string? Target, string? Title, string? Rationale,
        string? NewValue, int? Occurrences, List<ExampleIn>? Examples);
    public record ImportResult(int Saved, List<string> Skipped);

    /// <summary>
    /// Guarda las propuestas de la rutina como tarjetas pendientes. Valida contra el texto ACTUAL: target que
    /// exista, texto nuevo distinto y no vacío, sin placeholders inventados, sin repetir una pendiente ni algo
    /// ya rechazado. Lo descartado vuelve con el motivo. Registra la corrida del día.
    /// </summary>
    public async Task<ImportResult> ImportAsync(DateOnly day, int conversations, List<ProposalIn> proposals, CancellationToken ct = default)
    {
        var skipped = new List<string>();
        var saved = 0;
        foreach (var p in proposals)
        {
            var kind = p.Kind is "intent" or "idea" ? p.Kind : "config";
            var target = kind == "idea" ? "" : (p.Target ?? "").Trim();
            var label = $"{p.ProductKey}:{kind}:{target}";
            string? old = null;
            if (kind == "config")
            {
                var cfg = await _db.OnboardingConfigs.AsNoTracking().FirstOrDefaultAsync(c => c.ProductKey == p.ProductKey, ct);
                if (cfg is null || !Editable(cfg).TryGetValue(target, out old)) { skipped.Add($"{label}: el campo no existe o está vacío"); continue; }
            }
            else if (kind == "intent")
            {
                var it = await _db.ReplyIntents.AsNoTracking().FirstOrDefaultAsync(i => i.Key == target, ct);
                old = it is null ? null : it.ReplyByProduct.GetValueOrDefault(p.ProductKey) ?? it.Reply;
                if (string.IsNullOrWhiteSpace(old)) { skipped.Add($"{label}: la intención no existe o no tiene respuesta"); continue; }
            }
            var newValue = p.NewValue?.Trim();
            if (kind != "idea")
            {
                if (string.IsNullOrWhiteSpace(newValue) || newValue == old) { skipped.Add($"{label}: texto nuevo vacío o igual"); continue; }
                var allowed = Placeholder.Matches(old ?? "").Select(m => m.Value).ToHashSet();
                var invented = Placeholder.Matches(newValue).Select(m => m.Value).Where(v => !allowed.Contains(v) && !v.Contains('|')).ToList();
                if (invented.Count > 0) { skipped.Add($"{label}: placeholders que no existen ({string.Join(", ", invented)})"); continue; }
                if (await _db.BotProposals.AnyAsync(x => x.ProductKey == p.ProductKey && x.Status == "rejected" && x.NewValue == newValue, ct))
                { skipped.Add($"{label}: ya se rechazó ese mismo texto"); continue; }
                if (await _db.BotProposals.AnyAsync(x => x.ProductKey == p.ProductKey && x.Status == "pending" && x.Kind == kind && x.Target == target, ct))
                { skipped.Add($"{label}: ya hay una tarjeta pendiente para ese texto"); continue; }
            }

            var examples = new List<Example>();
            foreach (var e in (p.Examples ?? new()).Take(3))
            {
                Guid.TryParse(e.LeadId, out var lid);
                var name = lid == Guid.Empty ? null
                    : await _db.Leads.AsNoTracking().Where(l => l.Id == lid).Select(l => l.Name).FirstOrDefaultAsync(ct);
                examples.Add(new Example(lid, name, Trunc(e.Context, 800), Trunc(e.BotSaid, 800), Trunc(e.Better, 800)));
            }
            _db.BotProposals.Add(new BotProposal
            {
                Day = day,
                ProductKey = p.ProductKey,
                Kind = kind,
                Target = target,
                Title = Trunc(p.Title, 256) ?? "Mejora",
                Rationale = p.Rationale ?? "",
                OldValue = old,
                NewValue = kind == "idea" ? null : newValue,
                ExamplesJson = JsonSerializer.Serialize(examples, Json),
                Occurrences = Math.Max(1, p.Occurrences ?? examples.Count),
            });
            await _db.SaveChangesAsync(ct);
            saved++;
        }

        var run = await _db.BotLearningRuns.FirstOrDefaultAsync(r => r.Day == day, ct);
        if (run is null) _db.BotLearningRuns.Add(run = new BotLearningRun { Day = day });
        run.RanAt = DateTimeOffset.UtcNow;
        run.Conversations = conversations;
        run.Proposals += saved;
        run.Error = skipped.Count == 0 ? null : Trunc(string.Join(" | ", skipped), 1000);
        await _db.SaveChangesAsync(ct);
        _log.LogInformation("BotLearning {Day}: {N} tarjetas nuevas, {S} descartadas", day, saved, skipped.Count);
        return new ImportResult(saved, skipped);
    }

    // ── aplicar / deshacer ──────────────────────────────────────────────────────────────

    public record DecisionResult(bool Ok, string Status, string? Error);

    public async Task<DecisionResult> ApproveAsync(Guid id, Guid? by, CancellationToken ct = default)
    {
        var p = await _db.BotProposals.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (p is null) return new(false, "missing", "No existe");
        if (p.Status != "pending") return new(false, p.Status, "Ya estaba decidida");
        var now = DateTimeOffset.UtcNow;
        p.DecidedAt = now;
        p.DecidedBy = by;
        if (p.Kind != "idea")
        {
            var current = await CurrentValueAsync(p, ct);
            if (current != p.OldValue)
            {
                p.Status = "stale";
                await _db.SaveChangesAsync(ct);
                return new(false, "stale", "El texto cambió desde que se hizo la propuesta; no se aplicó.");
            }
            await SetValueAsync(p, p.NewValue!, ct);
            p.AppliedAt = now;
        }
        p.Status = "approved";
        await _db.SaveChangesAsync(ct);
        return new(true, p.Status, null);
    }

    public async Task<DecisionResult> RejectAsync(Guid id, Guid? by, CancellationToken ct = default)
    {
        var p = await _db.BotProposals.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (p is null) return new(false, "missing", "No existe");
        if (p.Status != "pending") return new(false, p.Status, "Ya estaba decidida");
        p.Status = "rejected";
        p.DecidedAt = DateTimeOffset.UtcNow;
        p.DecidedBy = by;
        await _db.SaveChangesAsync(ct);
        return new(true, p.Status, null);
    }

    /// <summary>Vuelve el texto al de antes. Un rechazo también se puede deshacer (vuelve a pendiente).</summary>
    public async Task<DecisionResult> RollbackAsync(Guid id, CancellationToken ct = default)
    {
        var p = await _db.BotProposals.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (p is null) return new(false, "missing", "No existe");
        if (p.Status == "rejected")
        {
            p.Status = "pending";
            p.DecidedAt = null;
            p.DecidedBy = null;
            await _db.SaveChangesAsync(ct);
            return new(true, p.Status, null);
        }
        if (p.Status != "approved") return new(false, p.Status, "Solo se deshace algo aprobado o rechazado");
        if (p.Kind != "idea")
        {
            var current = await CurrentValueAsync(p, ct);
            if (current != p.NewValue)
                return new(false, p.Status, "El texto se volvió a cambiar después (otra propuesta o a mano); deshacé esa primero.");
            await SetValueAsync(p, p.OldValue ?? "", ct);
        }
        p.Status = "rolled_back";
        p.RolledBackAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);
        return new(true, p.Status, null);
    }

    private async Task<string?> CurrentValueAsync(BotProposal p, CancellationToken ct)
    {
        if (p.Kind == "intent")
        {
            var it = await _db.ReplyIntents.AsNoTracking().FirstOrDefaultAsync(i => i.Key == p.Target, ct);
            return it is null ? null : it.ReplyByProduct.GetValueOrDefault(p.ProductKey) ?? it.Reply;
        }
        var cfg = await _db.OnboardingConfigs.AsNoTracking().FirstOrDefaultAsync(c => c.ProductKey == p.ProductKey, ct);
        return cfg is null ? null : Editable(cfg).GetValueOrDefault(p.Target);
    }

    private async Task SetValueAsync(BotProposal p, string value, CancellationToken ct)
    {
        if (p.Kind == "intent")
        {
            var it = await _db.ReplyIntents.FirstAsync(i => i.Key == p.Target, ct);
            // Solo para esta app: copia nueva del diccionario para que EF vea el cambio del jsonb.
            it.ReplyByProduct = new Dictionary<string, string>(it.ReplyByProduct) { [p.ProductKey] = value };
            it.UpdatedAt = DateTimeOffset.UtcNow;
            return;
        }
        var cfg = await _db.OnboardingConfigs.FirstAsync(c => c.ProductKey == p.ProductKey, ct);
        var m = Regex.Match(p.Target, @"^(\w+)\[(\d+)\]$");
        if (m.Success)
        {
            var prop = typeof(OnboardingConfig).GetProperty(m.Groups[1].Value)!;
            var list = new List<string>((List<string>)prop.GetValue(cfg)!);
            list[int.Parse(m.Groups[2].Value)] = value;
            prop.SetValue(cfg, list);
        }
        else typeof(OnboardingConfig).GetProperty(p.Target)!.SetValue(cfg, value);
    }

    // ── helpers ─────────────────────────────────────────────────────────────────────────

    /// <summary>Textos del guion con contenido, por nombre de campo (las listas con índice).</summary>
    private static Dictionary<string, string> Editable(OnboardingConfig cfg)
    {
        var d = new Dictionary<string, string>();
        foreach (var f in ConfigFields)
            if (typeof(OnboardingConfig).GetProperty(f)?.GetValue(cfg) is string { Length: > 0 } v) d[f] = v;
        foreach (var f in ConfigLists)
            if (typeof(OnboardingConfig).GetProperty(f)?.GetValue(cfg) is List<string> list)
                for (var i = 0; i < list.Count; i++)
                    if (!string.IsNullOrWhiteSpace(list[i])) d[$"{f}[{i}]"] = list[i];
        return d;
    }

    private async Task<Dictionary<string, string>> IntentRepliesAsync(string productKey, List<string> keys, CancellationToken ct)
    {
        var rows = await _db.ReplyIntents.AsNoTracking().Where(i => keys.Contains(i.Key) && i.Enabled).ToListAsync(ct);
        return rows.Select(i => (i.Key, Reply: i.ReplyByProduct.GetValueOrDefault(productKey) ?? i.Reply))
            .Where(x => !string.IsNullOrWhiteSpace(x.Reply))
            .ToDictionary(x => x.Key, x => x.Reply!);
    }

    private static string? Trunc(string? s, int max) => s is null ? null : s.Length <= max ? s : s[..max];

    private static TimeZoneInfo SafeArTz()
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById("America/Argentina/Buenos_Aires"); }
        catch { return TimeZoneInfo.CreateCustomTimeZone("AR", TimeSpan.FromHours(-3), "AR", "AR"); }
    }
}
