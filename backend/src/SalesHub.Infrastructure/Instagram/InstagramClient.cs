using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Playwright;
using SalesHub.Core.Domain.Entities;
using SalesHub.Infrastructure.Persistence;

namespace SalesHub.Infrastructure.Instagram;

/// <summary>
/// Cliente de Instagram basado en Playwright.
/// Maneja login persistente con cookies, scraping de perfiles y envío de DMs.
/// </summary>
public class InstagramClient : IAsyncDisposable
{
    private readonly ApplicationDbContext _db;
    private readonly InstagramEncryptionService _crypto;
    private readonly InstagramOptions _opts;
    private readonly ILogger _log;

    private IPlaywright? _playwright;
    private IBrowser? _browser;
    private IBrowserContext? _context;
    private IPage? _page;

    private bool _loggedIn;

    /// <summary>
    /// Tracker de fallos de selectores (opcional). Si un caller lo setea, el scrape
    /// reporta cuándo un selector no matchea o trae 0 resultados → tras N fallos se
    /// dispara el re-análisis con Claude (self-heal). Si es null, no se reporta nada.
    /// </summary>
    public SelectorFailureTracker? FailureTracker { get; set; }

    // Instagram (form unificado de Meta) usa estos names en el login web.
    // Los dejamos como lista para tolerar variantes A/B y el form viejo.
    private const string UsernameSelector = "input[name='email'], input[name='username']";
    private const string PasswordSelector = "input[name='pass'], input[name='password']";
    // En la página two_step_verification el input del código NO tiene 'name' ni
    // 'aria-label' estables (es un <input type=text autocomplete=off> con id de React).
    // Lo matcheamos por forma, tolerando además variantes viejas.
    private const string TwoFactorSelector =
        "input[name='verificationCode'], " +
        "input[autocomplete='one-time-code'], " +
        "input[type='tel']:not([name]), " +
        "input[type='text'][autocomplete='off']:not([name])";

    public InstagramClient(
        ApplicationDbContext db,
        InstagramEncryptionService crypto,
        InstagramOptions opts,
        ILogger log)
    {
        _db = db;
        _crypto = crypto;
        _opts = opts;
        _log = log;
    }

    /// <summary>
    /// Inicializa el navegador y restaura sesión si hay cookies guardadas.
    /// </summary>
    public async Task InitializeAsync(InstagramAccount account, CancellationToken ct = default)
    {
        _playwright = await Microsoft.Playwright.Playwright.CreateAsync();

        var launchOpts = new BrowserTypeLaunchOptions
        {
            Headless = true,
            Args = new[]
            {
                "--no-sandbox",
                "--disable-setuid-sandbox",
                "--disable-dev-shm-usage",
                "--disable-gpu",
                // Stealth: saca la bandera de automatización que IG usa para detectar el bot
                // (con headless sin esto, IG sirve el shell logueado pero NO renderiza el perfil).
                "--disable-blink-features=AutomationControlled"
            }
        };

        // Proxy: prioridad al de la cuenta (geo-matcheado a su origen real,
        // ej. gymhero → residencial AR) y fallback al global de InstagramOptions.
        // Se arma una LISTA de candidatos con FAILOVER: primario (cuenta o global) y luego
        // los FallbackProxies globales; se prueba en orden y se usa el primero que LLEGUE a
        // Instagram con cert válido. Si el primario (ej. túnel de la compu) está caído, la
        // sesión salta sola al siguiente (ej. iProxy del celu) sin frenar el follow.
        var proxyCandidates = BuildProxyCandidates(account.ProxyUrl);
        if (proxyCandidates.Count > 0)
        {
            var proxy = await ResolveWorkingProxyAsync(proxyCandidates, account.Username, ct);
            if (proxy is null)
                throw new InvalidOperationException(
                    $"Ningún proxy (de {proxyCandidates.Count} candidato/s) llegó a Instagram para {account.Username}. " +
                    "No se intenta sin proxy para no exponer la cuenta desde la IP del server.");

            launchOpts.Proxy = proxy;
            _log.LogInformation("Instagram {User} sale por proxy {Server}", account.Username, proxy.Server);
        }
        else
        {
            _log.LogWarning("Instagram {User} SIN proxy (sale por la IP del server). " +
                "IG puede pedir 2FA/checkpoint si la geo no coincide.", account.Username);
        }

        _browser = await _playwright.Chromium.LaunchAsync(launchOpts);

        // Crear contexto con user-agent realista
        _context = await _browser.NewContextAsync(new BrowserNewContextOptions
        {
            UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/125.0.0.0 Safari/537.36",
            ViewportSize = new ViewportSize { Width = 1280, Height = 800 },
            Locale = "es-AR",
            TimezoneId = "America/Argentina/Buenos_Aires"
        });

        // Stealth: enmascarar los tells típicos de un navegador automatizado ANTES de que
        // corra el JS de IG. Sin esto, IG detecta el bot y sirve el perfil vacío (solo el nav).
        await _context.AddInitScriptAsync(@"
            Object.defineProperty(navigator, 'webdriver', { get: () => undefined });
            Object.defineProperty(navigator, 'languages', { get: () => ['es-AR', 'es', 'en'] });
            Object.defineProperty(navigator, 'plugins', { get: () => [1, 2, 3, 4, 5] });
            window.chrome = { runtime: {} };
            const _q = window.navigator.permissions && window.navigator.permissions.query;
            if (_q) {
                window.navigator.permissions.query = (p) =>
                    p && p.name === 'notifications'
                        ? Promise.resolve({ state: Notification.permission })
                        : _q(p);
            }
        ");

        // Bloqueo de recursos pesados para minimizar el consumo de datos del
        // proxy mobile: cada byte que baja el navegador SALE POR EL 4G DEL CHIP
        // y se paga. Bloqueando imágenes, video/audio y fuentes, una sesión de IG
        // baja de decenas de MB a <3 MB sin romper la automatización (opera sobre
        // el DOM/JS). NO bloqueamos document/script/xhr/fetch (la SPA dejaría de
        // funcionar) ni stylesheet (para no alterar los chequeos de visibilidad
        // que hace Playwright antes de cada Fill/Click).
        if (_opts.BlockHeavyResources)
        {
            await _context.RouteAsync("**/*", async route =>
            {
                var type = route.Request.ResourceType;
                if (type is "image" or "media" or "font")
                    await route.AbortAsync();
                else
                    await route.ContinueAsync();
            });
        }

        // Restaurar cookies si existen
        if (!string.IsNullOrEmpty(account.SessionCookiesJson))
        {
            try
            {
                var cookies = JsonSerializer.Deserialize<List<BrowserContextCookies>>(account.SessionCookiesJson);
                if (cookies is { Count: > 0 })
                {
                    await _context.AddCookiesAsync(cookies.Select(c => new Cookie
                    {
                        Name = c.Name,
                        Value = c.Value,
                        Domain = c.Domain,
                        Path = c.Path,
                        HttpOnly = c.HttpOnly,
                        Secure = c.Secure,
                        SameSite = c.SameSite,
                        Expires = c.Expires
                    }).ToArray());
                    _loggedIn = true;
                    _log.LogInformation("Sesión de Instagram restaurada para {User}", account.Username);
                }
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Error al restaurar cookies de Instagram para {User}", account.Username);
            }
        }

        _page = await _context.NewPageAsync();
        _page.SetDefaultTimeout(_opts.NavigationTimeoutMs);

        // Si no hay sesión, hacer login
        if (!_loggedIn)
        {
            await LoginAsync(account, ct);
        }
    }

    /// <summary>
    /// Hace login en Instagram con usuario/contraseña.
    /// </summary>
    private async Task LoginAsync(InstagramAccount account, CancellationToken ct)
    {
        var password = _crypto.Decrypt(account.EncryptedPassword);
        if (string.IsNullOrEmpty(password))
            throw new InvalidOperationException($"Password encriptada inválida para {account.Username}");

        _log.LogInformation("Logueando en Instagram como {User}...", account.Username);

        await _page!.GotoAsync("https://www.instagram.com/accounts/login/", new PageGotoOptions
        {
            WaitUntil = WaitUntilState.NetworkIdle,
            Timeout = _opts.NavigationTimeoutMs
        });

        await RandomDelayAsync();

        // Esperar y llenar campos de login.
        // Instagram renombró los campos (username→email, password→pass) y el
        // botón visible es un <div>; el form se envía con Enter sobre el password.
        var userEl = await _page.WaitForSelectorAsync(UsernameSelector);
        await userEl!.FillAsync(account.Username);
        await RandomDelayAsync();
        var passEl = await _page.WaitForSelectorAsync(PasswordSelector);
        await passEl!.FillAsync(password);
        await RandomDelayAsync();

        // Submit del form (Enter dispara el submit nativo)
        await passEl.PressAsync("Enter");
        await Task.Delay(6000, ct);

        // Verificar si pide 2FA. Lo detectamos por la URL (estable: la página de
        // verificación es /accounts/login/two_step_verification?...&flow=two_factor_login)
        // en vez de depender del input, que no tiene atributos estables.
        var afterLoginUrl = _page.Url;
        var asksTwoFactor = afterLoginUrl.Contains("two_factor")
            || afterLoginUrl.Contains("two_step_verification");
        if (asksTwoFactor)
        {
            _log.LogInformation("Instagram pide 2FA para {User}", account.Username);

            if (!string.IsNullOrEmpty(account.TwoFactorSecret))
            {
                // Esperar a que el input del código esté disponible y autocompletarlo
                var twoFactorInput = await _page.WaitForSelectorAsync(TwoFactorSelector,
                    new PageWaitForSelectorOptions { Timeout = 15_000 });
                var code = GenerateTotpCode(account.TwoFactorSecret);
                await twoFactorInput!.FillAsync(code);
                await RandomDelayAsync();
                await twoFactorInput.PressAsync("Enter");
                await Task.Delay(6000, ct);
            }
            else
            {
                // No tenemos secret → guardar estado para que el usuario
                // ingrese el código desde la UI
                _log.LogWarning(
                    "Instagram pide 2FA para {User} y no hay secret configurado. " +
                    "La cuenta queda en estado 'awaiting 2FA'. " +
                    "Ingresá el código desde la UI o configurá el TwoFactorSecret.",
                    account.Username);

                account.IsAwaitingTwoFactor = true;
                account.UpdatedAt = DateTimeOffset.UtcNow;
                await _db.SaveChangesAsync(ct);

                // No tiramos excepción, solo devolvemos sin completar login
                return;
            }
        }

        // Completar login (Save Info, Notifications, verificar)
        await CompleteLoginAsync(account, ct);
    }

    /// <summary>
    /// Scrapea los seguidores de una cuenta de Instagram.
    /// </summary>
    /// <summary>
    /// Normaliza un "source" a un handle de IG limpio: tolera URLs completas
    /// (https://instagram.com/x/), arroba y barras. Sin esto, una URL como source
    /// rompe el scraping (se navega a instagram.com/https://.../ ).
    /// </summary>
    private static string NormalizeHandle(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
        var s = raw.Trim();
        var idx = s.IndexOf("instagram.com/", StringComparison.OrdinalIgnoreCase);
        if (idx >= 0) s = s[(idx + "instagram.com/".Length)..];
        s = s.TrimStart('@').Trim().Trim('/');
        s = s.Split('/', '?').FirstOrDefault() ?? s;
        return s.Trim();
    }

    /// <summary>Scrapea los SEGUIDORES (followers) del perfil indicado.</summary>
    public Task<List<InstagramProfile>> ScrapeFollowersAsync(string targetHandle, int maxProfiles, CancellationToken ct = default)
        => ScrapeConnectionsAsync(targetHandle, maxProfiles, following: false, ct);

    /// <summary>Scrapea las cuentas que el perfil indicado SIGUE (following).</summary>
    public Task<List<InstagramProfile>> ScrapeFollowingAsync(string targetHandle, int maxProfiles, CancellationToken ct = default)
        => ScrapeConnectionsAsync(targetHandle, maxProfiles, following: true, ct);

    /// <summary>
    /// Abre el dialog de "followers" o "following" de un perfil y scrapea hasta
    /// <paramref name="maxProfiles"/> handles, scrolleando para cargar más.
    /// </summary>
    private async Task<List<InstagramProfile>> ScrapeConnectionsAsync(
        string targetHandle, int maxProfiles, bool following, CancellationToken ct = default)
    {
        EnsureLoggedIn();
        var profiles = new List<InstagramProfile>();

        // Selectores del último snapshot válido (auto-reparado por Claude), con fallback
        // a los defaults hardcodeados si no hay ninguno.
        var sel = await LoadActiveSelectorsAsync(ct);

        // "followers" = seguidores del perfil; "following" = cuentas que el perfil sigue.
        var tab = following ? "following" : "followers";
        var label = following ? "seguidos" : "seguidores";

        targetHandle = NormalizeHandle(targetHandle);
        if (string.IsNullOrEmpty(targetHandle))
        {
            _log.LogWarning("Source de {Label} inválido (handle vacío tras normalizar).", label);
            return profiles;
        }

        _log.LogInformation("Scrapeando {Label} de {Target} (max {Max})...", label, targetHandle, maxProfiles);

        await _page!.GotoAsync($"https://www.instagram.com/{targetHandle}/", new PageGotoOptions
        {
            WaitUntil = WaitUntilState.DOMContentLoaded,
            Timeout = _opts.NavigationTimeoutMs
        });

        await RandomDelayAsync();

        // Esperar el link de followers/following: IG renderiza el perfil client-side,
        // así que un QuerySelector inmediato suele llegar antes de que el link exista.
        // El selector tolera el href exacto o cualquiera que termine en /{tab}/.
        // El contador de seguidores del header ya NO es <a href="/x/followers/">: IG lo pasó
        // a <a href="#"> con el texto "14,3 mil seguidores" (abre el modal por JS). Probamos
        // el href viejo (por si vuelve) y, si no, matcheamos por el TEXTO del contador.
        var countWord = following ? "seguidos" : "seguidores";
        IElementHandle? tabLink = null;
        try
        {
            tabLink = await _page.WaitForSelectorAsync(
                $"a[href$='/{tab}/']:not([href$='mutualOnly']), a[role='link']:has-text('{countWord}')",
                new PageWaitForSelectorOptions { Timeout = 12_000 });
        }
        catch (Exception) { /* timeout: el link no apareció */ }

        if (tabLink is null)
        {
            if (_page.Url.Contains("/accounts/login"))
            {
                _loggedIn = false;
                _log.LogWarning("Sesión caída al scrapear {Label} de {Target}", label, targetHandle);
            }
            else
            {
                _log.LogWarning("No se encontró el link de {Label} para {Target} " +
                    "(perfil privado, o Instagram limita/bloquea la lista para esta cuenta).", label, targetHandle);
                // Puede ser perfil privado (fuente puntual) o que IG cambió el DOM (rompe
                // TODAS las fuentes). Reportamos: si es lo segundo, se acumulan fallos y salta
                // el self-heal; el ReportSuccess de las fuentes que sí andan resetea el ruido.
                FailureTracker?.ReportFailure("profile", "followersLink");
            }
            return profiles;
        }

        FailureTracker?.ReportSuccess("profile", "followersLink");
        await tabLink.ClickAsync();
        await Task.Delay(3000, ct);

        // Scroll para cargar más
        IElementHandle? dialog = null;
        try
        {
            dialog = await _page.WaitForSelectorAsync(sel.FollowersDialog.Dialog, new PageWaitForSelectorOptions
            {
                Timeout = _opts.NavigationTimeoutMs
            });
        }
        catch (Exception) { /* timeout: el dialog no apareció con el selector actual */ }

        if (dialog is null)
        {
            _log.LogWarning("No se abrió el dialog de {Label} para {Target}", label, targetHandle);
            FailureTracker?.ReportFailure("followersDialog", "dialog");
            return profiles;
        }
        FailureTracker?.ReportSuccess("followersDialog", "dialog");

        var lastCount = 0;
        var scrollAttempts = 0;

        while (profiles.Count < maxProfiles && scrollAttempts < 30)
        {
            // Extraer handles visibles
            var links = await _page.QuerySelectorAllAsync(sel.FollowersDialog.UserLinks);
            foreach (var link in links)
            {
                var href = await link.GetAttributeAsync("href");
                // Saltear los links de navegación del propio perfil (followers/following).
                if (string.IsNullOrEmpty(href) || href == "/"
                    || href.Contains("/followers/") || href.Contains("/following/")) continue;

                var handle = href.TrimStart('/').Split('/').FirstOrDefault();
                if (string.IsNullOrEmpty(handle) || profiles.Any(p => p.Handle == handle)) continue;

                var nameEl = await link.QuerySelectorAsync("span");
                var displayName = nameEl is not null ? await nameEl.InnerTextAsync() : handle;

                profiles.Add(new InstagramProfile
                {
                    Handle = handle,
                    DisplayName = displayName,
                    Source = tab,
                    SourceTarget = targetHandle
                });

                if (profiles.Count >= maxProfiles) break;
            }

            if (profiles.Count == lastCount)
            {
                scrollAttempts++;
                // Hacer scroll en el dialog
                await _page.EvaluateAsync(
                    "(s) => document.querySelector(s)?.scrollBy(0, 300)",
                    sel.FollowersDialog.ScrollContainer);
            }
            else
            {
                scrollAttempts = 0;
                lastCount = profiles.Count;
            }

            await Task.Delay(1500, ct);
        }

        if (profiles.Count == 0)
        {
            // El dialog abrió pero no salió ningún handle → muy probablemente el selector
            // de links de perfil cambió. Reportamos para gatillar el re-análisis con Claude.
            _log.LogWarning("Dialog de {Label} de {Target} abrió pero 0 perfiles extraídos", label, targetHandle);
            FailureTracker?.ReportFailure("followersDialog", "userLinks");
        }
        else
        {
            FailureTracker?.ReportSuccess("followersDialog", "userLinks");
        }

        _log.LogInformation("Scrapeados {Count} {Label} de {Target}", profiles.Count, label, targetHandle);
        return profiles;
    }

    /// <summary>
    /// DIAGNÓSTICO (no encola nada): navega directo a /{handle}/followers/, espera el
    /// dialog y devuelve qué encontró REALMENTE — para entender por qué el scrape trae 0
    /// (dialog vacío = IG limita la lista; dialog con filas no-&lt;a&gt; = cambió el DOM).
    /// </summary>
    public async Task<string> DebugFollowersHtmlAsync(string targetHandle, CancellationToken ct = default)
    {
        EnsureLoggedIn();
        targetHandle = NormalizeHandle(targetHandle);

        var log = new List<string>();

        // 1) Navegar al PERFIL (igual que el scrape).
        await _page!.GotoAsync($"https://www.instagram.com/{targetHandle}/", new PageGotoOptions
        {
            WaitUntil = WaitUntilState.DOMContentLoaded,
            Timeout = _opts.NavigationTimeoutMs
        });
        log.Add($"nav profile -> {_page.Url}");
        var loginRedirect = _page.Url.Contains("/accounts/login");

        // 2) ESPERAR el link de followers hasta 15s (igual que el scrape, que espera 12s).
        IElementHandle? followersLink = null;
        try
        {
            followersLink = await _page.WaitForSelectorAsync(
                "a[href$='/followers/']:not([href$='mutualOnly']), a[role='link']:has-text('seguidores')",
                new PageWaitForSelectorOptions { Timeout = 15000 });
        }
        catch { }
        var linkFound = followersLink is not null;
        log.Add($"followersLink apareció (15s): {linkFound}");

        // 3) Click y esperar el dialog hasta 15s.
        var dialogFound = false;
        var dialogAnchors = 0;
        if (followersLink is not null)
        {
            try { await followersLink.ClickAsync(); } catch (Exception ex) { log.Add("click error: " + ex.Message); }
            IElementHandle? dlg = null;
            try { dlg = await _page.WaitForSelectorAsync("div[role='dialog']", new PageWaitForSelectorOptions { Timeout = 15000 }); }
            catch { }
            dialogFound = dlg is not null;
            log.Add($"dialog apareció tras click (15s): {dialogFound}");
            if (dialogFound)
            {
                await Task.Delay(3500, ct); // dejar cargar las filas
                dialogAnchors = (await _page.QuerySelectorAllAsync("div[role='dialog'] a[href^='/']")).Count;
                log.Add($"anchors a[href^=/] en dialog: {dialogAnchors}");
            }
        }

        // Dump: si hay dialog, su innerHTML; si no, el body — sin clases/style/svg para leer.
        var dumpTarget = dialogFound
            ? await _page.QuerySelectorAsync("div[role='dialog']")
            : await _page.QuerySelectorAsync("body");
        var html = dumpTarget is not null ? await dumpTarget.InnerHTMLAsync() : await _page.ContentAsync();
        html = Regex.Replace(html, @"<(script|style|svg|head)[^>]*>[\s\S]*?</\1>", "", RegexOptions.IgnoreCase);
        html = Regex.Replace(html, @"\s(class|style|data-visualcompletion|crossorigin|data-p|data-btmanifest|data-c)=""[^""]*""", "");
        if (html.Length > 9000) html = html[..9000];

        return JsonSerializer.Serialize(new
        {
            handle = targetHandle,
            loginRedirect,
            linkFound,
            dialogFound,
            dialogAnchors,
            log,
            htmlDump = html
        });
    }

    /// <summary>
    /// Carga los selectores del último snapshot válido (auto-reparado por Claude),
    /// con fallback a los defaults hardcodeados si no hay ninguno / no parsea.
    /// </summary>
    private async Task<InstagramSelectors> LoadActiveSelectorsAsync(CancellationToken ct)
    {
        try
        {
            var snap = await _db.InstagramStructureSnapshots
                .AsNoTracking()
                .Where(s => s.IsValid)
                .OrderByDescending(s => s.SnapshotDate)
                .ThenByDescending(s => s.CreatedAt)
                .FirstOrDefaultAsync(ct);

            if (snap is not null && !string.IsNullOrWhiteSpace(snap.SelectorsJson))
            {
                var parsed = JsonSerializer.Deserialize<InstagramSelectors>(
                    snap.SelectorsJson,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (parsed is not null) return parsed;
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "No pude cargar selectores del snapshot, uso defaults");
        }

        return InstagramSelectors.GetDefault();
    }

    /// <summary>
    /// Scrapea usuarios que comentaron en posts con un hashtag específico.
    /// </summary>
    public async Task<List<InstagramProfile>> ScrapeHashtagCommentersAsync(string hashtag, int maxProfiles, CancellationToken ct = default)
    {
        EnsureLoggedIn();
        var profiles = new List<InstagramProfile>();
        var seenHandles = new HashSet<string>();

        _log.LogInformation("Scrapeando comentadores de #{Tag} (max {Max})...", hashtag, maxProfiles);

        await _page!.GotoAsync($"https://www.instagram.com/explore/tags/{hashtag}/", new PageGotoOptions
        {
            WaitUntil = WaitUntilState.NetworkIdle,
            Timeout = _opts.NavigationTimeoutMs
        });

        await RandomDelayAsync();

        // Click en el primer post
        var firstPost = await _page.QuerySelectorAsync("article a");
        if (firstPost is null)
        {
            _log.LogWarning("No se encontraron posts para #{Tag}", hashtag);
            return profiles;
        }

        await firstPost.ClickAsync();
        await Task.Delay(3000, ct);

        // Navegar posts y extraer comentadores
        for (int i = 0; i < 20 && profiles.Count < maxProfiles; i++)
        {
            // Extraer handles de comentarios
            var commentLinks = await _page.QuerySelectorAllAsync("h3 a, span a[href^='/']");
            foreach (var link in commentLinks)
            {
                var href = await link.GetAttributeAsync("href");
                if (string.IsNullOrEmpty(href) || !href.StartsWith("/")) continue;

                var handle = href.TrimStart('/').Split('/').FirstOrDefault();
                if (string.IsNullOrEmpty(handle) || seenHandles.Contains(handle)) continue;

                seenHandles.Add(handle);
                var displayName = await link.InnerTextAsync();

                profiles.Add(new InstagramProfile
                {
                    Handle = handle,
                    DisplayName = string.IsNullOrEmpty(displayName) ? handle : displayName,
                    Source = "hashtag_commenters",
                    SourceTarget = hashtag
                });

                if (profiles.Count >= maxProfiles) break;
            }

            // Click en flecha derecha para siguiente post
            var nextBtn = await _page.QuerySelectorAsync("button[aria-label='Next']");
            if (nextBtn is null) break;

            await nextBtn.ClickAsync();
            await Task.Delay(2000, ct);
        }

        _log.LogInformation("Scrapeados {Count} comentadores de #{Tag}", profiles.Count, hashtag);
        return profiles;
    }

    /// <summary>
    /// Envía un DM a un usuario de Instagram.
    /// </summary>
    public async Task<bool> SendDmAsync(string handle, string message, CancellationToken ct = default)
    {
        EnsureLoggedIn();

        _log.LogInformation("Enviando DM a {Handle}...", handle);

        try
        {
            // Ir a la página de DMs
            await _page!.GotoAsync("https://www.instagram.com/direct/inbox/", new PageGotoOptions
            {
                WaitUntil = WaitUntilState.NetworkIdle,
                Timeout = _opts.NavigationTimeoutMs
            });

            await RandomDelayAsync();

            // Click en "Send Message" button
            var sendMsgBtn = await _page.QuerySelectorAsync("div[role='button']:has-text('Send message')");
            if (sendMsgBtn is null)
            {
                // Intentar con el icono de "+"
                sendMsgBtn = await _page.QuerySelectorAsync("svg[aria-label='New message']");
                if (sendMsgBtn is null)
                {
                    _log.LogWarning("No se encontró botón para nuevo mensaje");
                    return false;
                }
            }

            await sendMsgBtn.ClickAsync();
            await Task.Delay(2000, ct);

            // Buscar al usuario
            var searchInput = await _page.QuerySelectorAsync("input[placeholder='Search...']");
            if (searchInput is null)
            {
                _log.LogWarning("No se encontró input de búsqueda");
                return false;
            }

            await searchInput.FillAsync(handle);
            await Task.Delay(2000, ct);

            // Click en el resultado
            var result = await _page.QuerySelectorAsync($"div[role='button']:has-text('{handle}')");
            if (result is null)
            {
                _log.LogWarning("No se encontró usuario {Handle} en resultados", handle);
                return false;
            }

            await result.ClickAsync();
            await Task.Delay(1000, ct);

            // Click en "Chat" o "Next"
            var chatBtn = await _page.QuerySelectorAsync("div[role='button']:has-text('Chat')");
            if (chatBtn is null)
                chatBtn = await _page.QuerySelectorAsync("div[role='button']:has-text('Next')");

            if (chatBtn is not null)
            {
                await chatBtn.ClickAsync();
                await Task.Delay(2000, ct);
            }

            // Escribir mensaje
            var msgInput = await _page.QuerySelectorAsync("div[role='textbox']");
            if (msgInput is null)
            {
                _log.LogWarning("No se encontró el textbox del mensaje");
                return false;
            }

            await msgInput.FillAsync(message);
            await RandomDelayAsync();

            // Enviar (Enter)
            await _page.Keyboard.PressAsync("Enter");
            await Task.Delay(2000, ct);

            _log.LogInformation("DM enviado a {Handle}", handle);
            return true;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Error al enviar DM a {Handle}", handle);
            return false;
        }
    }

    /// <summary>
    /// Lee el inbox de DMs y devuelve los mensajes ENTRANTES (de la otra persona).
    /// Usa la API interna autenticada de Instagram (direct_v2/inbox) vía fetch desde
    /// el contexto de la página — mucho más robusto que scrapear el DOM virtualizado.
    /// </summary>
    public async Task<List<InstagramInboundMessage>> ReadInboxAsync(int maxThreads = 20, CancellationToken ct = default)
    {
        EnsureLoggedIn();
        var result = new List<InstagramInboundMessage>();

        // El fetch tiene que ser same-origin (www.instagram.com) para mandar cookies + headers.
        if (string.IsNullOrEmpty(_page!.Url) || !_page.Url.Contains("instagram.com"))
        {
            await _page.GotoAsync("https://www.instagram.com/", new PageGotoOptions
            {
                WaitUntil = WaitUntilState.DOMContentLoaded,
                Timeout = _opts.NavigationTimeoutMs
            });
            await Task.Delay(1500, ct);
        }

        string raw;
        try
        {
            raw = await _page.EvaluateAsync<string>(@"async (limit) => {
                const url = `/api/v1/direct_v2/inbox/?visual_message_return_type=unseen&thread_message_limit=10&persistentBadging=true&limit=${limit}`;
                const res = await fetch(url, { headers: { 'X-IG-App-ID': '936619743392459' }, credentials: 'include' });
                return JSON.stringify({ status: res.status, body: res.ok ? await res.json() : null });
            }", maxThreads);
        }
        catch (Exception ex)
        {
            _log.LogWarning("ReadInbox: fetch falló: {Err}", ex.Message);
            return result;
        }

        try
        {
            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;
            if (!root.TryGetProperty("body", out var body) || body.ValueKind != JsonValueKind.Object)
            {
                var status = root.TryGetProperty("status", out var s) ? s.ToString() : "?";
                _log.LogWarning("ReadInbox: respuesta inesperada de IG (status {Status})", status);
                return result;
            }

            if (!body.TryGetProperty("inbox", out var inbox) ||
                !inbox.TryGetProperty("threads", out var threads) ||
                threads.ValueKind != JsonValueKind.Array)
                return result;

            foreach (var thread in threads.EnumerateArray())
            {
                // viewer_id = nuestro propio user id; los items con otro user_id son entrantes.
                long viewerId = thread.TryGetProperty("viewer_id", out var vid) && vid.ValueKind == JsonValueKind.Number
                    ? vid.GetInt64() : 0;

                // Counterparty (1:1). Para grupos tomamos el primero; igual matcheamos por handle.
                string? handle = null;
                if (thread.TryGetProperty("users", out var users) && users.ValueKind == JsonValueKind.Array
                    && users.GetArrayLength() > 0 && users[0].TryGetProperty("username", out var un))
                    handle = un.GetString();
                if (string.IsNullOrWhiteSpace(handle)) continue;

                if (!thread.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
                    continue;

                foreach (var item in items.EnumerateArray())
                {
                    if (!item.TryGetProperty("item_type", out var itype) || itype.GetString() != "text") continue;

                    long senderId = item.TryGetProperty("user_id", out var uid) && uid.ValueKind == JsonValueKind.Number
                        ? uid.GetInt64() : 0;
                    if (senderId == viewerId) continue; // saliente (lo mandamos nosotros)

                    var text = item.TryGetProperty("text", out var t) ? t.GetString() ?? "" : "";
                    if (string.IsNullOrWhiteSpace(text)) continue;

                    var itemId = "";
                    if (item.TryGetProperty("item_id", out var iid))
                        itemId = iid.ValueKind == JsonValueKind.String ? iid.GetString() ?? "" : iid.GetRawText();

                    // timestamp de IG: microsegundos desde epoch.
                    var ts = DateTimeOffset.UtcNow;
                    if (item.TryGetProperty("timestamp", out var tsp))
                    {
                        long micros = tsp.ValueKind == JsonValueKind.Number ? tsp.GetInt64()
                            : long.TryParse(tsp.GetString(), out var parsed) ? parsed : 0;
                        if (micros > 0) ts = DateTimeOffset.FromUnixTimeMilliseconds(micros / 1000);
                    }

                    result.Add(new InstagramInboundMessage(handle!.TrimStart('@'), itemId, text, ts));
                }
            }

            _log.LogInformation("ReadInbox: {Count} mensajes entrantes en {Threads} threads",
                result.Count, threads.GetArrayLength());
        }
        catch (Exception ex)
        {
            _log.LogWarning("ReadInbox: parseo falló: {Err}", ex.Message);
        }

        return result;
    }

    /// <summary>
    /// Baja los últimos posts públicos de un competidor con la sesión LOGUEADA, vía fetch
    /// same-origin al endpoint web (web_profile_info). Es un READ (GET), bajo riesgo — mismo
    /// patrón que ReadInboxAsync. Reemplaza el scraper de Apify. NO trae comentarios (ese
    /// endpoint solo da metadata del post). Lista vacía si el perfil no cargó o no hay sesión.
    /// </summary>
    public async Task<List<InstagramScrapedPost>> ScrapeCompetitorPostsAsync(string handle, int maxPosts, CancellationToken ct = default)
    {
        EnsureLoggedIn();
        var result = new List<InstagramScrapedPost>();
        handle = handle.TrimStart('@').Trim();
        if (handle.Length == 0) return result;

        // El fetch tiene que ser same-origin (www.instagram.com) para mandar cookies + headers.
        if (string.IsNullOrEmpty(_page!.Url) || !_page.Url.Contains("instagram.com"))
        {
            await _page.GotoAsync("https://www.instagram.com/", new PageGotoOptions
            {
                WaitUntil = WaitUntilState.DOMContentLoaded,
                Timeout = _opts.NavigationTimeoutMs
            });
            await Task.Delay(1500, ct);
        }

        string raw;
        try
        {
            raw = await _page.EvaluateAsync<string>(@"async (username) => {
                const url = `/api/v1/users/web_profile_info/?username=${encodeURIComponent(username)}`;
                const res = await fetch(url, { headers: { 'X-IG-App-ID': '936619743392459' }, credentials: 'include' });
                return JSON.stringify({ status: res.status, body: res.ok ? await res.json() : null });
            }", handle);
        }
        catch (Exception ex)
        {
            _log.LogWarning("ScrapeCompetitorPosts @{Handle}: fetch falló: {Err}", handle, ex.Message);
            return result;
        }

        try
        {
            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;
            if (!root.TryGetProperty("body", out var body) || body.ValueKind != JsonValueKind.Object)
            {
                var status = root.TryGetProperty("status", out var s) ? s.ToString() : "?";
                _log.LogWarning("ScrapeCompetitorPosts @{Handle}: respuesta inesperada de IG (status {Status})", handle, status);
                return result;
            }
            if (!body.TryGetProperty("data", out var data)
                || !data.TryGetProperty("user", out var user) || user.ValueKind != JsonValueKind.Object
                || !user.TryGetProperty("edge_owner_to_timeline_media", out var media)
                || !media.TryGetProperty("edges", out var edges) || edges.ValueKind != JsonValueKind.Array)
                return result;

            foreach (var edge in edges.EnumerateArray())
            {
                if (result.Count >= maxPosts) break;
                if (!edge.TryGetProperty("node", out var node)) continue;

                var externalId = (node.TryGetProperty("id", out var idEl) ? idEl.GetString() : null)
                    ?? (node.TryGetProperty("shortcode", out var scEl) ? scEl.GetString() : null);
                if (string.IsNullOrWhiteSpace(externalId)) continue;
                var shortcode = node.TryGetProperty("shortcode", out var sc2) ? sc2.GetString() : null;

                string? caption = null;
                if (node.TryGetProperty("edge_media_to_caption", out var capE)
                    && capE.TryGetProperty("edges", out var capEs) && capEs.ValueKind == JsonValueKind.Array
                    && capEs.GetArrayLength() > 0 && capEs[0].TryGetProperty("node", out var capN)
                    && capN.TryGetProperty("text", out var capT))
                    caption = capT.GetString();

                var isVideo = node.TryGetProperty("is_video", out var iv) && iv.ValueKind == JsonValueKind.True;
                var displayUrl = node.TryGetProperty("display_url", out var du) ? du.GetString() : null;
                var videoUrl = node.TryGetProperty("video_url", out var vu) ? vu.GetString() : null;
                DateTimeOffset? postedAt = node.TryGetProperty("taken_at_timestamp", out var tsEl) && tsEl.ValueKind == JsonValueKind.Number
                    ? DateTimeOffset.FromUnixTimeSeconds(tsEl.GetInt64()) : null;

                result.Add(new InstagramScrapedPost(
                    externalId!, shortcode, caption, postedAt,
                    EdgeCount(node, "edge_liked_by") ?? EdgeCount(node, "edge_media_preview_like") ?? 0,
                    EdgeCount(node, "edge_media_to_comment") ?? 0,
                    displayUrl, videoUrl, isVideo, node.GetRawText()));
            }
            _log.LogInformation("ScrapeCompetitorPosts @{Handle}: {N} posts", handle, result.Count);
        }
        catch (Exception ex)
        {
            _log.LogWarning("ScrapeCompetitorPosts @{Handle}: parseo falló: {Err}", handle, ex.Message);
        }

        return result;
    }

    private static int? EdgeCount(JsonElement node, string prop)
        => node.TryGetProperty(prop, out var e) && e.ValueKind == JsonValueKind.Object
           && e.TryGetProperty("count", out var c) && c.ValueKind == JsonValueKind.Number ? c.GetInt32() : null;

    /// <summary>
    /// Sigue (follow) al usuario indicado. Devuelve un <see cref="FollowResult"/>
    /// con el resultado: Followed, AlreadyFollowing, NotFound, Blocked o Failed.
    /// Detecta bloqueos de acción (action_blocked) y marca la cuenta.
    /// </summary>
    public async Task<FollowResult> FollowUserAsync(string handle, IReadOnlyList<string>? keywords = null, CancellationToken ct = default)
    {
        EnsureLoggedIn();

        _log.LogInformation("Following {Handle}...", handle);

        try
        {
            await _page!.GotoAsync($"https://www.instagram.com/{handle}/", new PageGotoOptions
            {
                WaitUntil = WaitUntilState.DOMContentLoaded,
                Timeout = _opts.NavigationTimeoutMs
            });

            await RandomDelayAsync();

            // Si la URL contiene login → sesión caída
            if (_page.Url.Contains("/accounts/login"))
            {
                _log.LogWarning("Sesión caída al intentar follow {Handle}", handle);
                _loggedIn = false;
                return FollowResult.Failed;
            }

            // Perfil no existe
            var notFoundHeading = await _page.QuerySelectorAsync("text=Sorry, this page isn't available");
            if (notFoundHeading is not null)
            {
                _log.LogInformation("Perfil {Handle} no existe", handle);
                return FollowResult.NotFound;
            }

            // Filtro por keywords: si la campaña define palabras, seguimos SOLO si el perfil
            // (nombre + bio + rubro — lo visible del header) MENCIONA alguna. Si no, se saltea.
            // Aprovecha esta misma visita (no hay carga extra). Si no se puede leer el texto,
            // no filtramos (dejamos pasar) para no frenar todo por un fallo de lectura.
            if (keywords is { Count: > 0 })
            {
                string profileText = "";
                try
                {
                    profileText = await _page.EvaluateAsync<string>(
                        "() => (document.querySelector('header')?.innerText || document.querySelector('main')?.innerText || document.body?.innerText || '').slice(0, 2500)");
                }
                catch { /* lectura best-effort */ }

                if (!string.IsNullOrWhiteSpace(profileText) && !MatchesAnyKeyword(profileText, keywords))
                {
                    _log.LogInformation("Perfil {Handle} no menciona keywords de la campaña → skip", handle);
                    return FollowResult.SkippedNoMatch;
                }
            }

            // Botón "Following" → ya lo seguimos
            var alreadyFollowing = await _page.QuerySelectorAsync(
                "header button:has-text('Following'), header button:has-text('Siguiendo')");
            if (alreadyFollowing is not null)
            {
                _log.LogInformation("Ya seguíamos a {Handle}", handle);
                return FollowResult.AlreadyFollowing;
            }

            // Botón "Follow" / "Seguir"
            var followBtn = await _page.QuerySelectorAsync(
                "header button:has-text('Follow'), header button:has-text('Seguir')");
            if (followBtn is null)
            {
                _log.LogWarning("No se encontró botón Follow para {Handle}", handle);
                return FollowResult.Failed;
            }

            await followBtn.ClickAsync();
            await Task.Delay(2500, ct);

            // ¿Apareció dialog de action_blocked?
            var blockedDialog = await _page.QuerySelectorAsync(
                "div[role='dialog']:has-text('Try Again Later'), " +
                "div[role='dialog']:has-text('Action Blocked'), " +
                "div[role='dialog']:has-text('Acción bloqueada')");

            if (blockedDialog is not null)
            {
                _log.LogWarning("Action Blocked al seguir a {Handle}", handle);
                return FollowResult.Blocked;
            }

            // Verificar que el botón ahora dice Following
            var nowFollowing = await _page.QuerySelectorAsync(
                "header button:has-text('Following'), header button:has-text('Siguiendo'), " +
                "header button:has-text('Requested'), header button:has-text('Solicitado')");

            if (nowFollowing is null)
            {
                _log.LogWarning("Follow no confirmado para {Handle}", handle);
                return FollowResult.Failed;
            }

            _log.LogInformation("Follow exitoso a {Handle}", handle);
            return FollowResult.Followed;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Error al hacer follow a {Handle}", handle);
            return FollowResult.Failed;
        }
    }

    /// <summary>True si <paramref name="text"/> contiene alguna keyword (case/acento-insensible).</summary>
    private static bool MatchesAnyKeyword(string text, IReadOnlyList<string> keywords)
    {
        var norm = NormalizeForMatch(text);
        foreach (var k in keywords)
        {
            var kk = NormalizeForMatch(k);
            if (kk.Length > 0 && norm.Contains(kk, StringComparison.Ordinal)) return true;
        }
        return false;
    }

    /// <summary>Lowercase + saca tildes/diacríticos, para matchear sin importar acentos ni mayúsculas.</summary>
    private static string NormalizeForMatch(string s)
    {
        var decomposed = s.ToLowerInvariant().Normalize(System.Text.NormalizationForm.FormD);
        var sb = new System.Text.StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
            if (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.NonSpacingMark)
                sb.Append(c);
        return sb.ToString();
    }

    /// <summary>
    /// Verifica si la sesión sigue activa navegando a la página principal.
    /// </summary>
    public async Task<bool> CheckSessionAsync(CancellationToken ct = default)
    {
        try
        {
            // OJO: NO usar NetworkIdle — el feed de IG mantiene conexiones abiertas
            // y nunca "asienta", así que por proxy timeouteaba y daba falso "caída".
            await _page!.GotoAsync("https://www.instagram.com/", new PageGotoOptions
            {
                WaitUntil = WaitUntilState.DOMContentLoaded,
                Timeout = _opts.NavigationTimeoutMs
            });
            await Task.Delay(2000, ct);

            // Deslogueado si IG redirige a login o muestra el form de login inline.
            if (_page.Url.Contains("/accounts/login")) return false;
            var loginField = await _page.QuerySelectorAsync(UsernameSelector);
            return loginField is null;
        }
        catch (Exception ex)
        {
            _log.LogWarning("CheckSession para {User} falló: {Err}", _page?.Url, ex.Message);
            return false;
        }
    }

    /// <summary>
    /// Envía el código 2FA que el usuario ingresó desde la UI
    /// y completa el login.
    /// </summary>
    public async Task<bool> SubmitTwoFactorCodeAsync(InstagramAccount account, string code, CancellationToken ct = default)
    {
        EnsureLoggedIn();

        var twoFactorInput = await _page!.QuerySelectorAsync(TwoFactorSelector);
        if (twoFactorInput is null)
        {
            _log.LogWarning("No se encontró el input de 2FA. Quizás ya expiró la página de login.");
            return false;
        }

        _log.LogInformation("Enviando código 2FA para {User}...", account.Username);

        await twoFactorInput.FillAsync(code);
        await RandomDelayAsync();
        await twoFactorInput.PressAsync("Enter");
        await Task.Delay(5000, ct);

        // Verificar si el código era correcto
        var stillHasTwoFactor = await _page.QuerySelectorAsync(TwoFactorSelector);
        if (stillHasTwoFactor is not null)
        {
            _log.LogWarning("Código 2FA incorrecto para {User}", account.Username);
            return false;
        }

        // Completar login (Save Info, Notifications, etc.)
        await CompleteLoginAsync(account, ct);
        return true;
    }

    /// <summary>
    /// Expone la página de Playwright para que el StructureAnalyzer
    /// pueda navegar y extraer HTML.
    /// </summary>
    public IPage? Page => _page;

    /// <summary>
    /// Guarda las cookies actuales en la DB para persistir la sesión.
    /// </summary>
    public async Task SaveSessionCookiesAsync(InstagramAccount account, CancellationToken ct = default)
    {
        if (_context is null) return;

        var cookies = await _context.CookiesAsync();
        var cookieList = cookies.Select(c => new BrowserContextCookies
        {
            Name = c.Name,
            Value = c.Value,
            Domain = c.Domain,
            Path = c.Path,
            HttpOnly = c.HttpOnly,
            Secure = c.Secure,
            SameSite = c.SameSite,
            Expires = c.Expires
        }).ToList();

        account.SessionCookiesJson = JsonSerializer.Serialize(cookieList);
        account.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Completa el login: cierra dialogs de Save Info, Notifications,
    /// verifica que estemos en la página principal y guarda cookies.
    /// </summary>
    private async Task CompleteLoginAsync(InstagramAccount account, CancellationToken ct)
    {
        // Cerrar diálogos post-login ("¿Guardar info de inicio de sesión?",
        // "Activar notificaciones"). El botón visible suele ser "Not Now" / "Ahora no"
        // (en/es, locale es-AR) y puede ser <button> o <div role=button>.
        const string dismissSel =
            "button:has-text('Not Now'), button:has-text('Ahora no'), " +
            "div[role='button']:has-text('Not Now'), div[role='button']:has-text('Ahora no')";
        for (var i = 0; i < 2; i++)
        {
            var dismiss = await _page!.QuerySelectorAsync(dismissSel);
            if (dismiss is null) break;
            await dismiss.ClickAsync();
            await Task.Delay(2000, ct);
        }

        await Task.Delay(3000, ct);

        // Verificar si estamos en la página principal (login exitoso)
        var currentUrl = _page.Url;
        if (currentUrl.Contains("login") || currentUrl.Contains("challenge"))
        {
            // El login no se completó (sigue en login / pide challenge). Esto NO es un
            // action-block de Instagram: puede ser 2FA no resuelto, un challenge, timing
            // o credenciales. NO marcamos IsActionBlocked — ese flag se reserva para el
            // diálogo real de "Action Blocked" en los follows (+24h). Acá solo logueamos
            // y dejamos la cuenta libre para reintentar, sin cooldown fantasma de 1h.
            _log.LogWarning("Login no se completó para {User}. URL: {Url}", account.Username, currentUrl);
            // Diagnóstico: capturar qué texto muestra Instagram (throttle, password,
            // checkpoint, "esperá unos minutos", etc.) para no adivinar la causa.
            try
            {
                var bodyText = await _page.InnerTextAsync("body");
                bodyText = bodyText.Replace("\n", " ").Trim();
                if (bodyText.Length > 700) bodyText = bodyText[..700];
                _log.LogWarning("IG mostró a {User}: «{Text}»", account.Username, bodyText);
            }
            catch (Exception diagEx)
            {
                _log.LogWarning("No se pudo leer el texto de la página de IG para {User}: {Err}",
                    account.Username, diagEx.Message);
            }
            account.IsLoggedIn = false;
            account.UpdatedAt = DateTimeOffset.UtcNow;
            await _db.SaveChangesAsync(ct);
            throw new InvalidOperationException(
                $"Login no se completó para {account.Username} (sigue en login/challenge).");
        }

        _loggedIn = true;
        account.IsLoggedIn = true;
        account.IsActionBlocked = false;
        account.BlockedUntil = null;
        account.IsAwaitingTwoFactor = false;
        account.LastLoginAt = DateTimeOffset.UtcNow;

        // Guardar cookies de sesión
        await SaveSessionCookiesAsync(account, ct);

        _log.LogInformation("Login exitoso en Instagram como {User}", account.Username);
    }

    private void EnsureLoggedIn()
    {
        if (!_loggedIn || _page is null)
            throw new InvalidOperationException("InstagramClient no está inicializado o logueado. Llame InitializeAsync primero.");
    }

    private async Task RandomDelayAsync()
    {
        var delay = Random.Shared.Next(_opts.MinActionDelayMs, _opts.MaxActionDelayMs);
        await Task.Delay(delay);
    }

    private static string GenerateTotpCode(string secret)
    {
        // Implementación simple de TOTP (RFC 6238) para 2FA.
        // El secret de Instagram viene en Base32 (RFC 4648): hay que DECODIFICARLO
        // a bytes crudos, no tomar el string como bytes (eso generaba códigos inválidos).
        var key = Base32Decode(secret);
        var time = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 30;
        var timeBytes = BitConverter.GetBytes(time);
        if (BitConverter.IsLittleEndian) Array.Reverse(timeBytes);

        using var hmac = new System.Security.Cryptography.HMACSHA1(key);
        var hash = hmac.ComputeHash(timeBytes);
        var offset = hash[^1] & 0x0F;
        var binary = (hash[offset] & 0x7F) << 24
                     | (hash[offset + 1] & 0xFF) << 16
                     | (hash[offset + 2] & 0xFF) << 8
                     | (hash[offset + 3] & 0xFF);

        var otp = binary % 1_000_000;
        return otp.ToString("D6");
    }

    // Token de sesión rotable dentro del username del proxy (formato 2captcha:
    // ...-session-XXXXX-...). Sirve para pedir otro peer de salida.
    private static readonly Regex SessionTokenRegex =
        new("session-[^-:@]+", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Reintentos sobre la MISMA sesión (peer pinneado) antes de concluir que el
    // peer está caído de verdad. Evita saltar de ciudad por un fallo transitorio
    // (timeout, blip de red): el city-pin solo se abandona si falla repetidamente.
    private const int StableSessionRetries = 3;

    // Backoff corto entre reintentos de la sesión estable (ms).
    private const int StableRetryDelayMs = 1_500;

    // Una vez descartado el peer pinneado, cuántos peers alternativos probar.
    // Rotar = cambiar de ciudad, así que lo mantenemos acotado y lo logueamos fuerte.
    private const int MaxRotationTries = 3;

    /// <summary>
    /// Arma la lista ORDENADA de proxies a probar (failover): primero el primario
    /// (el de la cuenta si tiene, si no el global <see cref="InstagramOptions.ProxyUrl"/>),
    /// y después los <see cref="InstagramOptions.FallbackProxies"/> globales. Tanto el
    /// primario como cada fallback pueden traer VARIOS proxies separados por coma/;/salto
    /// de línea. Deduplica preservando el orden. Vacía = sin proxy configurado.
    /// </summary>
    private List<string> BuildProxyCandidates(string? accountProxy)
    {
        var list = new List<string>();

        static IEnumerable<string> Split(string? s) =>
            string.IsNullOrWhiteSpace(s)
                ? Array.Empty<string>()
                : s.Split(new[] { ',', ';', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        // Primario: el de la cuenta manda; si no tiene, el global. (No se mezclan:
        // una cuenta con proxy propio no hereda el primario global, pero SÍ los fallbacks.)
        var primary = !string.IsNullOrWhiteSpace(accountProxy) ? accountProxy : _opts.ProxyUrl;
        list.AddRange(Split(primary));

        // Respaldos globales, en orden.
        foreach (var fb in _opts.FallbackProxies ?? new())
            list.AddRange(Split(fb));

        // Dedup preservando orden (case-insensitive por si repiten mayúsculas).
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return list.Where(p => seen.Add(p)).ToList();
    }

    /// <summary>
    /// Recorre los proxies candidatos en orden y devuelve el PRIMERO cuya sesión llega
    /// a Instagram. Es el failover entre proxies distintos (ej. túnel de la compu → iProxy
    /// del celu → IPRoyal). Cada candidato, internamente, hace su propio pin+rotación de
    /// ciudad antes de darse por caído. Null si NINGUNO llega.
    /// </summary>
    private async Task<Proxy?> ResolveWorkingProxyAsync(IReadOnlyList<string> candidates, string accountUser, CancellationToken ct)
    {
        for (var i = 0; i < candidates.Count; i++)
        {
            var resolved = await ResolveOneProxyAsync(candidates[i], accountUser, ct);
            if (resolved is not null)
            {
                if (i > 0)
                    _log.LogWarning("Proxy de {User}: FAILOVER al candidato #{N}/{Total} — el/los anterior/es no llegaron a IG.",
                        accountUser, i + 1, candidates.Count);
                return resolved;
            }

            _log.LogWarning("Proxy de {User}: candidato #{N}/{Total} no llegó a IG — probando el siguiente.",
                accountUser, i + 1, candidates.Count);
        }
        return null;
    }

    /// <summary>
    /// Devuelve un proxy cuya sesión llega a Instagram con certificado válido,
    /// PRIORIZANDO mantener el peer pinneado (misma IP/ciudad) para no disparar
    /// "viajes imposibles" en IG. Solo rota a otro peer (cambio de ciudad) si la
    /// sesión pinneada falla repetidamente — un fallo transitorio no la abandona.
    /// Null si ni el peer pinneado ni los alternativos sirven.
    /// </summary>
    private async Task<Proxy?> ResolveOneProxyAsync(string rawProxy, string accountUser, CancellationToken ct)
    {
        var baseProxy = BuildProxy(rawProxy);
        if (baseProxy is null) return null;

        // Fase 1: insistir con la sesión PINNEADA. Un fallo transitorio no debe
        // hacernos saltar de peer — reintentamos la misma sesión con backoff.
        for (var attempt = 1; attempt <= StableSessionRetries; attempt++)
        {
            if (await ProxyReachesInstagramAsync(baseProxy, ct))
                return baseProxy;

            _log.LogWarning("Proxy de {User}: la sesión pinneada no llegó a IG (intento {N}/{Max}).",
                accountUser, attempt, StableSessionRetries);

            if (attempt < StableSessionRetries)
                await Task.Delay(StableRetryDelayMs, ct);
        }

        // Sin token de sesión no hay nada que rotar: el peer pinneado es el único.
        if (baseProxy.Username is null || !SessionTokenRegex.IsMatch(baseProxy.Username))
        {
            _log.LogWarning("Proxy de {User}: sesión pinneada caída y sin token de sesión para rotar.",
                accountUser);
            return null;
        }

        // Fase 2: el peer pinneado parece caído de verdad. Recién acá rotamos a
        // otro peer, lo que implica un cambio de ciudad — lo logueamos fuerte.
        _log.LogWarning("Proxy de {User}: abandonando el peer pinneado tras {N} fallos — ROTANDO de ciudad.",
            accountUser, StableSessionRetries);

        for (var rot = 1; rot <= MaxRotationTries; rot++)
        {
            var token = "s" + Guid.NewGuid().ToString("N")[..8];
            var candidate = new Proxy
            {
                Server = baseProxy.Server,
                Username = SessionTokenRegex.Replace(baseProxy.Username!, "session-" + token),
                Password = baseProxy.Password
            };

            if (await ProxyReachesInstagramAsync(candidate, ct))
            {
                _log.LogInformation("Proxy de {User}: peer alternativo OK al intento {N} (nueva ciudad).",
                    accountUser, rot);
                return candidate;
            }

            _log.LogWarning("Proxy de {User}: peer alternativo no llegó a IG (intento {N}/{Max}).",
                accountUser, rot, MaxRotationTries);
        }

        return null;
    }

    /// <summary>
    /// Verifica vía HTTP que el proxy llega a la página de login de Instagram con un
    /// certificado VÁLIDO. Si el peer hace MITM (cert self-signed) o bloquea el dominio,
    /// la validación TLS por defecto tira excepción y devolvemos false (no exponemos
    /// credenciales por ese peer).
    /// </summary>
    private Task<bool> ProxyReachesInstagramAsync(Proxy proxy, CancellationToken ct)
        => ProbeReachesInstagramAsync(proxy, _log, ct);

    /// <summary>
    /// Probe BARATO (HttpClient, sin browser) de si un proxy llega a la página de login de
    /// Instagram con cert válido. Lo usa el self-heal (InstagramReloginWorker) para gatear:
    /// si el túnel está caído no spinnea logins. <paramref name="rawProxy"/> en formato URI
    /// (scheme://user:pass@host:port) o lista 2captcha; null/vacío o no parseable = false.
    /// </summary>
    public static Task<bool> ProbeProxyReachesInstagramAsync(string? rawProxy, ILogger? log, CancellationToken ct = default)
    {
        var proxy = BuildProxy(rawProxy);
        return proxy is null ? Task.FromResult(false) : ProbeReachesInstagramAsync(proxy, log, ct);
    }

    private static async Task<bool> ProbeReachesInstagramAsync(Proxy proxy, ILogger? log, CancellationToken ct)
    {
        try
        {
            var webProxy = new System.Net.WebProxy(proxy.Server);
            if (!string.IsNullOrEmpty(proxy.Username))
                webProxy.Credentials = new System.Net.NetworkCredential(proxy.Username, proxy.Password);

            using var handler = new HttpClientHandler { Proxy = webProxy, UseProxy = true };
            using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd(
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
                "(KHTML, like Gecko) Chrome/125.0.0.0 Safari/537.36");

            using var resp = await http.GetAsync(
                "https://www.instagram.com/accounts/login/",
                HttpCompletionOption.ResponseHeadersRead, ct);

            return (int)resp.StatusCode is >= 200 and < 400;
        }
        catch (Exception ex)
        {
            log?.LogDebug("Verificación de proxy falló: {Err}", ex.Message);
            return false;
        }
    }

    /// <summary>
    /// Construye un <see cref="Proxy"/> de Playwright a partir de un string en
    /// formato URI (scheme://user:pass@host:port) o lista 2captcha (host:port:user:pass
    /// o host:port). Devuelve null si está vacío o no se puede parsear.
    /// </summary>
    private static Proxy? BuildProxy(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        raw = raw.Trim();

        // Formato URI: scheme://[user:pass@]host:port
        if (raw.Contains("://"))
        {
            try
            {
                var uri = new Uri(raw);
                var proxy = new Proxy { Server = $"{uri.Scheme}://{uri.Host}:{uri.Port}" };
                if (!string.IsNullOrEmpty(uri.UserInfo))
                {
                    var parts = uri.UserInfo.Split(':', 2);
                    proxy.Username = Uri.UnescapeDataString(parts[0]);
                    if (parts.Length > 1) proxy.Password = Uri.UnescapeDataString(parts[1]);
                }
                return proxy;
            }
            catch
            {
                return null;
            }
        }

        // Formato lista de 2captcha: host:port:user:pass  (o host:port sin auth)
        var seg = raw.Split(':');
        if (seg.Length == 4)
            return new Proxy { Server = $"http://{seg[0]}:{seg[1]}", Username = seg[2], Password = seg[3] };
        if (seg.Length == 2)
            return new Proxy { Server = $"http://{seg[0]}:{seg[1]}" };

        return null;
    }

    /// <summary>
    /// Decodifica un secret en Base32 (RFC 4648) a sus bytes crudos.
    /// Tolera espacios, guiones, minúsculas y padding '='.
    /// </summary>
    private static byte[] Base32Decode(string input)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var clean = input.Trim()
            .Replace(" ", "").Replace("-", "")
            .TrimEnd('=')
            .ToUpperInvariant();

        var output = new List<byte>(clean.Length * 5 / 8);
        var bits = 0;
        var value = 0;
        foreach (var c in clean)
        {
            var idx = alphabet.IndexOf(c);
            if (idx < 0) continue; // ignorar caracteres fuera del alfabeto Base32
            value = (value << 5) | idx;
            bits += 5;
            if (bits >= 8)
            {
                output.Add((byte)((value >> (bits - 8)) & 0xFF));
                bits -= 8;
            }
        }

        return output.ToArray();
    }

    /// <summary>
    /// Postea una imagen en Instagram desde una URL pública.
    /// Descarga la imagen, la sube como post con la caption indicada.
    /// </summary>
    public async Task<bool> PostImageAsync(string imageUrl, string caption, CancellationToken ct = default)
    {
        EnsureLoggedIn();

        _log.LogInformation("Posteando imagen en Instagram...");

        try
        {
            await _page!.GotoAsync("https://www.instagram.com/", new PageGotoOptions
            {
                WaitUntil = WaitUntilState.DOMContentLoaded,
                Timeout = _opts.NavigationTimeoutMs
            });

            await RandomDelayAsync();

            var createBtn = await _page.QuerySelectorAsync(
                "svg[aria-label='New post'], " +
                "a[href='/create/style/'], " +
                "div[role='button']:has-text('Create')");

            if (createBtn is null)
            {
                await _page.GotoAsync("https://www.instagram.com/create/style/", new PageGotoOptions
                {
                    WaitUntil = WaitUntilState.DOMContentLoaded,
                    Timeout = _opts.NavigationTimeoutMs
                });
                await Task.Delay(3000, ct);
            }
            else
            {
                await createBtn.ClickAsync();
                await Task.Delay(2000, ct);
            }

            var tempPath = Path.Combine(Path.GetTempPath(), $"ig_post_{Guid.NewGuid()}.jpg");
            try
            {
                using var http = new HttpClient();
                var imageBytes = await http.GetByteArrayAsync(imageUrl, ct);
                await File.WriteAllBytesAsync(tempPath, imageBytes, ct);

                var fileInput = await _page.QuerySelectorAsync("input[type='file']");
                if (fileInput is null)
                {
                    _log.LogWarning("No se encontró el input de archivo para subir imagen");
                    return false;
                }

                await fileInput.SetInputFilesAsync(tempPath);
                await Task.Delay(4000, ct);

                for (var step = 0; step < 3; step++)
                {
                    var nextBtn = await _page.QuerySelectorAsync(
                        "button:has-text('Next'), " +
                        "div[role='button']:has-text('Next'), " +
                        "button:has-text('Siguiente'), " +
                        "div[role='button']:has-text('Siguiente')");

                    if (nextBtn is null) break;
                    await nextBtn.ClickAsync();
                    await Task.Delay(2500, ct);
                }

                var captionInput = await _page.QuerySelectorAsync(
                    "div[aria-label='Write a caption...'], " +
                    "div[aria-label='Escribe un pie de foto...'], " +
                    "textarea[aria-label='Write a caption...']");

                if (captionInput is not null)
                {
                    await captionInput.ClickAsync();
                    await captionInput.FillAsync(caption);
                    await RandomDelayAsync();
                }

                var shareBtn = await _page.QuerySelectorAsync(
                    "button:has-text('Share'), " +
                    "div[role='button']:has-text('Share'), " +
                    "button:has-text('Compartir'), " +
                    "div[role='button']:has-text('Compartir')");

                if (shareBtn is null)
                {
                    _log.LogWarning("No se encontró botón Share");
                    return false;
                }

                await shareBtn.ClickAsync();
                await Task.Delay(5000, ct);

                var posted = _page.Url.Contains("instagram.com") &&
                             !_page.Url.Contains("create");

                _log.LogInformation("Post {Result}", posted ? "exitoso" : "falló");
                return posted;
            }
            finally
            {
                if (File.Exists(tempPath))
                    File.Delete(tempPath);
            }
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Error al postear imagen");
            return false;
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            _page?.CloseAsync();
            _context?.CloseAsync();
            _browser?.CloseAsync();
            _playwright?.Dispose();
        }
        catch { /* ignore cleanup errors */ }
    }

    // Clases auxiliares para serialización de cookies
    private class BrowserContextCookies
    {
        public string Name { get; set; } = string.Empty;
        public string Value { get; set; } = string.Empty;
        public string Domain { get; set; } = string.Empty;
        public string Path { get; set; } = "/";
        public bool HttpOnly { get; set; }
        public bool Secure { get; set; }
        public SameSiteAttribute? SameSite { get; set; } = SameSiteAttribute.Lax;
        public float Expires { get; set; }
    }
}

/// <summary>
/// Resultado de un intento de seguir a un perfil.
/// </summary>
public enum FollowResult
{
    Followed = 0,
    AlreadyFollowing = 1,
    NotFound = 2,
    Blocked = 3,
    Failed = 4,
    /// <summary>El perfil no menciona ninguna de las keywords de la campaña → no se siguió.</summary>
    SkippedNoMatch = 5
}

/// <summary>
/// Perfil scrapeado de Instagram.
/// </summary>
public class InstagramProfile
{
    public string Handle { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;       // "followers", "hashtag_commenters", "location"
    public string SourceTarget { get; set; } = string.Empty; // la cuenta/hashtag/location de donde se obtuvo
}
