using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Playwright;
using SalesHub.Infrastructure.Services;

namespace SalesHub.Infrastructure.Instagram;

/// <summary>
/// Analiza la estructura HTML de Instagram y genera selectores dinámicos.
/// Una vez por día navega las páginas clave, extrae el HTML, lo limpia
/// y lo envía a Claude (Anthropic) para que devuelva los selectores
/// actualizados. Esto permite que el scraper se adapte automáticamente
/// a cambios en el DOM de Instagram sin necesidad de modificar código.
/// </summary>
public class InstagramStructureAnalyzer
{
    private readonly InstagramOptions _opts;
    private readonly ILogger<InstagramStructureAnalyzer> _log;
    private readonly ClaudeClient _claude;

    public InstagramStructureAnalyzer(
        InstagramOptions opts,
        ILogger<InstagramStructureAnalyzer> log,
        ClaudeClient claude)
    {
        _opts = opts;
        _log = log;
        _claude = claude;
    }

    /// <summary>
    /// Páginas de Instagram que se analizan para extraer selectores.
    /// </summary>
    public record PageAnalysisTarget(
        string Key,           // "login", "followers_dialog", "dm_inbox", "hashtag_page", "profile_page"
        string Url,           // URL a navegar
        string Description,   // Descripción para el LLM
        bool RequiresLogin    // Si necesita estar logueado
    );

    public static readonly PageAnalysisTarget[] DefaultTargets =
    {
        new("login", "https://www.instagram.com/accounts/login/", "Página de login de Instagram", false),
        new("profile_page", "https://www.instagram.com/instagram/", "Página de perfil de usuario", false),
        new("hashtag_page", "https://www.instagram.com/explore/tags/explore/", "Página de exploración por hashtag", false),
        new("followers_dialog", "https://www.instagram.com/instagram/followers/", "Dialog de seguidores de un perfil", true),
        new("dm_inbox", "https://www.instagram.com/direct/inbox/", "Bandeja de entrada de mensajes directos", true),
        new("dm_new", "https://www.instagram.com/direct/new/", "Pantalla para crear nuevo mensaje directo", true),
    };

    /// <summary>
    /// Ejecuta el análisis completo: navega cada página, extrae HTML, lo envía al LLM
    /// y devuelve los selectores actualizados.
    /// </summary>
    public async Task<InstagramSelectors> AnalyzeStructureAsync(
        IPage page,
        bool isLoggedIn,
        CancellationToken ct = default)
    {
        var pagesData = new List<PageHtmlData>();

        foreach (var target in DefaultTargets)
        {
            if (target.RequiresLogin && !isLoggedIn) continue;

            try
            {
                _log.LogInformation("Analizando página: {Key} ({Url})", target.Key, target.Url);

                await page.GotoAsync(target.Url, new PageGotoOptions
                {
                    WaitUntil = WaitUntilState.NetworkIdle,
                    Timeout = _opts.NavigationTimeoutMs
                });

                await Task.Delay(3000, ct);

                // followers_dialog: navegamos DIRECTO a /{perfil}/followers/ (esa URL abre
                // el modal sola) en vez de depender de clickear un link que IG puede romper
                // — si justo ese link cambia, el analyzer igual captura el dialog para sanarlo.
                var html = await page.ContentAsync();
                var cleaned = CleanHtml(html, target.Key);

                pagesData.Add(new PageHtmlData
                {
                    Key = target.Key,
                    Description = target.Description,
                    CleanedHtml = cleaned
                });

                _log.LogInformation("HTML obtenido para {Key}: {Length} chars (limpio: {CleanLength})",
                    target.Key, html.Length, cleaned.Length);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Error al analizar página {Key}", target.Key);
            }
        }

        // Si Claude no está configurado (sin API key), devolver selectores por defecto
        if (!_claude.IsConfigured)
        {
            _log.LogInformation("Claude no configurado, usando selectores por defecto");
            return InstagramSelectors.GetDefault();
        }

        // Enviar el HTML a Claude para que analice y devuelva los selectores
        var selectors = await AnalyzeWithClaudeAsync(pagesData, ct);

        _log.LogInformation("Análisis de estructura completado. {Count} selectores generados.",
            selectors?.GetType().GetProperties().Length ?? 0);

        return selectors ?? InstagramSelectors.GetDefault();
    }

    /// <summary>
    /// Limpia el HTML: remueve scripts, estilos, y contenido innecesario
    /// dejando solo la estructura semántica relevante para el scraping.
    /// </summary>
    private static string CleanHtml(string rawHtml, string pageKey)
    {
        // Remover scripts
        var cleaned = System.Text.RegularExpressions.Regex.Replace(rawHtml,
            @"<script[^>]*>[\s\S]*?</script>", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        // Remover estilos
        cleaned = System.Text.RegularExpressions.Regex.Replace(cleaned,
            @"<style[^>]*>[\s\S]*?</style>", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        // Remover comentarios HTML
        cleaned = System.Text.RegularExpressions.Regex.Replace(cleaned,
            @"<!--[\s\S]*?-->", "");

        // Remover atributos de datos internos de React (data-*)
        cleaned = System.Text.RegularExpressions.Regex.Replace(cleaned,
            @"\sdata-[a-zA-Z0-9_-]+=""[^""]*""", "");

        // Remover atributos de eventos (onclick, onchange, etc.)
        cleaned = System.Text.RegularExpressions.Regex.Replace(cleaned,
            @"\son[a-z]+=""[^""]*""", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        // Remover SVG
        cleaned = System.Text.RegularExpressions.Regex.Replace(cleaned,
            @"<svg[^>]*>[\s\S]*?</svg>", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        // Remover meta tags
        cleaned = System.Text.RegularExpressions.Regex.Replace(cleaned,
            @"<meta[^>]*>", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        // Remover links a hojas de estilo
        cleaned = System.Text.RegularExpressions.Regex.Replace(cleaned,
            @"<link[^>]*>", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        // Acortar el HTML si es muy largo (limitar a ~30K chars por página)
        if (cleaned.Length > 30_000)
        {
            cleaned = cleaned[..30_000];
        }

        return cleaned;
    }

    /// <summary>
    /// Envía el HTML limpio de todas las páginas a Claude (Anthropic) y recibe
    /// los selectores actualizados en formato JSON.
    /// </summary>
    private async Task<InstagramSelectors?> AnalyzeWithClaudeAsync(
        List<PageHtmlData> pagesData,
        CancellationToken ct)
    {
        var systemPrompt = @"
Eres un experto en scraping de Instagram. Tu tarea es analizar el HTML de las páginas de Instagram
y generar selectores CSS precisos para extraer la información necesaria.

Para cada página, identifica los selectores CSS que permitan:
1. LOGIN: input de username, input de password, botón de submit, input de 2FA
2. PROFILE: link de seguidores, nombre de usuario, bio
3. HASHTAG: primer post, botón de siguiente, lista de posts
4. FOLLOWERS_DIALOG: lista de usuarios, links a perfiles, scroll container
5. DM_INBOX: botón de nuevo mensaje, input de búsqueda, resultados
6. DM_NEW: input de búsqueda, resultados, botón de chat, textbox de mensaje

Responde SOLO con un JSON válido con esta estructura exacta, sin markdown ni explicaciones:

{
  ""login"": {
    ""usernameInput"": ""input[name='username']"",
    ""passwordInput"": ""input[name='password']"",
    ""submitButton"": ""button[type='submit']"",
    ""twoFactorInput"": ""input[name='verificationCode']"",
    ""saveInfoButton"": ""div:has-text('Save Info')"",
    ""notNowButton"": ""button:has-text('Not Now')"",
    ""notificationsButton"": ""button:has-text('Not Now')""
  },
  ""profile"": {
    ""followersLink"": ""a[href$='/followers/']"",
    ""userName"": ""h2"",
    ""bio"": ""span:has-text('Bio')""
  },
  ""followersDialog"": {
    ""dialog"": ""div[role='dialog']"",
    ""userLinks"": ""div[role='dialog'] a[href^='/']"",
    ""scrollContainer"": ""div[role='dialog']""
  },
  ""hashtag"": {
    ""firstPost"": ""article a"",
    ""nextButton"": ""button[aria-label='Next']"",
    ""commentLinks"": ""h3 a, span a[href^='/']""
  },
  ""dmInbox"": {
    ""newMessageButton"": ""div[role='button']:has-text('Send message')"",
    ""newMessageIcon"": ""svg[aria-label='New message']"",
    ""searchInput"": ""input[placeholder='Search...']"",
    ""userResult"": ""div[role='button']"",
    ""chatButton"": ""div[role='button']:has-text('Chat')"",
    ""nextButton"": ""div[role='button']:has-text('Next')"",
    ""messageTextbox"": ""div[role='textbox']""
  }
}

IMPORTANTE: Los selectores deben ser lo más específicos posible para evitar falsos positivos.
Usa atributos de aria, roles, text content, y selectores CSS avanzados.
";

        var userMessage = "Analiza el HTML de las siguientes páginas de Instagram y genera los selectores CSS actualizados:\n\n";

        foreach (var page in pagesData)
        {
            userMessage += $"=== PÁGINA: {page.Key} ({page.Description}) ===\n";
            userMessage += page.CleanedHtml + "\n\n";
        }

        userMessage += "\nResponde SOLO con el JSON de selectores, sin explicaciones ni markdown.";

        var text = await _claude.CompleteAsync(systemPrompt, userMessage, "instagram-selectors", ct);
        if (string.IsNullOrWhiteSpace(text))
        {
            _log.LogWarning("Claude no devolvió respuesta para el análisis de selectores");
            return null;
        }

        // Claude puede devolver el JSON pelado o dentro de un bloque ```json.
        var json = ExtractJsonBlock(text);
        if (string.IsNullOrEmpty(json))
        {
            _log.LogWarning("No se pudo extraer JSON de la respuesta de Claude");
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<InstagramSelectors>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Error al deserializar los selectores de Claude");
            return null;
        }
    }

    private static string? ExtractJsonBlock(string text)
    {
        // Buscar bloque JSON entre ```json y ``` o entre llaves
        var jsonMatch = System.Text.RegularExpressions.Regex.Match(text,
            @"```(?:json)?\s*(\{[\s\S]*?\})\s*```");

        if (jsonMatch.Success)
            return jsonMatch.Groups[1].Value;

        // Intentar encontrar un objeto JSON directamente
        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        if (start >= 0 && end > start)
        {
            return text[start..(end + 1)];
        }

        return null;
    }

    private record PageHtmlData
    {
        public string Key { get; init; } = string.Empty;
        public string Description { get; init; } = string.Empty;
        public string CleanedHtml { get; init; } = string.Empty;
    }
}

/// <summary>
/// Selectores CSS dinámicos para cada página de Instagram.
/// Se actualizan automáticamente mediante el análisis con LLM.
/// </summary>
public class InstagramSelectors
{
    [JsonPropertyName("login")]
    public LoginSelectors Login { get; set; } = new();

    [JsonPropertyName("profile")]
    public ProfileSelectors Profile { get; set; } = new();

    [JsonPropertyName("followersDialog")]
    public FollowersDialogSelectors FollowersDialog { get; set; } = new();

    [JsonPropertyName("hashtag")]
    public HashtagSelectors Hashtag { get; set; } = new();

    [JsonPropertyName("dmInbox")]
    public DmInboxSelectors DmInbox { get; set; } = new();

    /// <summary>
    /// Devuelve los selectores por defecto (hardcodeados).
    /// </summary>
    public static InstagramSelectors GetDefault() => new();

    public class LoginSelectors
    {
        public string UsernameInput { get; set; } = "input[name='username']";
        public string PasswordInput { get; set; } = "input[name='password']";
        public string SubmitButton { get; set; } = "button[type='submit']";
        public string TwoFactorInput { get; set; } = "input[name='verificationCode']";
        public string SaveInfoButton { get; set; } = "div:has-text('Save Info')";
        public string NotNowButton { get; set; } = "button:has-text('Not Now')";
        public string NotificationsButton { get; set; } = "button:has-text('Not Now')";
    }

    public class ProfileSelectors
    {
        public string FollowersLink { get; set; } = "a[href$='/followers/']";
        public string UserName { get; set; } = "h2";
        public string Bio { get; set; } = "span:has-text('Bio')";
    }

    public class FollowersDialogSelectors
    {
        public string Dialog { get; set; } = "div[role='dialog']";
        public string UserLinks { get; set; } = "div[role='dialog'] a[href^='/']";
        public string ScrollContainer { get; set; } = "div[role='dialog']";
    }

    public class HashtagSelectors
    {
        public string FirstPost { get; set; } = "article a";
        public string NextButton { get; set; } = "button[aria-label='Next']";
        public string CommentLinks { get; set; } = "h3 a, span a[href^='/']";
    }

    public class DmInboxSelectors
    {
        public string NewMessageButton { get; set; } = "div[role='button']:has-text('Send message')";
        public string NewMessageIcon { get; set; } = "svg[aria-label='New message']";
        public string SearchInput { get; set; } = "input[placeholder='Search...']";
        public string UserResult { get; set; } = "div[role='button']";
        public string ChatButton { get; set; } = "div[role='button']:has-text('Chat')";
        public string NextButton { get; set; } = "div[role='button']:has-text('Next')";
        public string MessageTextbox { get; set; } = "div[role='textbox']";
    }
}
