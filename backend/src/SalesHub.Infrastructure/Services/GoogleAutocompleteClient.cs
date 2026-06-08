using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace SalesHub.Infrastructure.Services;

/// <summary>
/// Fuente de keywords REALES y gratuita: el endpoint público de autocompletado de
/// Google (suggestqueries). Devuelve lo que la gente efectivamente tipea, sin costo
/// ni API key. No da volumen exacto (eso requiere DataForSEO/Ahrefs), pero sí las
/// frases reales — base honesta para que el modelo después agrupe y priorice.
/// </summary>
public class GoogleAutocompleteClient
{
    private readonly HttpClient _http;
    private readonly ILogger<GoogleAutocompleteClient> _log;

    public GoogleAutocompleteClient(HttpClient http, ILogger<GoogleAutocompleteClient> log)
    {
        _http = http;
        _log = log;
        _http.Timeout = TimeSpan.FromSeconds(15);
    }

    /// <summary>
    /// Trae sugerencias para una semilla. Expande con modificadores ("como", "para",
    /// "mejor", "precio", "vs") para sacar long-tail e intención comercial.
    /// </summary>
    public async Task<List<string>> SuggestAsync(string seed, string language = "es", string country = "ar", CancellationToken ct = default)
    {
        var seeds = new List<string> { seed };
        foreach (var mod in new[] { "como", "para", "mejor", "precio", "que es", "app", "vs" })
            seeds.Add($"{seed} {mod}");

        var results = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in seeds)
        {
            ct.ThrowIfCancellationRequested();
            foreach (var sug in await FetchOneAsync(s, language, country, ct))
                results.Add(sug);
        }
        return results.ToList();
    }

    private async Task<List<string>> FetchOneAsync(string query, string language, string country, CancellationToken ct)
    {
        var url = $"https://suggestqueries.google.com/complete/search?client=firefox&hl={Uri.EscapeDataString(language)}&gl={Uri.EscapeDataString(country)}&q={Uri.EscapeDataString(query)}";
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            // Sin User-Agent el endpoint a veces devuelve 403.
            req.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (compatible; SalesHubSEO/1.0)");
            var resp = await _http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode) return new();

            // Formato firefox: ["query", ["sug1","sug2",...]]
            var stream = await resp.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() < 2) return new();
            var arr = root[1];
            if (arr.ValueKind != JsonValueKind.Array) return new();

            var list = new List<string>();
            foreach (var item in arr.EnumerateArray())
            {
                var s = item.GetString();
                if (!string.IsNullOrWhiteSpace(s)) list.Add(s.Trim());
            }
            return list;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Google autocomplete falló para '{Query}'", query);
            return new();
        }
    }
}
