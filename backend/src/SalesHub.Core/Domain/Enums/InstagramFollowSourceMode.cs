namespace SalesHub.Core.Domain.Enums;

/// <summary>
/// De qué lista del <c>SourceHandle</c> tomamos las cuentas a seguir en una
/// campaña de auto-follow.
/// </summary>
public enum InstagramFollowSourceMode
{
    /// <summary>Seguir a los SEGUIDORES del perfil source (su lista "followers").</summary>
    Followers = 0,

    /// <summary>Seguir a las cuentas que el perfil source SIGUE (su lista "following").</summary>
    Following = 1,

    /// <summary>
    /// Seguir una LISTA importada de handles (ej. leads scrapeados de la competencia). No
    /// scrapea nada: la cola se llena con POST /{id}/handles y el <c>SourceHandle</c> es
    /// solo el nombre de la lista.
    /// </summary>
    List = 2
}
