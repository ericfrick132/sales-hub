using System.Text.Json;
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
    private readonly ILogger<InstagramClient> _log;

    private IPlaywright? _playwright;
    private IBrowser? _browser;
    private IBrowserContext? _context;
    private IPage? _page;

    private bool _loggedIn;

    public InstagramClient(
        ApplicationDbContext db,
        InstagramEncryptionService crypto,
        InstagramOptions opts,
        ILogger<InstagramClient> log)
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
                "--disable-gpu"
            }
        };

        if (!string.IsNullOrEmpty(_opts.ProxyUrl))
            launchOpts.Proxy = new Proxy { Server = _opts.ProxyUrl };

        _browser = await _playwright.Chromium.LaunchAsync(launchOpts);

        // Crear contexto con user-agent realista
        _context = await _browser.NewContextAsync(new BrowserNewContextOptions
        {
            UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/125.0.0.0 Safari/537.36",
            ViewportSize = new ViewportSize { Width = 1280, Height = 800 },
            Locale = "es-AR"
        });

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

        // Esperar y llenar campos de login
        await _page.WaitForSelectorAsync("input[name='username']");
        await _page.FillAsync("input[name='username']", account.Username);
        await RandomDelayAsync();
        await _page.FillAsync("input[name='password']", password);
        await RandomDelayAsync();

        // Click en login
        await _page.ClickAsync("button[type='submit']");
        await Task.Delay(5000, ct);

        // Verificar si pide 2FA
        var twoFactorInput = await _page.QuerySelectorAsync("input[name='verificationCode']");
        if (twoFactorInput is not null)
        {
            _log.LogInformation("Instagram pide 2FA para {User}", account.Username);

            if (!string.IsNullOrEmpty(account.TwoFactorSecret))
            {
                // Tenemos el secret, generar código automáticamente
                var code = GenerateTotpCode(account.TwoFactorSecret);
                await _page.FillAsync("input[name='verificationCode']", code);
                await RandomDelayAsync();
                await _page.ClickAsync("button[type='submit']");
                await Task.Delay(5000, ct);
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
    public async Task<List<InstagramProfile>> ScrapeFollowersAsync(string targetHandle, int maxFollowers, CancellationToken ct = default)
    {
        EnsureLoggedIn();
        var profiles = new List<InstagramProfile>();

        _log.LogInformation("Scrapeando seguidores de {Target} (max {Max})...", targetHandle, maxFollowers);

        await _page!.GotoAsync($"https://www.instagram.com/{targetHandle}/", new PageGotoOptions
        {
            WaitUntil = WaitUntilState.NetworkIdle,
            Timeout = _opts.NavigationTimeoutMs
        });

        await RandomDelayAsync();

        // Click en el link de seguidores
        var followersLink = await _page.QuerySelectorAsync($"a[href='/{targetHandle}/followers/']");
        if (followersLink is null)
        {
            _log.LogWarning("No se encontró el link de seguidores para {Target}", targetHandle);
            return profiles;
        }

        await followersLink.ClickAsync();
        await Task.Delay(3000, ct);

        // Scroll para cargar más seguidores
        var followersDialog = await _page.WaitForSelectorAsync("div[role='dialog']", new PageWaitForSelectorOptions
        {
            Timeout = _opts.NavigationTimeoutMs
        });

        if (followersDialog is null)
        {
            _log.LogWarning("No se abrió el dialog de seguidores para {Target}", targetHandle);
            return profiles;
        }

        var lastCount = 0;
        var scrollAttempts = 0;

        while (profiles.Count < maxFollowers && scrollAttempts < 30)
        {
            // Extraer handles visibles
            var links = await _page.QuerySelectorAllAsync("div[role='dialog'] a[href^='/']");
            foreach (var link in links)
            {
                var href = await link.GetAttributeAsync("href");
                if (string.IsNullOrEmpty(href) || href == "/" || href.Contains("/followers/")) continue;

                var handle = href.TrimStart('/').Split('/').FirstOrDefault();
                if (string.IsNullOrEmpty(handle) || profiles.Any(p => p.Handle == handle)) continue;

                var nameEl = await link.QuerySelectorAsync("span");
                var displayName = nameEl is not null ? await nameEl.InnerTextAsync() : handle;

                profiles.Add(new InstagramProfile
                {
                    Handle = handle,
                    DisplayName = displayName,
                    Source = "followers",
                    SourceTarget = targetHandle
                });

                if (profiles.Count >= maxFollowers) break;
            }

            if (profiles.Count == lastCount)
            {
                scrollAttempts++;
                // Hacer scroll en el dialog
                await _page.EvaluateAsync("document.querySelector('div[role=\"dialog\"]')?.scrollBy(0, 300)");
            }
            else
            {
                scrollAttempts = 0;
                lastCount = profiles.Count;
            }

            await Task.Delay(1500, ct);
        }

        _log.LogInformation("Scrapeados {Count} seguidores de {Target}", profiles.Count, targetHandle);
        return profiles;
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
    /// Verifica si la sesión sigue activa navegando a la página principal.
    /// </summary>
    public async Task<bool> CheckSessionAsync(CancellationToken ct = default)
    {
        try
        {
            await _page!.GotoAsync("https://www.instagram.com/", new PageGotoOptions
            {
                WaitUntil = WaitUntilState.NetworkIdle,
                Timeout = 15_000
            });

            var loginBtn = await _page.QuerySelectorAsync("a[href='/accounts/login/']");
            return loginBtn is null;
        }
        catch
        {
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

        var twoFactorInput = await _page!.QuerySelectorAsync("input[name='verificationCode']");
        if (twoFactorInput is null)
        {
            _log.LogWarning("No se encontró el input de 2FA. Quizás ya expiró la página de login.");
            return false;
        }

        _log.LogInformation("Enviando código 2FA para {User}...", account.Username);

        await _page.FillAsync("input[name='verificationCode']", code);
        await RandomDelayAsync();
        await _page.ClickAsync("button[type='submit']");
        await Task.Delay(5000, ct);

        // Verificar si el código era correcto
        var stillHasTwoFactor = await _page.QuerySelectorAsync("input[name='verificationCode']");
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
        // Verificar si hay "Save Info" dialog
        var saveInfo = await _page!.QuerySelectorAsync("div:has-text('Save Info')");
        if (saveInfo is not null)
        {
            var notNow = await _page.QuerySelectorAsync("button:has-text('Not Now')");
            if (notNow is not null)
                await notNow.ClickAsync();
            await Task.Delay(2000, ct);
        }

        // Verificar si hay "Turn on Notifications" dialog
        var notif = await _page.QuerySelectorAsync("button:has-text('Not Now')");
        if (notif is not null)
            await notif.ClickAsync();

        await Task.Delay(3000, ct);

        // Verificar si estamos en la página principal (login exitoso)
        var currentUrl = _page.Url;
        if (currentUrl.Contains("login") || currentUrl.Contains("challenge"))
        {
            _log.LogWarning("Login falló o pidió challenge para {User}. URL: {Url}", account.Username, currentUrl);
            account.IsActionBlocked = true;
            account.BlockedUntil = DateTimeOffset.UtcNow.AddHours(1);
            await _db.SaveChangesAsync(ct);
            throw new InvalidOperationException($"Login falló para {account.Username}. Posible challenge o bloqueo.");
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
        // Implementación simple de TOTP para 2FA
        // En producción usar una librería como Otp.NET
        var key = System.Text.Encoding.UTF8.GetBytes(secret);
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
        public string SameSite { get; set; } = "Lax";
        public float Expires { get; set; }
    }
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
