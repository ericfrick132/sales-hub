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
    private const int SocialMaxTokens = 3000;

    /// <summary>
    /// Los modelos de imagen rompen el texto largo: el ÚNICO texto que va en la imagen
    /// es el campo 'overlay' — un gancho corto de copy, no el concepto interno.
    /// </summary>
    private const string OverlayRule =
        "REGLA DEL TEXTO EN LA IMAGEN: 'overlay' es el ÚNICO texto que puede aparecer estampado en la imagen. " +
        "Máximo 8 palabras, en español rioplatense, escrito como copy publicitario (gancho), ortografía PERFECTA. " +
        "Si la imagen es más fuerte sin texto, devolvé overlay = \"\" (string vacío). " +
        "El 'prompt' visual NO debe pedir ningún otro texto escrito, ni pantallas/planillas/carteles con texto legible.";

    /// <summary>
    /// Términos vetados por el dueño de la marca: no se usan en NINGÚN texto generado.
    /// </summary>
    private const string BannedWordsRule =
        "PALABRA PROHIBIDA: jamás uses 'no-show', 'no show' ni 'no-shows' en ningún campo (caption, overlay, concepto, narración, prompt, hashtags). " +
        "Decilo en español: 'ausencias', 'turnos que quedan vacíos', 'clientes que no vienen', 'reservas sin usar'.";

    /// <summary>
    /// Arco narrativo obligatorio de TODO posteo: gancho que frena el scroll →
    /// desarrollo con valor concreto → llamado a la acción. Aplica al caption
    /// (single) y al reparto de roles de las slides (multi).
    /// </summary>
    private const string StructureRule =
        "ESTRUCTURA OBLIGATORIA HOOK → DESARROLLO → CTA: el caption arranca con UNA línea de GANCHO que frene el scroll " +
        "(Instagram corta ahí el 'ver más', esa línea tiene que obligar a abrir; seguí el ESTILO DE GANCHO indicado y NO abras siempre con una pregunta retórica tipo '¿cuántas veces...?'), " +
        "sigue el DESARROLLO (2-4 líneas cortas con valor real: el dolor, el dato, el beneficio — sin humo), " +
        "y cierra SIEMPRE con un CTA claro y corto ('Probalo gratis — link en bio', '¿Empezamos hoy? Escribinos', 'Link en bio'). " +
        "Sin CTA no hay posteo.";

    /// <summary>
    /// Lo MÁS importante del visual: que el 'prompt' describa una ESCENA REAL, larga y
    /// específica, con personas — y que NO parezca IA (el fondo falso/plástico es lo peor).
    /// </summary>
    private const string PhotoRealismRule =
        "REGLA DE FOTORREALISMO (lo MÁS importante del visual): salvo que el modo elegido sea claramente un mockup de app, un poster " +
        "tipográfico o un gráfico de marca, el 'prompt' describe UNA FOTOGRAFÍA REAL — editorial/documental, tomada con cámara, NO un render ni una ilustración. " +
        "Escribí el 'prompt' LARGO y específico, como indicaciones a un fotógrafo (no seas escueto): " +
        "PERSONAS reales, diversas, argentinas/latinas, con imperfecciones humanas naturales (piel con textura y poros, no plástica), haciendo una acción concreta y creíble del momento; " +
        "el LUGAR exacto del rubro con detalles AUTÉNTICOS de fondo (objetos gastados, señalética en español, contexto real, algo de desorden natural) — un fondo real y creíble, jamás vacío ni genérico; " +
        "la LUZ real (natural, hora del día, dirección, sombras reales); y la cámara ('shot on 35mm, f/2, natural light, editorial documentary photography, photorealistic, sharp focus with natural depth of field'). " +
        "PROHIBIDO EL 'LOOK IA' (es lo peor): nada de fondos surreales o imposibles, manos/dedos deformes, texto o logos deformados, superficies sobre-suavizadas o plásticas, simetría perfecta irreal, colores fluor irreales, ni gente de stock sonriendo forzado a cámara. " +
        "El FONDO tiene que verse 100% real y del lugar. Cuanto más específica, humana y detallada la escena, mejor.";

    /// <summary>
    /// Receta "majestic" de video: en vez de una fórmula genérica (que produce el
    /// look IA), le pedimos a Claude DIRECCIÓN DE CINE — una micro-historia real
    /// alrededor del mundo del cliente, con el dolor contado de costado.
    /// </summary>
    /// <summary>Guion de voz en off para los videos (se sintetiza con ElevenLabs).</summary>
    private const string NarrationRule =
        "NARRACIÓN — si el assetKind es 'video', agregá el campo 'narration': un guion de VOZ EN OFF en español rioplatense (voseo), " +
        "de 22 a 28 palabras (entra en ~10 segundos hablados), que cuente la misma micro-historia del video sin describir lo que se ve. " +
        "Tono de la marca, cálido y natural como un amigo que te cuenta algo, NO como publicidad gritada. Cerrá con una idea que deje pensando (no un CTA explícito tipo 'comprá ya'). " +
        "Sin emojis, sin hashtags, sin URLs — es para leer en voz alta. Si el assetKind es 'image', devolvé narration = \"\".";

    /// <summary>
    /// Regla para STORIES: son verticales, efímeras (24h) y más íntimas/casuales que el
    /// feed. Menos producidas, más "momento". El overlay es clave (la story vive del texto
    /// grande arriba de la imagen), y va bárbaro para una pregunta o un guiño rápido.
    /// </summary>
    private const string StoryRule =
        "ES UNA STORY (vertical 9:16, efímera 24h): tono más casual e íntimo que el feed, como un mensaje al toque. " +
        "El 'overlay' es PROTAGONISTA (la story se lee de ese texto grande) — que sea un gancho, pregunta o guiño corto. " +
        "El caption es breve. El visual es simple, con aire arriba y abajo para el texto, no una placa recargada. " +
        "Ideal para preguntas, un tip rápido o un momento del día — no un anuncio formal.";

    private const string VideoPromptRecipe =
        "REGLA DEL PROMPT DE VIDEO — si el assetKind es 'video', el campo 'prompt' (en INGLÉS) es dirección de cine para UNA sola toma continua, vertical 9:16, ~5 segundos. Escribilo así:\n" +
        "1) HISTORIA: un micro-arco (situación → giro → remate) ambientado en el MUNDO REAL del cliente de esta app. Pensá cuál es su dolor de fondo, pero NO lo muestres literal ni lo nombres: contalo de costado — un detalle, una consecuencia, el momento de calma u orgullo que el producto hace posible. La historia insinúa; nunca 'vende'.\n" +
        "2) SUJETO ÚNICO con textura de persona real: edad aproximada, ropa creíble, un gesto específico. IMPORTANTE — la persona y el lugar tienen que leerse LATINOAMERICANOS / rioplatenses (rasgos latinos, carteles y objetos en ESPAÑOL, ambientación de Argentina/LATAM), NUNCA asiáticos, ni carteles en otros idiomas, ni estética de stock USA. UNA sola acción física clara que evoluciona durante los 5 segundos (las acciones múltiples o los saltos de escena delatan IA).\n" +
        "3) CÁMARA: un único movimiento motivado por la acción (slow dolly-in, sutil handheld, orbit lento o static con vida interna). Lente y encuadre concretos: ej. '35mm lens, shallow depth of field, medium close-up at chest height'.\n" +
        "4) LUZ PRÁCTICA y hora del día: la fuente tiene que existir en la escena (golden hour entrando por una persiana, tubos fluorescentes de gimnasio a las 6am, neón de vidriera de noche, pantalla que ilumina una cara). Pedí sombras reales y volumen.\n" +
        "5) LOOK ANTI-IA — incluí literalmente: 'shot on digital cinema camera, subtle film grain, natural skin texture, slight handheld imperfection, muted realistic color grade, documentary feel'.\n" +
        "6) PROHIBIDO en el prompt: texto/letreros/logos, manos en primer plano haciendo cosas finas, multitudes, morphing, transiciones o cortes, cámara lenta genérica, look de render 3D/comercial pulido, colores saturados de stock.\n" +
        "7) RITMO explícito: qué se ve en el segundo 1 (hook visual), qué cambia en el 3 (giro) y con qué imagen cierra el 5 (remate emocional).\n" +
        "Largo final del prompt: 80-140 palabras, denso y visual. El producto NO aparece explicado — la historia lo hace inevitable.";

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
        sys.AppendLine("{\"pillar\":string, \"assetKind\":\"image\"|\"video\", \"format\":\"post\"|\"story\"|\"reel\"|\"carousel\", \"concept\":string, \"caption\":string, \"hashtags\":string[], \"prompt\":string, \"overlay\":string, \"narration\":string}");
        sys.AppendLine(OverlayRule);
        sys.AppendLine(BannedWordsRule);
        sys.AppendLine(StructureRule);
        sys.AppendLine(PhotoRealismRule);
        sys.AppendLine(NarrationRule);
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
                Narration: Str(r, "narration"),
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
    public async Task<GeneratedPost?> GenerateForChannelAsync(PostingProfile p, PostingChannel ch, IReadOnlyList<string> recentConcepts, string? hint = null, string? contentTypeKey = null, string? hookDirective = null, string? inspirationRef = null, CancellationToken ct = default)
    {
        if (!_claude.IsConfigured) { _log.LogWarning("Claude no configurado"); return null; }

        // Tipo de posteo: el caller puede forzar uno; si no, elegimos con sesgo orgánico.
        var hasPrices = HasLandingPrices(p);
        var type = ContentTypes.ByKey(contentTypeKey) ?? ContentTypes.Pick(null, hasPrices, Random.Shared);

        var sys = new StringBuilder();
        sys.AppendLine($"Sos el generador de contenido social del producto '{p.ProductKey}' para la red {ch.Platform}.");
        sys.AppendLine($"Audiencia: {p.TargetAudience}.");
        sys.AppendLine($"Tono/voz de marca: {p.BrandVoice}");
        sys.AppendLine($"Guía de marca: {p.BrandGuidelines}");
        sys.AppendLine($"Paleta (no la cambies): {p.BrandColorsJson}. Fuentes: {p.BrandFonts}.");
        if (p.ContentPillars.Count > 0)
            sys.AppendLine($"Pilares de contenido: {string.Join(" | ", p.ContentPillars)}.");
        if (!string.IsNullOrWhiteSpace(p.LandingKnowledge))
        {
            sys.AppendLine();
            sys.AppendLine("FICHA REAL DEL PRODUCTO (datos de la landing — usá SOLO esto para features/precios, no inventes):");
            sys.AppendLine(p.LandingKnowledge);
        }
        sys.AppendLine();
        sys.AppendLine("INSTRUCCIONES ESPECÍFICAS DE ESTA RED:");
        sys.AppendLine(string.IsNullOrWhiteSpace(ch.PromptTemplate) ? "(sin instrucciones extra)" : ch.PromptTemplate);
        sys.AppendLine();
        sys.AppendLine("TIPO DE POSTEO DE ESTA VEZ (respetalo, marca el ángulo):");
        sys.AppendLine(type.Directive);
        if (!string.IsNullOrWhiteSpace(hookDirective))
        {
            sys.AppendLine();
            sys.AppendLine("ESTILO DE GANCHO DE ESTA VEZ (así ABRE el caption — respetalo; variar el arranque es CLAVE para que los posteos no salgan todos iguales):");
            sys.AppendLine(hookDirective);
        }
        sys.AppendLine();
        sys.AppendLine($"El formato es {ch.Format} y el asset es {ch.AssetKind} (NO los cambies). Caption en español rioplatense (voseo). El 'prompt' (para generar el visual) va en INGLÉS, LARGO y MUY detallado, describiendo una ESCENA REAL completa (ver REGLA DE FOTORREALISMO abajo), respetando la paleta de marca.");
        if (ch.Format == SocialPostFormat.Story) sys.AppendLine(StoryRule);

        var slideCount = Math.Max(1, ch.SlideCount);
        if (slideCount > 1)
        {
            // Multi-slide: carrusel de feed (Carousel) o combo de stories (Story).
            var isStoryCombo = ch.Format == SocialPostFormat.Story;
            sys.AppendLine();
            sys.AppendLine($"Este posteo es MULTI-SLIDE: {slideCount} {(isStoryCombo ? "stories seguidas" : "imágenes de un carrusel")} que juntas CUENTAN UNA HISTORIA con arco (gancho → desarrollo → cierre).");
            sys.AppendLine("Cada slide tiene un ROL propio y avanza la narrativa; no las repitas. ESTRUCTURA OBLIGATORIA: la slide 1 es el HOOK (gancho grande que frena el scroll — pregunta que duele, dato fuerte o promesa tipo '3 tips para...'), las del medio son el DESARROLLO (una idea concreta por slide: dato, tip numerado, encuesta con 2 opciones DIBUJADAS no votable, antes/después), y la ÚLTIMA slide es SIEMPRE el CTA (cierre con llamado a la acción: '¿Te gustaría empezar hoy?', 'Probalo gratis', 'link en bio' como texto — NO un link real).");
            if (isStoryCombo)
                sys.AppendLine("Son STORIES: verticales, casuales, texto grande (overlay) protagonista. Ej. secuencia: 1) pregunta/gancho, 2) el dato o la info, 3) el cierre con 'link en bio'.");
            sys.AppendLine("Cada slide: su propio 'overlay' (texto estampado corto) y su propio 'prompt' visual (INGLÉS). El 'caption' del posteo es UNO SOLO para todo el conjunto.");
            sys.AppendLine("Devolvés EXCLUSIVAMENTE un JSON válido, sin markdown, con: {\"pillar\":string, \"concept\":string, \"caption\":string, \"hashtags\":string[], \"slides\":[{\"role\":string, \"overlay\":string, \"prompt\":string}]}");
            sys.AppendLine($"El array 'slides' tiene EXACTAMENTE {slideCount} elementos, en orden narrativo.");
            sys.AppendLine(OverlayRule);
        sys.AppendLine(BannedWordsRule);
        sys.AppendLine(StructureRule);
        sys.AppendLine(PhotoRealismRule);
        }
        else
        {
            sys.AppendLine("Devolvés EXCLUSIVAMENTE un JSON válido, sin markdown, con: {\"pillar\":string, \"concept\":string, \"caption\":string, \"hashtags\":string[], \"prompt\":string, \"overlay\":string, \"narration\":string}");
            sys.AppendLine(OverlayRule);
        sys.AppendLine(BannedWordsRule);
        sys.AppendLine(StructureRule);
        sys.AppendLine(PhotoRealismRule);
            if (ch.AssetKind == SocialAssetKind.Video) { sys.AppendLine(NarrationRule); sys.AppendLine(VideoPromptRecipe); }
        }

        var user = new StringBuilder();
        user.AppendLine($"Generá 1 idea nueva para {ch.Platform} ({ch.Format}), tipo {type.Label}{(slideCount > 1 ? $", de {slideCount} slides" : "")}.");
        if (!string.IsNullOrWhiteSpace(hint))
            user.AppendLine($"TEMA / INDICACIÓN para este posteo: {hint.Trim()}");
        if (!string.IsNullOrWhiteSpace(inspirationRef))
        {
            user.AppendLine();
            user.AppendLine("REFERENCIA DEL RUBRO (un ÁNGULO/FORMATO que usó un competidor — NO copies el texto ni menciones NINGUNA marca; agarrá la IDEA o el ángulo y adaptalo a NUESTRO producto con datos reales):");
            user.AppendLine(inspirationRef.Length > 600 ? inspirationRef[..600] : inspirationRef.Trim());
        }
        if (recentConcepts.Count > 0)
        {
            user.AppendLine("Evitá repetir estos conceptos recientes:");
            foreach (var c in recentConcepts.Take(15)) user.AppendLine($"- {c}");
        }
        user.AppendLine("Recordá: SOLO el JSON.");

        var maxTokens = slideCount > 1 ? SocialMaxTokens + slideCount * 400 : SocialMaxTokens;
        var raw = await _claude.CompleteAsync(sys.ToString(), user.ToString(), maxTokens, null, "social", ct);
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var json = ExtractJson(raw);
        try
        {
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            var hashtags = new List<string>();
            if (r.TryGetProperty("hashtags", out var h) && h.ValueKind == JsonValueKind.Array)
                hashtags.AddRange(h.EnumerateArray().Select(x => x.GetString() ?? "").Where(s => s.Length > 0));

            var slides = new List<GeneratedSlide>();
            if (slideCount > 1 && r.TryGetProperty("slides", out var sl) && sl.ValueKind == JsonValueKind.Array)
            {
                var order = 0;
                foreach (var el in sl.EnumerateArray())
                    slides.Add(new GeneratedSlide(order++, Str(el, "role"), Str(el, "overlay"), Str(el, "prompt")));
            }

            // Para multi-slide, el prompt/overlay "principal" es el de la 1ª slide (preview).
            var mainPrompt = slides.Count > 0 ? slides[0].Prompt : Str(r, "prompt");
            var mainOverlay = slides.Count > 0 ? slides[0].Overlay : Str(r, "overlay");

            return new GeneratedPost(
                Pillar: Str(r, "pillar"),
                AssetKind: ch.AssetKind == SocialAssetKind.Video ? "video" : "image",
                Format: ch.Format.ToString().ToLowerInvariant(),
                Concept: Str(r, "concept"),
                Prompt: mainPrompt,
                Overlay: mainOverlay,
                Narration: Str(r, "narration"),
                Caption: Str(r, "caption"),
                Hashtags: hashtags,
                RawJson: json,
                Type: type.Key,
                Slides: slides);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "No pude parsear JSON de Claude (canal {Platform})", ch.Platform);
            return null;
        }
    }

    /// <summary>true si la ficha de landing tiene precios reales (para habilitar el tipo "precio").</summary>
    private static bool HasLandingPrices(PostingProfile p) =>
        !string.IsNullOrWhiteSpace(p.LandingKnowledge)
        && p.LandingKnowledge.IndexOf("sin precios", StringComparison.OrdinalIgnoreCase) < 0;

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
        sys.AppendLine("El 'prompt' (para generar el visual) va en INGLÉS, LARGO y MUY detallado, describiendo una ESCENA REAL completa (ver REGLA DE FOTORREALISMO abajo), respetando la paleta de marca.");
        if (ch != null)
            sys.AppendLine($"El formato es {ch.Format} y el asset es {ch.AssetKind} (NO los cambies).");
        sys.AppendLine("Devolvés EXCLUSIVAMENTE un JSON válido, sin markdown, con: {\"pillar\":string, \"assetKind\":\"image\"|\"video\", \"format\":\"post\"|\"story\"|\"reel\"|\"carousel\", \"concept\":string, \"caption\":string, \"hashtags\":string[], \"prompt\":string, \"overlay\":string, \"narration\":string}");
        sys.AppendLine(OverlayRule);
        sys.AppendLine(BannedWordsRule);
        sys.AppendLine(StructureRule);
        sys.AppendLine(PhotoRealismRule);
        sys.AppendLine(NarrationRule);
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
        sys.AppendLine("Devolvés EXCLUSIVAMENTE un JSON válido, sin markdown, con: {\"pillar\":string, \"assetKind\":\"image\"|\"video\", \"format\":\"post\"|\"story\"|\"reel\"|\"carousel\", \"concept\":string, \"caption\":string, \"hashtags\":string[], \"prompt\":string, \"overlay\":string, \"narration\":string}");
        sys.AppendLine(OverlayRule);
        sys.AppendLine(BannedWordsRule);
        sys.AppendLine(StructureRule);
        sys.AppendLine(PhotoRealismRule);
        sys.AppendLine(NarrationRule);
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
                Narration: Str(r, "narration"),
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
    string Narration,
    string Caption,
    List<string> Hashtags,
    string RawJson,
    string Type = "",
    List<GeneratedSlide>? Slides = null);

/// <summary>Una slide generada por Claude para un posteo multi-slide (aún sin imagen).</summary>
public record GeneratedSlide(int Order, string Role, string Overlay, string Prompt);
