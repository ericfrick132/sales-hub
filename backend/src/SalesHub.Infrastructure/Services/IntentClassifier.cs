using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SalesHub.Core.Domain.Entities;
using SalesHub.Infrastructure.Persistence;

namespace SalesHub.Infrastructure.Services;

/// <summary>
/// Clasificación de un mensaje del lead contra una lista de patrones, sin base de datos (la usa
/// el clasificador en vivo y la prueba de paridad contra los scripts de análisis).
/// </summary>
public sealed class IntentMatcher
{
    public const string Other = "otro";
    public const string Empty = "vacio";
    public const string Qualification = "dato_calificacion";

    public record Rule(string Key, Regex Pattern, int? MaxWords);

    private static readonly TimeSpan T = TimeSpan.FromMilliseconds(200);

    // Contexto: si nuestro último mensaje hizo una pregunta del guion, una respuesta corta es la respuesta.
    private static readonly Regex AskedName = new(@"como se llama|nombre (del|de tu|de la)|decime (solo )?el nombre", RegexOptions.Compiled, T);
    private static readonly Regex AskedAny = new(@"\?|como (llevas|cobras|tomas|manejas)|cuantos|que (usas|sistema|rubro)", RegexOptions.Compiled, T);

    private readonly IReadOnlyList<Rule> _rules;

    public IntentMatcher(IEnumerable<(string Key, string Pattern, int? MaxWords)> rules, ILogger? log = null)
    {
        var list = new List<Rule>();
        foreach (var (key, pattern, maxWords) in rules)
        {
            try { list.Add(new Rule(key, new Regex(pattern, RegexOptions.Compiled, T), maxWords)); }
            catch (ArgumentException ex) { log?.LogWarning(ex, "Patrón inválido en el diccionario ({Key}), se ignora", key); }
        }
        _rules = list;
    }

    /// <param name="previousOutbound">Nuestro último mensaje antes de este (para las reglas de contexto).</param>
    public string Classify(string? text, string? previousOutbound)
    {
        var t = IntentText.Normalize(text);
        if (t.Length == 0) return Empty;
        var words = IntentText.WordCount(t);
        foreach (var r in _rules)
        {
            if (r.MaxWords is { } max && words > max) continue;
            try { if (r.Pattern.IsMatch(t)) return r.Key; }
            catch (RegexMatchTimeoutException) { /* patrón patológico con este texto: se saltea */ }
        }
        if (!string.IsNullOrWhiteSpace(previousOutbound))
        {
            var p = IntentText.Normalize(previousOutbound);
            if (AskedName.IsMatch(p) && words <= 6) return Qualification;
            if (AskedAny.IsMatch(p) && words <= 4) return Qualification;
        }
        return Other;
    }
}

/// <summary>
/// El diccionario en vivo: carga reply_intents (caché 60 s, Invalidate al editar) y clasifica.
/// Si la tabla está vacía la siembra con el diccionario del análisis (recurso embebido).
/// </summary>
public class IntentClassifier
{
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<IntentClassifier> _log;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private IntentMatcher? _matcher;
    private DateTimeOffset _expires = DateTimeOffset.MinValue;

    public IntentClassifier(IServiceScopeFactory scopes, ILogger<IntentClassifier> log)
    {
        _scopes = scopes; _log = log;
    }

    public void Invalidate() => _expires = DateTimeOffset.MinValue;

    public async Task<string> ClassifyAsync(string? text, string? previousOutbound, CancellationToken ct = default)
        => (await GetMatcherAsync(ct)).Classify(text, previousOutbound);

    public async Task<IntentMatcher> GetMatcherAsync(CancellationToken ct = default)
    {
        if (_matcher is not null && DateTimeOffset.UtcNow < _expires) return _matcher;
        await _lock.WaitAsync(ct);
        try
        {
            if (_matcher is not null && DateTimeOffset.UtcNow < _expires) return _matcher;
            using var scope = _scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            await SeedIfEmptyAsync(db, ct);
            var rows = await db.ReplyIntents.AsNoTracking()
                .Where(i => i.Enabled)
                .OrderBy(i => i.SortOrder)
                .Select(i => new { i.Key, i.Pattern, i.MaxWords })
                .ToListAsync(ct);
            _matcher = new IntentMatcher(rows.Select(r => (r.Key, r.Pattern, r.MaxWords)), _log);
            _expires = DateTimeOffset.UtcNow.AddSeconds(60);
            return _matcher;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex, "No pude cargar el diccionario de tipos de mensaje");
            return _matcher ?? new IntentMatcher(Array.Empty<(string, string, int?)>());
        }
        finally { _lock.Release(); }
    }

    /// <summary>Diccionario del análisis (docs/conversaciones/v2/build_dict.py) embebido en el ensamblado.</summary>
    public static List<ReplyIntent> LoadSeed()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("intent-dictionary.json")
            ?? throw new InvalidOperationException("Falta el recurso intent-dictionary.json");
        using var doc = JsonDocument.Parse(stream);
        var result = new List<ReplyIntent>();
        var order = 0;
        foreach (var i in doc.RootElement.GetProperty("intents").EnumerateArray())
        {
            string? Str(string name) => i.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            result.Add(new ReplyIntent
            {
                Id = Guid.NewGuid(),
                Key = Str("key")!,
                Name = Str("name") ?? Str("key")!,
                Pattern = Str("pattern")!,
                MaxWords = i.TryGetProperty("max_words", out var mw) && mw.ValueKind == JsonValueKind.Number ? mw.GetInt32() : null,
                Action = Str("action") ?? "",
                Reply = Str("reply"),
                Note = Str("note"),
                Examples = i.TryGetProperty("examples", out var ex) && ex.ValueKind == JsonValueKind.Array
                    ? ex.EnumerateArray().Select(e => e.GetString() ?? "").Where(e => e.Length > 0).ToList()
                    : new(),
                SortOrder = (order += 10),
                Enabled = true,
                AutoReply = false,
            });
        }
        return result;
    }

    private async Task SeedIfEmptyAsync(ApplicationDbContext db, CancellationToken ct)
    {
        if (await db.ReplyIntents.AnyAsync(ct)) return;
        var seed = LoadSeed();
        db.ReplyIntents.AddRange(seed);
        await db.SaveChangesAsync(ct);
        _log.LogInformation("Diccionario de tipos de mensaje sembrado: {N} tipos", seed.Count);
    }
}
