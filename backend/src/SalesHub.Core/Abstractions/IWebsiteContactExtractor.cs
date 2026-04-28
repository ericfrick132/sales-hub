namespace SalesHub.Core.Abstractions;

public record WebsiteContactInfo(string? Phone, string? InstagramHandle, string? FacebookUrl);

public interface IWebsiteContactExtractor
{
    Task<WebsiteContactInfo> ExtractAsync(string url, CancellationToken ct = default);
}
