using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SalesHub.Infrastructure.Options;

namespace SalesHub.Infrastructure.Services;

/// <summary>
/// Cliente mínimo de la API de GitHub para crear/actualizar archivos en un repo
/// (Contents API). Lo usa el distribuidor de blog para pushear HTML estático a
/// cada app; DO App Platform redeploya solo en el push.
/// </summary>
public class GitHubContentClient
{
    private readonly HttpClient _http;
    private readonly GitHubOptions _opts;
    private readonly ILogger<GitHubContentClient> _log;

    public GitHubContentClient(HttpClient http, IOptions<GitHubOptions> opts, ILogger<GitHubContentClient> log)
    {
        _http = http;
        _opts = opts.Value;
        _log = log;
        _http.BaseAddress = new Uri(_opts.ApiBase.TrimEnd('/') + "/");
        _http.Timeout = TimeSpan.FromSeconds(60);
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("SalesHub-SEO/1.0");
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        if (!string.IsNullOrWhiteSpace(_opts.Token))
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _opts.Token);
    }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_opts.Token);

    /// <summary>Obtiene el SHA actual de un archivo (null si no existe). Necesario para actualizarlo.</summary>
    private async Task<string?> GetShaAsync(string repo, string path, string branch, CancellationToken ct)
    {
        var resp = await _http.GetAsync($"repos/{repo}/contents/{Uri.EscapeDataString(path).Replace("%2F", "/")}?ref={Uri.EscapeDataString(branch)}", ct);
        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        if (!resp.IsSuccessStatusCode) return null;
        using var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        return doc.RootElement.TryGetProperty("sha", out var sha) ? sha.GetString() : null;
    }

    /// <summary>
    /// Crea o actualiza un archivo de texto en el repo. Devuelve true si el commit
    /// se hizo (o si el contenido ya era idéntico, en cuyo caso no hace nada).
    /// </summary>
    public async Task<bool> PutFileAsync(string repo, string branch, string path, string content, string message, CancellationToken ct = default)
    {
        if (!IsConfigured) throw new InvalidOperationException("GitHub token no configurado (GitHub__Token)");

        var existingSha = await GetShaAsync(repo, path, branch, ct);
        var b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(content));

        var body = new Dictionary<string, object?>
        {
            ["message"] = message,
            ["content"] = b64,
            ["branch"] = branch,
            ["committer"] = new { name = _opts.CommitterName, email = _opts.CommitterEmail },
        };
        if (existingSha is not null) body["sha"] = existingSha;

        var resp = await _http.PutAsJsonAsync($"repos/{repo}/contents/{Uri.EscapeDataString(path).Replace("%2F", "/")}", body, ct);
        if (resp.StatusCode == System.Net.HttpStatusCode.UnprocessableEntity && existingSha is not null)
        {
            // 422: el contenido puede ser idéntico (no-op). Lo tratamos como éxito.
            _log.LogInformation("GitHub PUT {Repo}:{Path} 422 (sin cambios)", repo, path);
            return true;
        }
        if (!resp.IsSuccessStatusCode)
        {
            var err = await resp.Content.ReadAsStringAsync(ct);
            _log.LogWarning("GitHub PUT {Repo}:{Path} falló: {Status} {Body}", repo, path, resp.StatusCode, err);
            throw new InvalidOperationException($"GitHub PUT falló ({(int)resp.StatusCode}) para {repo}:{path}");
        }
        return true;
    }

    /// <summary>Lee el contenido de texto de un archivo del repo (null si no existe).</summary>
    public async Task<string?> GetFileTextAsync(string repo, string path, string branch, CancellationToken ct = default)
    {
        var resp = await _http.GetAsync($"repos/{repo}/contents/{Uri.EscapeDataString(path).Replace("%2F", "/")}?ref={Uri.EscapeDataString(branch)}", ct);
        if (!resp.IsSuccessStatusCode) return null;
        using var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        if (!doc.RootElement.TryGetProperty("content", out var c)) return null;
        var b64 = (c.GetString() ?? "").Replace("\n", "");
        try { return Encoding.UTF8.GetString(Convert.FromBase64String(b64)); } catch { return null; }
    }
}
