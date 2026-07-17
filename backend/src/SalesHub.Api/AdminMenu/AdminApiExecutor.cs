using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SalesHub.Api.Auth;
using SalesHub.Core.Domain.Enums;
using SalesHub.Infrastructure.Persistence;

namespace SalesHub.Api.AdminMenu;

/// <summary>
/// Ejecuta operaciones contra la PROPIA API (http://localhost:8080) como admin. Mintea un JWT con
/// <see cref="IJwtService"/> (mismo issuer/audience/key que valida la API) para un Seller con
/// Role=Admin, así reusa TODA la autorización ([Authorize(Roles=Admin)] + los CurrentUser.IsAdmin
/// inline) y la validación de cada endpoint — sin re-implementar nada. Corre en el proceso de la
/// API (el webhook lo invoca), por eso localhost:8080 es alcanzable.
/// </summary>
public class AdminApiExecutor
{
    private readonly HttpClient _http;
    private readonly IServiceScopeFactory _scopes;

    // Token cacheado (se mintea con 7 días; lo refrescamos cada 6h para no acercarnos al vencimiento).
    private static string? _token;
    private static DateTimeOffset _tokenUntil;

    public AdminApiExecutor(HttpClient http, IConfiguration cfg, IServiceScopeFactory scopes)
    {
        _http = http;
        _scopes = scopes;
        var baseUrl = cfg["AdminAgent:SelfBaseUrl"];
        if (string.IsNullOrWhiteSpace(baseUrl)) baseUrl = "http://localhost:8080";
        _http.BaseAddress = new Uri(baseUrl);
        _http.Timeout = TimeSpan.FromSeconds(30);
    }

    private async Task<string?> TokenAsync(CancellationToken ct)
    {
        if (_token is not null && DateTimeOffset.UtcNow < _tokenUntil) return _token;
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var jwt = scope.ServiceProvider.GetRequiredService<IJwtService>();
        var admin = await db.Sellers
            .Where(s => s.Role == SellerRole.Admin && s.IsActive)
            .OrderBy(s => s.CreatedAt)
            .FirstOrDefaultAsync(ct);
        if (admin is null) return null;
        _token = jwt.Issue(admin);
        _tokenUntil = DateTimeOffset.UtcNow.AddHours(6);
        return _token;
    }

    /// <summary>Ejecuta un request y devuelve (status HTTP, cuerpo crudo). Nunca tira: los errores vuelven en el status.</summary>
    public async Task<(int status, string body)> SendAsync(string method, string path, object? body, CancellationToken ct)
    {
        try
        {
            var token = await TokenAsync(ct);
            if (token is null) return (0, "no hay un Seller admin para autenticar el agente");

            using var req = new HttpRequestMessage(new HttpMethod(method.ToUpperInvariant()), path);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            if (body is not null && method.ToUpperInvariant() is "POST" or "PUT" or "PATCH")
                req.Content = JsonContent.Create(body);

            using var resp = await _http.SendAsync(req, ct);
            var text = await resp.Content.ReadAsStringAsync(ct);
            return ((int)resp.StatusCode, text);
        }
        catch (Exception ex)
        {
            return (0, ex.Message);
        }
    }

    /// <summary>GET que devuelve el JSON parseado (o null si falló). Para armar sub-menús/inputs dinámicos.</summary>
    public async Task<JsonElement?> GetJsonAsync(string path, CancellationToken ct)
    {
        var (status, body) = await SendAsync("GET", path, null, ct);
        if (status is < 200 or >= 300 || string.IsNullOrWhiteSpace(body)) return null;
        try { return JsonDocument.Parse(body).RootElement.Clone(); }
        catch { return null; }
    }

    public static bool IsOk(int status) => status is >= 200 and < 300;
}
