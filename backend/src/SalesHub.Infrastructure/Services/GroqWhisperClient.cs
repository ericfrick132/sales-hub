using System.Net.Http.Headers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SalesHub.Infrastructure.Options;

namespace SalesHub.Infrastructure.Services;

/// <summary>
/// Transcribe audio a texto vía la API de Groq (Whisper). Es OpenAI-compatible:
/// multipart con el archivo + model. Devuelve null si falla — el caller decide
/// si reintenta.
/// </summary>
public class GroqWhisperClient
{
    private readonly HttpClient _http;
    private readonly GroqOptions _opts;
    private readonly ILogger<GroqWhisperClient> _log;

    public GroqWhisperClient(HttpClient http, IOptions<GroqOptions> opts, ILogger<GroqWhisperClient> log)
    {
        _http = http;
        _opts = opts.Value;
        _log = log;
        _http.BaseAddress = new Uri(_opts.BaseUrl.TrimEnd('/') + "/");
        _http.Timeout = TimeSpan.FromSeconds(_opts.TimeoutSeconds);
        if (!string.IsNullOrWhiteSpace(_opts.ApiKey))
            _http.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", _opts.ApiKey);
    }

    /// <summary>True si hay API key — el worker no procesa nada si está sin configurar.</summary>
    public bool IsConfigured => !string.IsNullOrWhiteSpace(_opts.ApiKey);

    /// <summary>
    /// Transcribe los bytes de audio. <paramref name="fileName"/> sólo le da
    /// pista del formato a la API (ej. "voice.ogg"). Devuelve el texto, o null
    /// si falló o vino vacío.
    /// </summary>
    public async Task<string?> TranscribeAsync(byte[] audio, string fileName, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_opts.ApiKey))
        {
            _log.LogWarning("Groq ApiKey no configurada — no se puede transcribir");
            return null;
        }
        if (audio.Length == 0) return null;

        using var form = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent(audio);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        form.Add(fileContent, "file", fileName);
        form.Add(new StringContent(_opts.TranscriptionModel), "model");
        if (!string.IsNullOrWhiteSpace(_opts.Language))
            form.Add(new StringContent(_opts.Language), "language");
        form.Add(new StringContent("text"), "response_format");

        try
        {
            var resp = await _http.PostAsync("audio/transcriptions", form, ct);
            if (!resp.IsSuccessStatusCode)
            {
                var err = await resp.Content.ReadAsStringAsync(ct);
                _log.LogWarning("Groq transcription failed: {Status} {Body}", resp.StatusCode, err);
                return null;
            }
            // response_format=text → el body es texto plano.
            var text = await resp.Content.ReadAsStringAsync(ct);
            return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Groq transcription threw");
            return null;
        }
    }
}
