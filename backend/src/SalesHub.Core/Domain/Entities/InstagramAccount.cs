namespace SalesHub.Core.Domain.Entities;

public class InstagramAccount
{
    public Guid Id { get; set; }

    public Guid SellerId { get; set; }
    public Seller? Seller { get; set; }

    public string Username { get; set; } = string.Empty;
    /// <summary>Encriptado con AES. Nunca en texto plano.</summary>
    public string EncryptedPassword { get; set; } = string.Empty;
    public string? TwoFactorSecret { get; set; }

    /// <summary>
    /// Proxy por cuenta para que cada cuenta saliente coincida con su geo real
    /// (ej. gymhero → residencial AR). Formato: scheme://user:pass@host:port
    /// o host:port:user:pass. Si es null, cae al proxy global de InstagramOptions.
    /// </summary>
    public string? ProxyUrl { get; set; }

    /// <summary>Cookies de sesión serializadas como JSON para persistir el login.</summary>
    public string? SessionCookiesJson { get; set; }

    public bool IsActive { get; set; } = true;
    public bool IsLoggedIn { get; set; }

    /// <summary>Si Instagram nos bloqueó temporalmente (action_blocked).</summary>
    public bool IsActionBlocked { get; set; }
    public DateTimeOffset? BlockedUntil { get; set; }

    /// <summary>
    /// Si Instagram pidió 2FA y estamos esperando que el usuario
    /// ingrese el código desde la UI.
    /// </summary>
    public bool IsAwaitingTwoFactor { get; set; }

    public DateTimeOffset? LastLoginAt { get; set; }
    public DateTimeOffset? LastUsedAt { get; set; }

    /// <summary>
    /// Paceo a nivel CUENTA: no se ejecuta otro follow hasta esta hora. Se setea después de
    /// cada intento con un gap aleatorio (jitter) para espaciar los follows como un humano
    /// y no dispararlos en ráfaga al abrir la ventana (eso gatilla action-block). Es por
    /// cuenta (no por campaña) para que dos campañas de la misma cuenta no se sumen y bursteen.
    /// </summary>
    public DateTimeOffset? NextFollowEligibleAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
