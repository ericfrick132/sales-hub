namespace SalesHub.Api.Dtos;

public record LoginRequest(string Email, string Password);
public record GoogleLoginRequest(string IdToken);

public record AuthResponse(
    string AccessToken,
    Guid SellerId,
    string SellerKey,
    string DisplayName,
    string Email,
    string Role,
    List<string> VerticalsWhitelist);
