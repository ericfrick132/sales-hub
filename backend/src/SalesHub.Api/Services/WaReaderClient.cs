using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc;

namespace SalesHub.Api.Services;

/// <summary>
/// Cliente del servicio <c>wa-reader</c>: el que se vincula a un celular por QR y devuelve sus
/// chats con el teléfono real de cada uno. Vive aparte porque el protocolo de WhatsApp Web sólo
/// existe en Node (Baileys) y la Evolution de producción empaqueta una versión que descarta ese
/// número; el front no lo toca nunca — pasa siempre por acá, que es quien tiene la clave.
/// </summary>
public class WaReaderClient
{
    public const string ReaderHeader = "X-Reader-Key";

    private readonly HttpClient _http;
    private readonly string _key;
    private readonly ILogger<WaReaderClient> _log;

    public WaReaderClient(HttpClient http, IConfiguration config, ILogger<WaReaderClient> log)
    {
        _http = http;
        _key = config["WaReader:Key"] ?? "";
        _log = log;
        var baseUrl = config["WaReader:BaseUrl"] ?? "http://wa-reader:8090";
        _http.BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/");
        _http.Timeout = TimeSpan.FromSeconds(30);
    }

    /// <summary>¿Este request viene del lector (y no de un navegador)?</summary>
    public bool IsReader(HttpRequest request) =>
        !string.IsNullOrEmpty(_key)
        && request.Headers.TryGetValue(ReaderHeader, out var sent)
        && string.Equals(sent.ToString(), _key, StringComparison.Ordinal);

    /// <summary>
    /// Reenvía la llamada al lector tal cual y devuelve su respuesta. Si el servicio no está
    /// arriba se dice con todas las letras: es un contenedor aparte y puede faltar.
    /// </summary>
    public async Task<IActionResult> ProxyAsync(HttpMethod method, string path, CancellationToken ct, object? body = null)
    {
        if (string.IsNullOrEmpty(_key))
            return new ObjectResult(new { error = "El lector de chats no está configurado (falta WaReader__Key)." }) { StatusCode = 503 };

        try
        {
            using var req = new HttpRequestMessage(method, path);
            req.Headers.Add(ReaderHeader, _key);
            if (body is not null) req.Content = JsonContent.Create(body);
            using var res = await _http.SendAsync(req, ct);
            var payload = await res.Content.ReadAsStringAsync(ct);
            return new ContentResult
            {
                StatusCode = (int)res.StatusCode,
                Content = string.IsNullOrWhiteSpace(payload) ? "{}" : payload,
                ContentType = "application/json"
            };
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "wa-reader no respondió en {Path}", path);
            return new ObjectResult(new { error = "El lector de chats no responde. Probá de nuevo en un rato." }) { StatusCode = 502 };
        }
    }
}
