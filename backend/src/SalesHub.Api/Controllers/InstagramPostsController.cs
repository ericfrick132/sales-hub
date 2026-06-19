ACCIÓN: crear archivo nuevo

using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SalesHub.Infrastructure.Instagram;
using SalesHub.Infrastructure.Persistence;

namespace SalesHub.Api.Controllers;

[ApiController]
[Route("instagram-accounts/{accountId}/post")]
public class InstagramPostsController : ControllerBase
{
    private readonly ApplicationDbContext _db;
    private readonly InstagramEncryptionService _crypto;
    private readonly IOptions<InstagramOptions> _opts;
    private readonly ILogger<InstagramPostsController> _log;

    public InstagramPostsController(
        ApplicationDbContext db,
        InstagramEncryptionService crypto,
        IOptions<InstagramOptions> opts,
        ILogger<InstagramPostsController> log)
    {
        _db = db;
        _crypto = crypto;
        _opts = opts;
        _log = log;
    }

    public record PostImageRequest(string ImageUrl, string Caption);

    [HttpPost]
    public async Task<IActionResult> PostImage(
        string accountId,
        [FromBody] PostImageRequest req,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.ImageUrl))
            return BadRequest(new { error = "ImageUrl es requerido" });

        var account = await _db.InstagramAccounts
            .FirstOrDefaultAsync(a => a.Id == accountId, ct);

        if (account is null)
            return NotFound(new { error = "Cuenta no encontrada" });

        if (!account.IsLoggedIn)
            return BadRequest(new { error = "La cuenta no está logueada" });

        if (account.IsActionBlocked)
            return BadRequest(new { error = "La cuenta está bloqueada" });

        await using var client = new InstagramClient(_db, _crypto, _opts.Value, _log);
        await client.InitializeAsync(account, ct);

        var ok = await client.PostImageAsync(req.ImageUrl, req.Caption ?? "", ct);

        if (!ok)
            return StatusCode(500, new { error = "No se pudo publicar el post" });

        return Ok(new { message = "Post publicado correctamente" });
    }
}
