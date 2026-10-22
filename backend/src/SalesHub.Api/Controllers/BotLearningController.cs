using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SalesHub.Infrastructure.Options;
using SalesHub.Infrastructure.Persistence;
using SalesHub.Infrastructure.Services;

namespace SalesHub.Api.Controllers;

/// <summary>
/// Mejoras del bot (Dashboard → Bot): tarjetas que Eric aprueba o descarta, con rollback. Las propuestas las
/// sube la rutina /bot-review de Claude Code (sin API: corre en la Mac), que antes baja los chats del día con
/// <c>export</c>. La rutina entra sin login con el header X-Bot-Key (HMAC de la clave del JWT).
/// </summary>
[ApiController]
[Route("api/bot-learning")]
[Authorize(Roles = "Admin")]
public class BotLearningController : ControllerBase
{
    private const string KeyHeader = "X-Bot-Key";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly BotLearningService _svc;
    private readonly ApplicationDbContext _db;
    private readonly JwtOptions _jwt;

    public BotLearningController(BotLearningService svc, ApplicationDbContext db, IOptions<JwtOptions> jwt)
    {
        _svc = svc; _db = db; _jwt = jwt.Value;
    }

    public record Card(Guid Id, string Day, string ProductKey, string Kind, string Target, string Title, string Rationale,
        string? OldValue, string? NewValue, List<BotLearningService.Example> Examples, int Occurrences, string Status,
        DateTimeOffset CreatedAt, DateTimeOffset? DecidedAt, DateTimeOffset? AppliedAt, DateTimeOffset? RolledBackAt);
    public record RunRow(string Day, DateTimeOffset RanAt, int Conversations, int Proposals, string? Error);

    /// <summary>Pendientes (las tarjetas), el historial de decididas y las últimas corridas.</summary>
    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        var rows = await _db.BotProposals.AsNoTracking()
            .Where(p => p.Status == "pending" || p.CreatedAt >= DateTimeOffset.UtcNow.AddDays(-30))
            .OrderByDescending(p => p.Occurrences).ThenByDescending(p => p.CreatedAt)
            .ToListAsync(ct);
        var runs = await _db.BotLearningRuns.AsNoTracking().OrderByDescending(r => r.Day).Take(7).ToListAsync(ct);
        Card ToCard(SalesHub.Core.Domain.Entities.BotProposal p) => new(p.Id, p.Day.ToString("yyyy-MM-dd"), p.ProductKey, p.Kind,
            p.Target, p.Title, p.Rationale, p.OldValue, p.NewValue,
            string.IsNullOrWhiteSpace(p.ExamplesJson) ? new() : JsonSerializer.Deserialize<List<BotLearningService.Example>>(p.ExamplesJson, Json) ?? new(),
            p.Occurrences, p.Status, p.CreatedAt, p.DecidedAt, p.AppliedAt, p.RolledBackAt);
        return Ok(new
        {
            pending = rows.Where(p => p.Status == "pending").Select(ToCard).ToList(),
            history = rows.Where(p => p.Status != "pending").OrderByDescending(p => p.DecidedAt ?? p.CreatedAt).Select(ToCard).ToList(),
            runs = runs.Select(r => new RunRow(r.Day.ToString("yyyy-MM-dd"), r.RanAt, r.Conversations, r.Proposals, r.Error)).ToList(),
        });
    }

    [HttpPost("{id:guid}/approve")]
    public async Task<IActionResult> Approve(Guid id, CancellationToken ct) => Result(await _svc.ApproveAsync(id, CurrentUser.Id(User), ct));

    [HttpPost("{id:guid}/reject")]
    public async Task<IActionResult> Reject(Guid id, CancellationToken ct) => Result(await _svc.RejectAsync(id, CurrentUser.Id(User), ct));

    [HttpPost("{id:guid}/rollback")]
    public async Task<IActionResult> Rollback(Guid id, CancellationToken ct) => Result(await _svc.RollbackAsync(id, ct));

    /// <summary>Clave de la rutina (se copia una vez a la Mac).</summary>
    [HttpGet("key")]
    public IActionResult Key() => Ok(new { key = RoutineKey(_jwt.SigningKey) });

    /// <summary>Chats del bot de un día (por defecto ayer) para la rutina. Sin login: X-Bot-Key.</summary>
    [HttpGet("export")]
    [AllowAnonymous]
    public async Task<IActionResult> Export([FromQuery] DateOnly? day, CancellationToken ct)
    {
        if (!KeyOk()) return Unauthorized();
        return Ok(await _svc.ExportAsync(day ?? BotLearningService.TodayAr().AddDays(-1), ct));
    }

    public record ImportRequest(DateOnly Day, int Conversations, List<BotLearningService.ProposalIn> Proposals);

    /// <summary>Propuestas de la rutina → tarjetas pendientes. Sin login: X-Bot-Key.</summary>
    [HttpPost("import")]
    [AllowAnonymous]
    public async Task<IActionResult> Import([FromBody] ImportRequest req, CancellationToken ct)
    {
        if (!KeyOk()) return Unauthorized();
        if (req.Proposals.Count > 30) return BadRequest(new { error = "máximo 30 propuestas por día" });
        return Ok(await _svc.ImportAsync(req.Day, req.Conversations, req.Proposals, ct));
    }

    private IActionResult Result(BotLearningService.DecisionResult r) =>
        r.Ok ? Ok(r) : r.Status == "missing" ? NotFound(r) : Conflict(r);

    public static string RoutineKey(string jwtSigningKey) =>
        Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(jwtSigningKey), "bot-learning"u8.ToArray()))[..32].ToLowerInvariant();

    private bool KeyOk()
    {
        var expected = Encoding.UTF8.GetBytes(RoutineKey(_jwt.SigningKey));
        var given = Encoding.UTF8.GetBytes(Request.Headers[KeyHeader].ToString());
        return given.Length == expected.Length && CryptographicOperations.FixedTimeEquals(given, expected);
    }
}
