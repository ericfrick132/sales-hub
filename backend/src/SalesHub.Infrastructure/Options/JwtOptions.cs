namespace SalesHub.Infrastructure.Options;

public class JwtOptions
{
    public string Issuer { get; set; } = "sales-hub";
    public string Audience { get; set; } = "sales-hub-users";
    public string SigningKey { get; set; } = string.Empty;
    public int ExpiryMinutes { get; set; } = 60 * 24 * 7; // 7 days
}
