namespace SalesHub.Infrastructure.Options;

/// <summary>
/// Config para publicar el contenido SEO en los repos de cada app vía la API de
/// GitHub. El token va en el env del droplet como GitHub__Token (PAT con scope repo).
/// </summary>
public class GitHubOptions
{
    public string Token { get; set; } = string.Empty;
    public string ApiBase { get; set; } = "https://api.github.com";
    /// <summary>Autor de los commits que hace el distribuidor.</summary>
    public string CommitterName { get; set; } = "SalesHub SEO Bot";
    public string CommitterEmail { get; set; } = "seo-bot@efcloud.tech";
}
