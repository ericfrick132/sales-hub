using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SalesHub.Api.Auth;
using SalesHub.Api.Dtos;
using SalesHub.Core.Domain.Entities;
using SalesHub.Core.Domain.Enums;
using SalesHub.Infrastructure.Options;
using SalesHub.Infrastructure.Persistence;

namespace SalesHub.Api.Controllers;

[ApiController]
[Route("api/auth")]
public class AuthController : ControllerBase
{
    private readonly ApplicationDbContext _db;
    private readonly IJwtService _jwt;
    private readonly GoogleOptions _google;

    public AuthController(ApplicationDbContext db, IJwtService jwt, IOptions<GoogleOptions> google)
    {
        _db = db; _jwt = jwt; _google = google.Value;
    }

    [HttpPost("login")]
    public async Task<ActionResult<AuthResponse>> Login([FromBody] LoginRequest req, CancellationToken ct)
    {
        var seller = await _db.Sellers.FirstOrDefaultAsync(s => s.Email.ToLower() == req.Email.ToLowerInvariant(), ct);
        if (seller is null || !seller.IsActive) return Unauthorized();
        if (string.IsNullOrEmpty(seller.PasswordHash) || !BCrypt.Net.BCrypt.Verify(req.Password, seller.PasswordHash))
            return Unauthorized();

        seller.LastLoginAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);
        return new AuthResponse(_jwt.Issue(seller), seller.Id, seller.SellerKey, seller.DisplayName,
            seller.Email, seller.Role.ToString(), seller.VerticalsWhitelist);
    }

    [HttpPost("google")]
    public async Task<ActionResult<AuthResponse>> GoogleLogin([FromBody] GoogleLoginRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_google.OAuthClientId))
            return BadRequest(new { error = "Google OAuth not configured" });

        var payload = await Google.Apis.Auth.GoogleJsonWebSignature.ValidateAsync(req.IdToken,
            new Google.Apis.Auth.GoogleJsonWebSignature.ValidationSettings
            {
                Audience = new[] { _google.OAuthClientId }
            });

        var seller = await _db.Sellers.FirstOrDefaultAsync(s => s.Email.ToLower() == payload.Email.ToLowerInvariant(), ct);
        if (seller is null || !seller.IsActive) return Unauthorized(new { error = "Email no autorizado" });

        seller.GoogleSubject = payload.Subject;
        seller.LastLoginAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);

        return new AuthResponse(_jwt.Issue(seller), seller.Id, seller.SellerKey, seller.DisplayName,
            seller.Email, seller.Role.ToString(), seller.VerticalsWhitelist);
    }

    [HttpGet("me")]
    [Authorize]
    public async Task<ActionResult<AuthResponse>> Me(CancellationToken ct)
    {
        var id = CurrentUser.Id(User);
        var seller = await _db.Sellers.FirstOrDefaultAsync(s => s.Id == id, ct);
        if (seller is null) return NotFound();
        return new AuthResponse("", seller.Id, seller.SellerKey, seller.DisplayName, seller.Email,
            seller.Role.ToString(), seller.VerticalsWhitelist);
    }
}

public static class CurrentUser
{
    public static Guid Id(System.Security.Claims.ClaimsPrincipal user)
    {
        var sub = user.FindFirst(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub)?.Value
               ?? user.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        return Guid.TryParse(sub, out var id) ? id : Guid.Empty;
    }

    public static bool IsAdmin(System.Security.Claims.ClaimsPrincipal user)
        => user.IsInRole(SellerRole.Admin.ToString());
}
