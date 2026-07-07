using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SalesHub.Infrastructure.Options;

namespace SalesHub.Infrastructure.Services.Social;

/// <summary>
/// Cliente mínimo de ElevenLabs Text-to-Speech: texto (guion de narración) → mp3.
/// Se usa para ponerle voz en off a los videos de fal.ai. Devuelve null si no hay
/// key o si la API falla (el video igual sale, mudo).
/// </summary>
public class ElevenLabsClient
{
    private readonly HttpClient _http;
    private readonly ElevenLabsOptions _opts;
    private readonly ILogger<ElevenLabsClient> _log;

    public ElevenLabsClient(HttpClient http, IOptions<ElevenLabsOptions> opts, ILogger<ElevenLabsClient> log)
    {
        _http = http;
        _opts = opts.Value;
        _log = log;
        _http.BaseAddress = new Uri(_opts.BaseUrl.TrimEnd('/') + "/");
        _http.Timeout = TimeSpan.FromSeconds(_opts.TimeoutSeconds);
        if (!string.IsNullOrWhiteSpace(_opts.ApiKey))
            _http.DefaultRequestHeaders.Add("xi-api-key", _opts.ApiKey);
    }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_opts.ApiKey);

    /// <summary>Genera el mp3 de la narración. <paramref name="voiceId"/> opcional (override del default).</summary>
    public async Task<byte[]?> SynthesizeAsync(string text, string? voiceId = null, CancellationToken ct = default)
    {
        if (!IsConfigured) { _log.LogWarning("ElevenLabs sin API key — no se genera narración"); return null; }
        if (string.IsNullOrWhiteSpace(text)) return null;

        var voice = string.IsNullOrWhiteSpace(voiceId) ? _opts.VoiceId : voiceId;
        var body = new
        {
            text,
            model_id = _opts.ModelId,
            voice_settings = new { stability = 0.5, similarity_boost = 0.75, style = 0.0, use_speaker_boost = true },
        };

        try
        {
            var req = new HttpRequestMessage(HttpMethod.Post, $"v1/text-to-speech/{voice}")
            {
                Content = JsonContent.Create(body),
            };
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("audio/mpeg"));
            var resp = await _http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode)
            {
                var err = await resp.Content.ReadAsStringAsync(ct);
                _log.LogWarning("ElevenLabs TTS falló: {Status} {Body}", resp.StatusCode, err[..Math.Min(err.Length, 300)]);
                return null;
            }
            var bytes = await resp.Content.ReadAsByteArrayAsync(ct);
            return bytes is { Length: > 0 } ? bytes : null;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "ElevenLabs TTS excepción");
            return null;
        }
    }
}
