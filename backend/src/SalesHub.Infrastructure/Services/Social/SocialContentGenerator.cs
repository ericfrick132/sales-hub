using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using SalesHub.Core.Domain.Entities.Social;

namespace SalesHub.Infrastructure.Services.Social;

/// <summary>
/// Genera una idea de posteo con Claude a partir de la base de marca fija del
/// producto (colores/tono/pilares) + un historial reciente para no repetir.
/// La marca no varía; el concepto/prompt/caption sí.
/// </summary>
public class SocialContentGenerator
{
    private readonly ClaudeClient _claude;
    private readonly ILogger<SocialContentGenerator> _log;

    public SocialContentGenerator(ClaudeClient claude, ILogger<SocialContentGenerator> log)
    {
        _claude = claude;
        _log = log;
    }

    public bool IsConfigured => _claude.IsConfigured;

    public async Task<GeneratedPost?> GenerateAsync(PostingProfile p, IReadOnlyList<string> recentConcepts, CancellationToken ct = default)
    {
        if (!_claude.IsConfigured) { _log.LogWarning("Claude no configurado — no se genera contenido"); return null; }

        // System prompt = parte fija (marca) → se cachea entre llamadas del mismo producto.
        var sys = new StringBuilder();
        sys.AppendLine($"Sos el generador de contenido social del producto '{p.ProductKey}'.");
        sys.AppendLine($"Audiencia: {p.TargetAudience}.");
        sys.AppendLine($"Tono/voz de marca: {p.BrandVoice}");
        sys.AppendLine($"Guía de marca: {p.BrandGuidelines}");
        sys.AppendLine($"Paleta (no la cambies): {p.BrandColorsJson}. Fuentes: {p.BrandFonts}.");
        if (p.ContentPillars.Count > 0)
            sys.AppendLine($"Pilares de contenido: {string.Join(" | ", p.ContentPillars)}.");
        sys.AppendLine();
        sys.AppendLine("Generás ideas de posteo para redes (Instagram/TikTok). Respondés SIEMPRE en español rioplatense (voseo) para el caption.");
        sys.AppendLine("El campo 'prompt' (para generar el visual) va en INGLÉS, detallado y cinematográfico, respetando la paleta y estética de la marca.");
        sys.AppendLine("Devolvés EXCLUSIVAMENTE un objeto JSON válido, sin texto extra ni markdown, con estas claves:");
        sys.AppendLine("{\"pillar\":string, \"assetKind\":\"image\"|\"video\", \"format\":\"post\"|\"story\"|\"reel\"|\"carousel\", \"concept\":string, \"prompt\":string, \"caption\":string, \"hashtags\":string[]}");

        var user = new StringBuilder();
        user.AppendLine("Generá 1 idea de posteo nueva. Elegí un pilar y un formato adecuado.");
        if (recentConcepts.Count > 0)
        {
            user.AppendLine("Evitá repetir estos conceptos recientes:");
            foreach (var c in recentConcepts.Take(15)) user.AppendLine($"- {c}");
        }
        user.AppendLine("Recordá: SOLO el JSON.");

        var raw = await _claude.CompleteAsync(sys.ToString(), user.ToString(), "social", ct);
        if (string.IsNullOrWhiteSpace(raw)) return null;

        var json = ExtractJson(raw);
        try
        {
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            var hashtags = new List<string>();
            if (r.TryGetProperty("hashtags", out var h) && h.ValueKind == JsonValueKind.Array)
                hashtags.AddRange(h.EnumerateArray().Select(x => x.GetString() ?? "").Where(s => s.Length > 0));

            return new GeneratedPost(
                Pillar: Str(r, "pillar"),
                AssetKind: Str(r, "assetKind", "image").ToLowerInvariant(),
                Format: Str(r, "format", "post").ToLowerInvariant(),
                Concept: Str(r, "concept"),
                Prompt: Str(r, "prompt"),
                Caption: Str(r, "caption"),
                Hashtags: hashtags,
                RawJson: json);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "No pude parsear el JSON de Claude: {Raw}", raw[..Math.Min(raw.Length, 300)]);
            return null;
        }
    }

    /// <summary>
    /// Genera para una red específica usando el prompt propio de ese canal (red×app)
    /// + la base de marca del perfil. El formato/asset los fija el canal (no Claude).
    /// </summary>
    public async Task<GeneratedPost?> GenerateForChannelAsync(PostingProfile p, PostingChannel ch, IReadOnlyList<string> recentConcepts, CancellationToken ct = default)
    {
        if (!_claude.IsConfigured) { _log.LogWarning("Claude no configurado"); return null; }

        var sys = new StringBuilder();
        sys.AppendLine($"Sos el generador de contenido social del producto '{p.ProductKey}' para la red {ch.Platform}.");
        sys.AppendLine($"Audiencia: {p.TargetAudience}.");
        sys.AppendLine($"Tono/voz de marca: {p.BrandVoice}");
        sys.AppendLine($"Guía de marca: {p.BrandGuidelines}");
        sys.AppendLine($"Paleta (no la cambies): {p.BrandColorsJson}. Fuentes: {p.BrandFonts}.");
        if (p.ContentPillars.Count > 0)
            sys.AppendLine($"Pilares de contenido: {string.Join(" | ", p.ContentPillars)}.");
        sys.AppendLine();
        sys.AppendLine("INSTRUCCIONES ESPECÍFICAS DE ESTA RED:");
        sys.AppendLine(string.IsNullOrWhiteSpace(ch.PromptTemplate) ? "(sin instrucciones extra)" : ch.PromptTemplate);
        sys.AppendLine();
        sys.AppendLine($"El formato es {ch.Format} y el asset es {ch.AssetKind} (NO los cambies). Caption en español rioplatense (voseo). El 'prompt' (para generar el visual) va en INGLÉS, detallado y cinematográfico, respetando la paleta de marca.");
        sys.AppendLine("Devolvés EXCLUSIVAMENTE un JSON válido, sin markdown, con: {\"pillar\":string, \"concept\":string, \"prompt\":string, \"caption\":string, \"hashtags\":string[]}");

        var user = new StringBuilder();
        user.AppendLine($"Generá 1 idea nueva para {ch.Platform} ({ch.Format}).");
        if (recentConcepts.Count > 0)
        {
            user.AppendLine("Evitá repetir estos conceptos recientes:");
            foreach (var c in recentConcepts.Take(15)) user.AppendLine($"- {c}");
        }
        user.AppendLine("Recordá: SOLO el JSON.");

        var raw = await _claude.CompleteAsync(sys.ToString(), user.ToString(), "social", ct);
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var json = ExtractJson(raw);
        try
        {
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            var hashtags = new List<string>();
            if (r.TryGetProperty("hashtags", out var h) && h.ValueKind == JsonValueKind.Array)
                hashtags.AddRange(h.EnumerateArray().Select(x => x.GetString() ?? "").Where(s => s.Length > 0));
            return new GeneratedPost(
                Pillar: Str(r, "pillar"),
                AssetKind: ch.AssetKind == SocialAssetKind.Video ? "video" : "image",
                Format: ch.Format.ToString().ToLowerInvariant(),
                Concept: Str(r, "concept"),
                Prompt: Str(r, "prompt"),
                Caption: Str(r, "caption"),
                Hashtags: hashtags,
                RawJson: json);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "No pude parsear JSON de Claude (canal {Platform})", ch.Platform);
            return null;
        }
    }

    private static string Str(JsonElement e, string key, string def = "") =>
        e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? (v.GetString() ?? def) : def;

    /// <summary>Quita fences ```json … ``` si Claude los agrega y recorta al primer objeto.</summary>
    private static string ExtractJson(string raw)
    {
        var s = raw.Trim();
        if (s.StartsWith("```"))
        {
            var nl = s.IndexOf('\n');
            if (nl >= 0) s = s[(nl + 1)..];
            if (s.EndsWith("```")) s = s[..^3];
            s = s.Trim();
        }
        var start = s.IndexOf('{');
        var end = s.LastIndexOf('}');
        return start >= 0 && end > start ? s[start..(end + 1)] : s;
    }
}

public record GeneratedPost(
    string Pillar,
    string AssetKind,
    string Format,
    string Concept,
    string Prompt,
    string Caption,
    List<string> Hashtags,
    string RawJson);
