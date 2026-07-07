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

    /// <summary>
    /// Receta para que Claude escriba prompts de VIDEO de calidad (reemplaza la "magia"
    /// de apps tipo Higgsfield: la ponemos en el system prompt y se la pasamos a fal.ai).
    /// </summary>
    /// <summary>
    /// El default global (400, pensado para respuestas de venta cortas) trunca el JSON
    /// del posteo (concepto + prompt cinematográfico + caption + hashtags) → parse error.
    /// </summary>
    private const int SocialMaxTokens = 1200;

    /// <summary>
    /// Los modelos de imagen rompen el texto largo: el ÚNICO texto que va en la imagen
    /// es el campo 'overlay' — un gancho corto de copy, no el concepto interno.
    /// </summary>
    private const string OverlayRule =
        "REGLA DEL TEXTO EN LA IMAGEN: 'overlay' es el ÚNICO texto que puede aparecer estampado en la imagen. " +
        "Máximo 8 palabras, en español rioplatense, escrito como copy publicitario (gancho), ortografía PERFECTA. " +
        "Si la imagen es más fuerte sin texto, devolvé overlay = \"\" (string vacío). " +
        "El 'prompt' visual NO debe pedir ningún otro texto escrito, ni pantallas/planillas/carteles con texto legible.";

    private const string VideoPromptRecipe =
        "IMPORTANTE — si el assetKind es 'video', el campo 'prompt' (en INGLÉS) tiene que ser un prompt de video bien armado con esta estructura: " +
        "[sujeto + escena] + [acción/movimiento concreto] + [movimiento de cámara: dolly in/pan/handheld/orbit/static] + " +
        "[tipo de plano: wide/medium/close-up] + [luz y mood] + [estilo visual] + [ritmo]. " +
        "Es para un clip VERTICAL corto (~5s) con un hook visual fuerte en el primer segundo. No pidas texto en pantalla largo.";

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
        sys.AppendLine("{\"pillar\":string, \"assetKind\":\"image\"|\"video\", \"format\":\"post\"|\"story\"|\"reel\"|\"carousel\", \"concept\":string, \"prompt\":string, \"overlay\":string, \"caption\":string, \"hashtags\":string[]}");
        sys.AppendLine(OverlayRule);
        sys.AppendLine(VideoPromptRecipe);

        var user = new StringBuilder();
        user.AppendLine("Generá 1 idea de posteo nueva. Elegí un pilar y un formato adecuado.");
        if (recentConcepts.Count > 0)
        {
            user.AppendLine("Evitá repetir estos conceptos recientes:");
            foreach (var c in recentConcepts.Take(15)) user.AppendLine($"- {c}");
        }
        user.AppendLine("Recordá: SOLO el JSON.");

        var raw = await _claude.CompleteAsync(sys.ToString(), user.ToString(), SocialMaxTokens, null, "social", ct);
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
                Overlay: Str(r, "overlay"),
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
        sys.AppendLine("Devolvés EXCLUSIVAMENTE un JSON válido, sin markdown, con: {\"pillar\":string, \"concept\":string, \"prompt\":string, \"overlay\":string, \"caption\":string, \"hashtags\":string[]}");
        sys.AppendLine(OverlayRule);
        if (ch.AssetKind == SocialAssetKind.Video) sys.AppendLine(VideoPromptRecipe);

        var user = new StringBuilder();
        user.AppendLine($"Generá 1 idea nueva para {ch.Platform} ({ch.Format}).");
        if (recentConcepts.Count > 0)
        {
            user.AppendLine("Evitá repetir estos conceptos recientes:");
            foreach (var c in recentConcepts.Take(15)) user.AppendLine($"- {c}");
        }
        user.AppendLine("Recordá: SOLO el JSON.");

        var raw = await _claude.CompleteAsync(sys.ToString(), user.ToString(), SocialMaxTokens, null, "social", ct);
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
                Overlay: Str(r, "overlay"),
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

    /// <summary>
    /// Genera una adaptación ORIGINAL inspirada en un post de la competencia que el
    /// usuario marcó como "me gusta esto, replicalo". Toma el hook/ángulo/estructura
    /// del post de referencia pero lo reescribe para NUESTRA marca y producto (no es
    /// una copia, no menciona al competidor). Si se pasa un canal, fija formato/asset.
    /// </summary>
    public async Task<GeneratedPost?> GenerateFromInspirationAsync(
        PostingProfile p, PostingChannel? ch,
        string inspirationCaption, IReadOnlyList<string> inspirationHashtags, string inspirationSource,
        IReadOnlyList<ClaudeImage>? inspirationImages,
        IReadOnlyList<string> recentConcepts, CancellationToken ct = default)
    {
        if (!_claude.IsConfigured) { _log.LogWarning("Claude no configurado — no se genera inspiración"); return null; }

        var platform = ch?.Platform.ToString() ?? "Instagram/TikTok";
        var sys = new StringBuilder();
        AppendBrandBase(sys, p, platform, ch);
        sys.AppendLine();
        sys.AppendLine("Te paso un posteo de la COMPETENCIA que funcionó bien. Tu tarea: crear una adaptación ORIGINAL para NUESTRA marca.");
        sys.AppendLine("Tomá el hook, el ángulo y la estructura que lo hacen bueno, pero reescribilo 100% para nuestro producto y audiencia.");
        if (inspirationImages is { Count: > 0 })
            sys.AppendLine("MIRÁ la imagen de referencia: analizá qué la hace atractiva (composición, colores, tipografía, mood) y reflejá ese ESTILO VISUAL en el 'prompt' — adaptado a nuestra paleta de marca, sin copiarla.");
        sys.AppendLine("NO lo copies textual. NO menciones ni nombres al competidor. Caption en español rioplatense (voseo).");
        sys.AppendLine("El 'prompt' (para generar el visual) va en INGLÉS, detallado y cinematográfico, respetando la paleta de marca.");
        if (ch != null)
            sys.AppendLine($"El formato es {ch.Format} y el asset es {ch.AssetKind} (NO los cambies).");
        sys.AppendLine("Devolvés EXCLUSIVAMENTE un JSON válido, sin markdown, con: {\"pillar\":string, \"assetKind\":\"image\"|\"video\", \"format\":\"post\"|\"story\"|\"reel\"|\"carousel\", \"concept\":string, \"prompt\":string, \"overlay\":string, \"caption\":string, \"hashtags\":string[]}");
        sys.AppendLine(OverlayRule);
        sys.AppendLine(VideoPromptRecipe);

        var user = new StringBuilder();
        user.AppendLine("=== POSTEO DE REFERENCIA (competencia) ===");
        user.AppendLine($"Red de origen: {inspirationSource}");
        user.AppendLine($"Caption: {Truncate(inspirationCaption, 1200)}");
        if (inspirationHashtags.Count > 0)
            user.AppendLine($"Hashtags: {string.Join(" ", inspirationHashtags.Take(20).Select(t => "#" + t.TrimStart('#')))}");
        if (inspirationImages is { Count: > 0 })
            user.AppendLine("(la imagen del posteo va adjunta)");
        user.AppendLine("=== FIN REFERENCIA ===");
        user.AppendLine();
        user.AppendLine("Generá 1 adaptación original para nuestra marca basada en lo que hace bueno a ese posteo.");
        AppendRecent(user, recentConcepts);
        user.AppendLine("Recordá: SOLO el JSON.");

        var raw = await _claude.CompleteAsync(sys.ToString(), user.ToString(), inspirationImages, SocialMaxTokens, null, "social", ct);
        return Parse(raw, ch, "inspiración");
    }

    /// <summary>
    /// Genera desde las inspiraciones PROPIAS del usuario (imágenes/notas que mandó
    /// por WhatsApp o subió a la web, agrupadas por tema). Las referencias inspiran
    /// DOS cosas: el tema/ángulo del caption y el estilo visual del prompt.
    /// </summary>
    public async Task<GeneratedPost?> GenerateFromOwnInspirationAsync(
        PostingProfile p, PostingChannel? ch,
        string topic, IReadOnlyList<string> notes,
        IReadOnlyList<ClaudeImage> images,
        IReadOnlyList<string> recentConcepts, CancellationToken ct = default)
    {
        if (!_claude.IsConfigured) { _log.LogWarning("Claude no configurado — no se genera inspiración propia"); return null; }

        var platform = ch?.Platform.ToString() ?? "Instagram/TikTok";
        var sys = new StringBuilder();
        AppendBrandBase(sys, p, platform, ch);
        sys.AppendLine();
        sys.AppendLine("Te paso INSPIRACIONES que el dueño de la marca guardó porque le gustaron (imágenes y/o notas, agrupadas por tema).");
        sys.AppendLine("Usalas en DOS frentes:");
        sys.AppendLine("1) TEMA: sacá el ángulo/idea de qué hablar en el caption a partir del tema y las referencias.");
        sys.AppendLine("2) VISUAL: si hay imágenes, MIRALAS y analizá qué las hace atractivas (composición, colores, tipografía, mood, estética) y llevá ese estilo al 'prompt' — adaptado a nuestra paleta de marca, NO una copia.");
        sys.AppendLine("El resultado tiene que ser contenido 100% original de nuestra marca. Caption en español rioplatense (voseo).");
        sys.AppendLine("El 'prompt' (para generar el visual) va en INGLÉS, detallado y cinematográfico.");
        if (ch != null)
            sys.AppendLine($"El formato es {ch.Format} y el asset es {ch.AssetKind} (NO los cambies).");
        sys.AppendLine("Devolvés EXCLUSIVAMENTE un JSON válido, sin markdown, con: {\"pillar\":string, \"assetKind\":\"image\"|\"video\", \"format\":\"post\"|\"story\"|\"reel\"|\"carousel\", \"concept\":string, \"prompt\":string, \"overlay\":string, \"caption\":string, \"hashtags\":string[]}");
        sys.AppendLine(OverlayRule);
        sys.AppendLine(VideoPromptRecipe);

        var user = new StringBuilder();
        user.AppendLine($"=== INSPIRACIONES DEL USUARIO — tema: \"{topic}\" ===");
        if (images.Count > 0) user.AppendLine($"(adjunto {images.Count} imagen(es) de referencia)");
        foreach (var n in notes.Where(n => !string.IsNullOrWhiteSpace(n)).Take(10))
            user.AppendLine($"- Nota: {Truncate(n, 500)}");
        user.AppendLine("=== FIN INSPIRACIONES ===");
        user.AppendLine();
        user.AppendLine("Generá 1 idea de posteo original de nuestra marca inspirada en esas referencias (tema + estilo visual).");
        AppendRecent(user, recentConcepts);
        user.AppendLine("Recordá: SOLO el JSON.");

        var raw = await _claude.CompleteAsync(sys.ToString(), user.ToString(), images, SocialMaxTokens, null, "social", ct);
        return Parse(raw, ch, $"inspiración propia '{topic}'");
    }

    /// <summary>Base de marca compartida por los prompts de inspiración.</summary>
    private static void AppendBrandBase(StringBuilder sys, PostingProfile p, string platform, PostingChannel? ch)
    {
        sys.AppendLine($"Sos el generador de contenido social del producto '{p.ProductKey}' para la red {platform}.");
        sys.AppendLine($"Audiencia: {p.TargetAudience}.");
        sys.AppendLine($"Tono/voz de marca: {p.BrandVoice}");
        sys.AppendLine($"Guía de marca: {p.BrandGuidelines}");
        sys.AppendLine($"Paleta (no la cambies): {p.BrandColorsJson}. Fuentes: {p.BrandFonts}.");
        if (p.ContentPillars.Count > 0)
            sys.AppendLine($"Pilares de contenido: {string.Join(" | ", p.ContentPillars)}.");
        if (ch != null && !string.IsNullOrWhiteSpace(ch.PromptTemplate))
        {
            sys.AppendLine();
            sys.AppendLine("INSTRUCCIONES ESPECÍFICAS DE ESTA RED:");
            sys.AppendLine(ch.PromptTemplate);
        }
    }

    private static void AppendRecent(StringBuilder user, IReadOnlyList<string> recentConcepts)
    {
        if (recentConcepts.Count == 0) return;
        user.AppendLine("Evitá repetir estos conceptos recientes nuestros:");
        foreach (var c in recentConcepts.Take(15)) user.AppendLine($"- {c}");
    }

    /// <summary>Parsea la respuesta JSON de Claude a un GeneratedPost (canal manda formato/asset).</summary>
    private GeneratedPost? Parse(string? raw, PostingChannel? ch, string context)
    {
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
                // Si hay canal, manda el canal; si no, lo que eligió Claude.
                AssetKind: ch != null ? (ch.AssetKind == SocialAssetKind.Video ? "video" : "image") : Str(r, "assetKind", "image").ToLowerInvariant(),
                Format: ch != null ? ch.Format.ToString().ToLowerInvariant() : Str(r, "format", "post").ToLowerInvariant(),
                Concept: Str(r, "concept"),
                Prompt: Str(r, "prompt"),
                Overlay: Str(r, "overlay"),
                Caption: Str(r, "caption"),
                Hashtags: hashtags,
                RawJson: json);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "No pude parsear JSON de Claude ({Context})", context);
            return null;
        }
    }

    private static string Truncate(string s, int n) => string.IsNullOrEmpty(s) || s.Length <= n ? (s ?? "") : s[..n] + "…";

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
    string Overlay,
    string Caption,
    List<string> Hashtags,
    string RawJson);
