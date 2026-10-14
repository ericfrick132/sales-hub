namespace SalesHub.Infrastructure.Instagram;

/// <summary>
/// DMs de las cuentas PROPIAS de Instagram por la API oficial (Messenger Platform para IG):
/// webhook <c>/api/webhooks/meta-instagram</c> (object=instagram, campo messages) y respuestas por
/// <c>POST /{page-id}/messages</c>. Nada que ver con <see cref="InstagramOptions"/>, que es el
/// cliente no oficial (cuentas logueadas que scrapean/mandan DMs de prospección).
/// Sección de config: <c>InstagramMessaging</c>.
/// </summary>
public class InstagramMessagingOptions
{
    /// <summary>Interruptor general: apagado, el webhook responde 200 y no procesa nada.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Verify token del webhook. Vacío = usa <c>Meta:VerifyToken</c>.</summary>
    public string? VerifyToken { get; set; }

    /// <summary>App secret para validar X-Hub-Signature-256. Vacío = usa <c>Meta:AppSecret</c>.</summary>
    public string? AppSecret { get; set; }

    public string GraphVersion { get; set; } = "v23.0";

    /// <summary>
    /// Token de system user (con las páginas asignadas e <c>instagram_manage_messages</c>). Si una
    /// cuenta no trae su <see cref="Account.PageAccessToken"/>, se pide el token de la página con
    /// este (<c>GET /{page-id}?fields=access_token</c>) y se cachea en memoria.
    /// </summary>
    public string? SystemUserToken { get; set; }

    /// <summary>Ventana de respuesta de Meta desde el último mensaje del usuario.</summary>
    public int ReplyWindowHours { get; set; } = 24;

    public List<Account> Accounts { get; set; } = new();

    public class Account
    {
        /// <summary>Id de la cuenta profesional de IG (instagram_business_account.id, 1784…).</summary>
        public string IgUserId { get; set; } = string.Empty;
        /// <summary>Página de Facebook vinculada (la que manda por /{page-id}/messages).</summary>
        public string PageId { get; set; } = string.Empty;
        public string? Username { get; set; }
        /// <summary>App a la que pertenece todo DM que entra a esta cuenta (gymhero, turnospro, playcrew).</summary>
        public string ProductKey { get; set; } = string.Empty;
        /// <summary>Token de la página. Vacío = se deriva de <see cref="SystemUserToken"/>.</summary>
        public string? PageAccessToken { get; set; }
        /// <summary>false = los DMs entran a Conversaciones pero el bot arranca muteado (lo atiende un humano).</summary>
        public bool BotEnabled { get; set; } = true;
    }
}
