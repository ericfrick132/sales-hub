using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using SalesHub.Infrastructure.Options;
using SalesHub.Infrastructure.Services;

namespace SalesHub.Api.Controllers;

/// <summary>
/// Cola de tareas por audio para Claude Code (ver <see cref="ClaudeTaskService"/>). El poller de la Mac entra
/// sin login con el header X-Claude-Key (HMAC "claude-tasks" de la clave del JWT, como X-Bot-Key).
/// </summary>
[ApiController]
[Route("api/claude-tasks")]
[Authorize(Roles = "Admin")]
public class ClaudeTasksController : ControllerBase
{
    private const string KeyHeader = "X-Claude-Key";

    private readonly ClaudeTaskService _svc;
    private readonly JwtOptions _jwt;

    public ClaudeTasksController(ClaudeTaskService svc, IOptions<JwtOptions> jwt)
    {
        _svc = svc; _jwt = jwt.Value;
    }

    /// <summary>Clave del poller (se copia una vez a la Mac).</summary>
    [HttpGet("key")]
    public IActionResult Key() => Ok(new { key = PollerKey(_jwt.SigningKey) });

    /// <summary>Próxima tarea en cola (204 si no hay). Cada consulta marca la Mac como prendida.</summary>
    [HttpGet("next")]
    [AllowAnonymous]
    public async Task<IActionResult> Next(CancellationToken ct)
    {
        if (!KeyOk()) return Unauthorized();
        var task = await _svc.ClaimNextAsync(ct);
        return task is null ? NoContent() : Ok(task);
    }

    [HttpPost("{id:guid}/result")]
    [AllowAnonymous]
    public async Task<IActionResult> Result(Guid id, [FromBody] ClaudeTaskService.ResultIn body, CancellationToken ct)
    {
        if (!KeyOk()) return Unauthorized();
        return await _svc.CompleteAsync(id, body, ct) ? Ok() : NotFound();
    }

    public static string PollerKey(string jwtSigningKey) =>
        Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(jwtSigningKey), "claude-tasks"u8.ToArray()))[..32].ToLowerInvariant();

    private bool KeyOk()
    {
        var expected = Encoding.UTF8.GetBytes(PollerKey(_jwt.SigningKey));
        var given = Encoding.UTF8.GetBytes(Request.Headers[KeyHeader].ToString());
        return given.Length == expected.Length && CryptographicOperations.FixedTimeEquals(given, expected);
    }
}
