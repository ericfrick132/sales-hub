using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using SalesHub.Core.Abstractions;
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
    private readonly IEvolutionClient _evo;
    private readonly JwtOptions _jwt;

    public ClaudeTasksController(ClaudeTaskService svc, IEvolutionClient evo, IOptions<JwtOptions> jwt)
    {
        _svc = svc; _evo = evo; _jwt = jwt.Value;
    }

    /// <summary>
    /// Crea (si hace falta) la línea privada en Evolution, sin fila en evolution_instances, y devuelve su estado
    /// y el QR para escanear con el WhatsApp personal. <paramref name="reset"/> la borra y la crea de cero (si el
    /// emparejamiento quedó trabado).
    /// </summary>
    [HttpPost("line")]
    public async Task<IActionResult> Line([FromQuery] bool reset, CancellationToken ct)
    {
        if (reset)
        {
            await _evo.DeleteInstanceAsync(ClaudeTaskService.LineName, ct);
            await Task.Delay(TimeSpan.FromSeconds(3), ct);
        }
        var status = await _evo.EnsureInstanceAsync(ClaudeTaskService.LineName, ct);
        if (status.Status == "open") return Ok(new { status = status.Status });
        return Ok(new { status = status.Status, qr = await _evo.GetQrCodeAsync(ClaudeTaskService.LineName, ct) });
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

    /// <summary>Imagen adjunta N (desde 1) de una tarea que la Mac ya tomó.</summary>
    [HttpGet("{id:guid}/attachments/{index:int}")]
    [AllowAnonymous]
    public async Task<IActionResult> Attachment(Guid id, int index, CancellationToken ct)
    {
        if (!KeyOk()) return Unauthorized();
        var a = await _svc.GetAttachmentAsync(id, index, ct);
        return a is null ? NotFound() : File(a.Data, a.MimeType);
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
