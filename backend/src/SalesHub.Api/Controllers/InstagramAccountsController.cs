using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SalesHub.Core.Domain.Entities;
using SalesHub.Infrastructure.Instagram;
using SalesHub.Infrastructure.Persistence;

namespace SalesHub.Api.Controllers;

[ApiController]
[Route("api/instagram-accounts")]
[Authorize(Roles = "Admin")]
public class InstagramAccountsController : ControllerBase
{
    private readonly ApplicationDbContext _db;
    private readonly InstagramEncryptionService _crypto;
    private readonly InstagramOptions _opts;
    private readonly ILogger<InstagramAccountsController> _log;

    public InstagramAccountsController(
        ApplicationDbContext db,
        InstagramEncryptionService crypto,
        InstagramOptions opts,
        ILogger<InstagramAccountsController> log)
    {
        _db = db;
        _crypto = crypto;
        _opts = opts;
        _log = log;
    }

    [HttpGet]
    public async Task<IActionResult> GetAll()
    {
        var accounts = await _db.InstagramAccounts
            .Include(a => a.Seller)
            .OrderBy(a => a.Username)
            .Select(a => new InstagramAccountDto
            {
                Id = a.Id,
                Username = a.Username,
                SellerId = a.SellerId,
                SellerName = a.Seller != null ? a.Seller.DisplayName : null,
                IsActive = a.IsActive,
                IsLoggedIn = a.IsLoggedIn,
                IsActionBlocked = a.IsActionBlocked,
                IsAwaitingTwoFactor = a.IsAwaitingTwoFactor,
                ProxyUrl = a.ProxyUrl,
                BlockedUntil = a.BlockedUntil,
                LastLoginAt = a.LastLoginAt,
                LastUsedAt = a.LastUsedAt,
                CreatedAt = a.CreatedAt
            })
            .ToListAsync();

        return Ok(accounts);
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> GetById(Guid id)
    {
        var account = await _db.InstagramAccounts
            .Include(a => a.Seller)
            .FirstOrDefaultAsync(a => a.Id == id);

        if (account is null) return NotFound();

        return Ok(new InstagramAccountDto
        {
            Id = account.Id,
            Username = account.Username,
            SellerId = account.SellerId,
            SellerName = account.Seller?.DisplayName,
            IsActive = account.IsActive,
            IsLoggedIn = account.IsLoggedIn,
            IsActionBlocked = account.IsActionBlocked,
            IsAwaitingTwoFactor = account.IsAwaitingTwoFactor,
            ProxyUrl = account.ProxyUrl,
            BlockedUntil = account.BlockedUntil,
            LastLoginAt = account.LastLoginAt,
            LastUsedAt = account.LastUsedAt,
            CreatedAt = account.CreatedAt
        });
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateInstagramAccountRequest req)
    {
        if (await _db.InstagramAccounts.AnyAsync(a => a.Username == req.Username))
            return Conflict(new { error = "Ya existe una cuenta con ese username" });

        var account = new InstagramAccount
        {
            Id = Guid.NewGuid(),
            SellerId = req.SellerId,
            Username = req.Username,
            EncryptedPassword = _crypto.Encrypt(req.Password),
            TwoFactorSecret = req.TwoFactorSecret,
            ProxyUrl = string.IsNullOrWhiteSpace(req.ProxyUrl) ? null : req.ProxyUrl.Trim(),
            IsActive = true,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };

        _db.InstagramAccounts.Add(account);
        await _db.SaveChangesAsync();

        _log.LogInformation("Cuenta de Instagram creada: {User} para seller {SellerId}", req.Username, req.SellerId);

        return Ok(new { id = account.Id });
    }

    /// <summary>
    /// Importación masiva de cuentas desde AccsMarket u otros proveedores.
    /// Acepta texto en formato: login:password:2FA_secret (una por línea)
    /// POST /api/instagram-accounts/import
    /// </summary>
    [HttpPost("import")]
    public async Task<IActionResult> Import([FromBody] ImportInstagramAccountsRequest req)
    {
        var lines = req.RawData
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var results = new List<object>();
        var imported = 0;
        var skipped = 0;
        var errors = 0;

        foreach (var line in lines)
        {
            try
            {
                // Formato esperado: login:password:2FA_secret
                var parts = line.Split(':');
                if (parts.Length < 2)
                {
                    results.Add(new { line, status = "skipped", reason = "Formato inválido. Esperado: login:password:2FA_secret" });
                    skipped++;
                    continue;
                }

                var username = parts[0].Trim();
                var password = parts[1].Trim();
                var twoFactorSecret = parts.Length >= 3 ? parts[2].Trim() : null;

                if (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(password))
                {
                    results.Add(new { line, status = "skipped", reason = "Username o password vacío" });
                    skipped++;
                    continue;
                }

                if (await _db.InstagramAccounts.AnyAsync(a => a.Username == username))
                {
                    results.Add(new { line, status = "skipped", reason = "Ya existe" });
                    skipped++;
                    continue;
                }

                var account = new InstagramAccount
                {
                    Id = Guid.NewGuid(),
                    SellerId = req.DefaultSellerId ?? Guid.Empty,
                    Username = username,
                    EncryptedPassword = _crypto.Encrypt(password),
                    TwoFactorSecret = twoFactorSecret,
                    IsActive = true,
                    CreatedAt = DateTimeOffset.UtcNow,
                    UpdatedAt = DateTimeOffset.UtcNow
                };

                _db.InstagramAccounts.Add(account);
                imported++;
                results.Add(new { line, status = "imported" });
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Error al importar línea: {Line}", line);
                results.Add(new { line, status = "error", reason = ex.Message });
                errors++;
            }
        }

        await _db.SaveChangesAsync();

        _log.LogInformation(
            "Importación masiva completada: {Imported} importadas, {Skipped} omitidas, {Errors} errores",
            imported, skipped, errors);

        return Ok(new
        {
            message = $"Importación completada: {imported} importadas, {skipped} omitidas, {errors} errores",
            imported,
            skipped,
            errors,
            results
        });
    }

    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdateInstagramAccountRequest req)
    {
        var account = await _db.InstagramAccounts.FirstOrDefaultAsync(a => a.Id == id);
        if (account is null) return NotFound();

        if (req.SellerId.HasValue) account.SellerId = req.SellerId.Value;
        if (!string.IsNullOrEmpty(req.Password))
            account.EncryptedPassword = _crypto.Encrypt(req.Password);
        if (req.TwoFactorSecret is not null) account.TwoFactorSecret = req.TwoFactorSecret;
        // ProxyUrl: si viene null no se toca; si viene vacío se limpia.
        if (req.ProxyUrl is not null)
            account.ProxyUrl = string.IsNullOrWhiteSpace(req.ProxyUrl) ? null : req.ProxyUrl.Trim();
        if (req.IsActive.HasValue) account.IsActive = req.IsActive.Value;

        // Si se cambió la contraseña, invalidar sesión
        if (!string.IsNullOrEmpty(req.Password))
        {
            account.SessionCookiesJson = null;
            account.IsLoggedIn = false;
        }

        account.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync();

        _log.LogInformation("Cuenta de Instagram actualizada: {User}", account.Username);

        return Ok(new { id = account.Id });
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id)
    {
        var account = await _db.InstagramAccounts.FirstOrDefaultAsync(a => a.Id == id);
        if (account is null) return NotFound();

        _db.InstagramAccounts.Remove(account);
        await _db.SaveChangesAsync();

        _log.LogInformation("Cuenta de Instagram eliminada: {User}", account.Username);

        return NoContent();
    }

    /// <summary>
    /// Hace login de todas las cuentas activas (batch).
    /// POST /api/instagram-accounts/login-all
    /// </summary>
    [HttpPost("login-all")]
    public async Task<IActionResult> LoginAll()
    {
        var accounts = await _db.InstagramAccounts
            .Where(a => a.IsActive && !a.IsActionBlocked)
            .ToListAsync();

        if (accounts.Count == 0)
            return Ok(new { message = "No hay cuentas activas para loguear", results = new List<object>() });

        _log.LogInformation("Login batch solicitado via API: {Count} cuentas", accounts.Count);

        var results = new List<object>();

        foreach (var account in accounts)
        {
            try
            {
                // Si ya está logueada, verificar sesión
                if (account.IsLoggedIn && !string.IsNullOrEmpty(account.SessionCookiesJson))
                {
                    await using var checkClient = new InstagramClient(_db, _crypto, _opts, _log);
                    await checkClient.InitializeAsync(account);
                    var sessionOk = await checkClient.CheckSessionAsync();

                    if (sessionOk)
                    {
                        results.Add(new { username = account.Username, status = "already_logged_in" });
                        continue;
                    }

                    _log.LogInformation("Sesión de {User} expirada, relogueando...", account.Username);
                    account.SessionCookiesJson = null;
                    account.IsLoggedIn = false;
                    await _db.SaveChangesAsync();
                }

                // Hacer login
                await using var client = new InstagramClient(_db, _crypto, _opts, _log);
                await client.InitializeAsync(account);

                results.Add(new { username = account.Username, status = "logged_in" });
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Login batch falló para {User}", account.Username);
                results.Add(new { username = account.Username, status = "failed", error = ex.Message });
            }
        }

        var successCount = results.Count(r => ((string)((dynamic)r).status) != "failed");
        _log.LogInformation("Login batch completado: {Success}/{Total} exitosos", successCount, accounts.Count);

        return Ok(new
        {
            message = $"Login batch completado: {successCount}/{accounts.Count} exitosos",
            results
        });
    }

    /// <summary>
    /// Envía el código 2FA que el usuario ingresó desde la UI.
    /// POST /api/instagram-accounts/{id}/submit-2fa
    /// </summary>
    [HttpPost("{id:guid}/submit-2fa")]
    public async Task<IActionResult> SubmitTwoFactor(Guid id, [FromBody] SubmitTwoFactorRequest req)
    {
        var account = await _db.InstagramAccounts.FirstOrDefaultAsync(a => a.Id == id);
        if (account is null) return NotFound();

        if (!account.IsAwaitingTwoFactor)
            return BadRequest(new { error = "La cuenta no está esperando código 2FA" });

        try
        {
            await using var client = new InstagramClient(_db, _crypto, _opts, _log);
            await client.InitializeAsync(account);

            var success = await client.SubmitTwoFactorCodeAsync(account, req.Code);

            if (!success)
                return BadRequest(new { error = "Código 2FA incorrecto o expiró la página de login" });

            return Ok(new { message = "2FA completado, login exitoso" });
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Error al enviar código 2FA para {User}", account.Username);
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpPost("{id:guid}/login")]
    public async Task<IActionResult> Login(Guid id)
    {
        var account = await _db.InstagramAccounts.FirstOrDefaultAsync(a => a.Id == id);
        if (account is null) return NotFound();

        try
        {
            await using var client = new InstagramClient(_db, _crypto, _opts, _log);
            await client.InitializeAsync(account);

            return Ok(new { message = "Login exitoso", isLoggedIn = account.IsLoggedIn });
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Error al loguear cuenta de Instagram {User}", account.Username);
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>
    /// DIAGNÓSTICO: intenta loguear y devuelve la URL + el texto que muestra IG (para saber
    /// si es contraseña incorrecta, checkpoint de IP/device, o la pantalla de 2FA no detectada).
    /// </summary>
    [HttpPost("{id:guid}/debug-login")]
    public async Task<IActionResult> DebugLogin(Guid id)
    {
        var account = await _db.InstagramAccounts.FirstOrDefaultAsync(a => a.Id == id);
        if (account is null) return NotFound();

        // Forzar re-login limpiando cookies EN MEMORIA (no persistimos).
        account.SessionCookiesJson = null;
        account.IsLoggedIn = false;

        await using var client = new InstagramClient(_db, _crypto, _opts, _log);
        var err = "";
        try { await client.InitializeAsync(account); }
        catch (Exception ex) { err = ex.Message; }

        var page = client.Page;
        if (page is null) return Ok(new { err, note = "sin página (falló antes del browser/proxy)" });

        string url = "", text = "", has2fa = "", hasPwErr = "";
        try { url = page.Url; } catch { }
        try { text = await page.EvaluateAsync<string>("() => (document.body?.innerText || '').replace(/\\s+/g,' ').slice(0,2500)"); } catch { }
        try { has2fa = (await page.QuerySelectorAsync("input[name='verificationCode'], input[autocomplete='one-time-code']")) is not null ? "sí" : "no"; } catch { }

        return Ok(new { err, url, has2faInput = has2fa, text });
    }

    [HttpGet("{id:guid}/metrics")]
    public async Task<IActionResult> GetMetrics(Guid id, [FromQuery] int days = 7)
    {
        var since = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-days));

        var metrics = await _db.InstagramMetrics
            .Where(m => m.InstagramAccountId == id && m.Date >= since)
            .OrderByDescending(m => m.Date)
            .ToListAsync();

        return Ok(metrics);
    }

    [HttpGet("dashboard")]
    public async Task<IActionResult> GetDashboard()
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        var accounts = await _db.InstagramAccounts
            .Include(a => a.Seller)
            .Select(a => new InstagramAccountDashboardDto
            {
                Id = a.Id,
                Username = a.Username,
                SellerName = a.Seller != null ? a.Seller.DisplayName : null,
                IsActive = a.IsActive,
                IsLoggedIn = a.IsLoggedIn,
                IsActionBlocked = a.IsActionBlocked,
                BlockedUntil = a.BlockedUntil,
                LastUsedAt = a.LastUsedAt,
                TodayMetrics = _db.InstagramMetrics
                    .Where(m => m.InstagramAccountId == a.Id && m.Date == today)
                    .Select(m => new InstagramMetricsSummaryDto
                    {
                        ProfilesScraped = m.ProfilesScraped,
                        LeadsCreated = m.LeadsCreated,
                        DmSent = m.DmSent,
                        DmFailed = m.DmFailed,
                        ActionBlocks = m.ActionBlocks
                    })
                    .FirstOrDefault()
            })
            .ToListAsync();

        return Ok(accounts);
    }

    // DTOs
    public record InstagramAccountDto
    {
        public Guid Id { get; init; }
        public string Username { get; init; } = string.Empty;
        public Guid SellerId { get; init; }
        public string? SellerName { get; init; }
        public bool IsActive { get; init; }
        public bool IsLoggedIn { get; init; }
        public bool IsActionBlocked { get; init; }
        public bool IsAwaitingTwoFactor { get; init; }
        public string? ProxyUrl { get; init; }
        public DateTimeOffset? BlockedUntil { get; init; }
        public DateTimeOffset? LastLoginAt { get; init; }
        public DateTimeOffset? LastUsedAt { get; init; }
        public DateTimeOffset CreatedAt { get; init; }
    }

    public record CreateInstagramAccountRequest
    {
        public Guid SellerId { get; init; }
        public string Username { get; init; } = string.Empty;
        public string Password { get; init; } = string.Empty;
        public string? TwoFactorSecret { get; init; }
        public string? ProxyUrl { get; init; }
    }

    public record UpdateInstagramAccountRequest
    {
        public Guid? SellerId { get; init; }
        public string? Password { get; init; }
        public string? TwoFactorSecret { get; init; }
        public bool? IsActive { get; init; }
        public string? ProxyUrl { get; init; }
    }

    public record InstagramAccountDashboardDto
    {
        public Guid Id { get; init; }
        public string Username { get; init; } = string.Empty;
        public string? SellerName { get; init; }
        public bool IsActive { get; init; }
        public bool IsLoggedIn { get; init; }
        public bool IsActionBlocked { get; init; }
        public DateTimeOffset? BlockedUntil { get; init; }
        public DateTimeOffset? LastUsedAt { get; init; }
        public InstagramMetricsSummaryDto? TodayMetrics { get; init; }
    }

    public record InstagramMetricsSummaryDto
    {
        public int ProfilesScraped { get; init; }
        public int LeadsCreated { get; init; }
        public int DmSent { get; init; }
        public int DmFailed { get; init; }
        public int ActionBlocks { get; init; }
    }

    public record SubmitTwoFactorRequest
    {
        public string Code { get; init; } = string.Empty;
    }

    public record ImportInstagramAccountsRequest
    {
        /// <summary>
        /// Texto plano con formato login:password:2FA_secret (una por línea).
        /// Ejemplo:
        ///   user1:pass123:JBSWY3DPEHPK3PXP
        ///   user2:pass456:JBSWY3DPEHPK3PXQ
        /// </summary>
        public string RawData { get; init; } = string.Empty;

        /// <summary>
        /// Seller al que se asignarán todas las cuentas importadas.
        /// Si no se especifica, quedan sin seller.
        /// </summary>
        public Guid? DefaultSellerId { get; init; }
    }
}
