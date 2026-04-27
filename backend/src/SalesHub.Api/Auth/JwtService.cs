using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using SalesHub.Core.Domain.Entities;
using SalesHub.Infrastructure.Options;

namespace SalesHub.Api.Auth;

public interface IJwtService
{
    string Issue(Seller seller);
}

public class JwtService : IJwtService
{
    private readonly JwtOptions _opts;

    public JwtService(IOptions<JwtOptions> opts) { _opts = opts.Value; }

    public string Issue(Seller seller)
    {
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, seller.Id.ToString()),
            new(JwtRegisteredClaimNames.Email, seller.Email),
            new(ClaimTypes.Name, seller.DisplayName),
            new(ClaimTypes.Role, seller.Role.ToString()),
            new("sellerKey", seller.SellerKey)
        };
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_opts.SigningKey));
        var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);
        var token = new JwtSecurityToken(
            issuer: _opts.Issuer,
            audience: _opts.Audience,
            claims: claims,
            expires: DateTime.UtcNow.AddMinutes(_opts.ExpiryMinutes),
            signingCredentials: creds);
        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}
