using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SalesHub.Core.Domain.Entities;
using SalesHub.Infrastructure.Options;
using SalesHub.Infrastructure.Persistence;
using SalesHub.Infrastructure.Services;

namespace SalesHub.Api.Controllers;

/// <summary>
/// Avisos a la Conversions API de Meta (lead de anuncio calificado / demo agendada, ver <see cref="MetaFunnel"/>):
/// qué se mandó y qué contestó Meta, una prueba suelta con test_event_code y el reintento de los rechazados.
/// Sin login con el header X-Bot-Key (la misma clave de la rutina /bot-review), o con sesión de admin.
/// </summary>
[ApiController]
[Route("api/meta-capi")]
[AllowAnonymous]
public class MetaCapiController : ControllerBase
{
    private const string KeyHeader = "X-Bot-Key";

    private readonly ApplicationDbContext _db;
    private readonly MetaConversionService _svc;
    private readonly MetaCapiOptions _opt;
    private readonly JwtOptions _jwt;

    public MetaCapiController(ApplicationDbContext db, MetaConversionService svc, IOptions<MetaCapiOptions> opt, IOptions<JwtOptions> jwt)
    {
        _db = db; _svc = svc; _opt = opt.Value; _jwt = jwt.Value;
    }

    public record EventRow(Guid Id, Guid LeadId, string? LeadName, string ProductKey, string Stage, bool IsTest, string EventName,
        string EventId, string? ActionSource, string Status, int? HttpStatus, string? Error, bool FallbackUsed, int? FirstHttpStatus,
        string? FirstError, string? MatchKeys, string? FbTraceId, string? Trigger, int Attempts, DateTimeOffset OccurredAt,
        DateTimeOffset? SentAt, DateTimeOffset UpdatedAt);

    /// <summary>Configuración efectiva (sin el token) y los últimos envíos, del más nuevo al más viejo.</summary>
    [HttpGet("events")]
    public async Task<IActionResult> Events([FromQuery] int limit = 50, [FromQuery] Guid? leadId = null,
        [FromQuery] string? status = null, CancellationToken ct = default)
    {
        if (!Allowed()) return Unauthorized();
        var q = _db.MetaConversionEvents.AsNoTracking().AsQueryable();
        if (leadId is not null) q = q.Where(x => x.LeadId == leadId);
        if (!string.IsNullOrWhiteSpace(status)) q = q.Where(x => x.Status == status);
        var rows = await q.OrderByDescending(x => x.UpdatedAt).Take(Math.Clamp(limit, 1, 500)).ToListAsync(ct);
        var ids = rows.Select(r => r.LeadId).Distinct().ToList();
        var names = await _db.Leads.AsNoTracking().Where(l => ids.Contains(l.Id))
            .Select(l => new { l.Id, l.Name }).ToDictionaryAsync(l => l.Id, l => l.Name, ct);

        var (token, tokenSource) = _svc.Token();
        var totals = await _db.MetaConversionEvents.AsNoTracking().Where(x => !x.IsTest)
            .GroupBy(x => new { x.Stage, x.Status }).Select(g => new { g.Key.Stage, g.Key.Status, Count = g.Count() })
            .ToListAsync(ct);
        return Ok(new
        {
            config = new
            {
                switchOn = await _svc.SwitchOnAsync(ct),
                tokenSource = token is null ? null : tokenSource,
                globalTestEventCode = !string.IsNullOrWhiteSpace(_opt.TestEventCode),
                events = new { qualified = _opt.Events.Qualified, demoScheduled = _opt.Events.DemoScheduled },
                products = _opt.Products.Where(p => !string.IsNullOrWhiteSpace(p.Value.PixelId))
                    .Select(p => new { productKey = p.Key, pixelId = p.Value.PixelId, pageId = p.Value.PageId,
                        whatsAppBusinessAccountId = p.Value.WhatsAppBusinessAccountId }),
            },
            totals,
            events = rows.Select(r => ToRow(r, names.GetValueOrDefault(r.LeadId))),
        });
    }

    public record TestRequest(Guid LeadId, string? Stage, string? TestEventCode);

    /// <summary>
    /// Manda UN evento de ese lead con el test_event_code (Administrador de eventos → Probar eventos), por el
    /// mismo camino que el envío real (business_messaging y, si Meta lo rechaza, system_generated). Usa el
    /// mismo event_id que el real, así nunca cuenta doble; no ocupa la fila del envío real.
    /// </summary>
    [HttpPost("test")]
    public async Task<IActionResult> Test([FromBody] TestRequest req, CancellationToken ct)
    {
        if (!Allowed()) return Unauthorized();
        if (string.IsNullOrWhiteSpace(req.TestEventCode)) return BadRequest(new { error = "falta testEventCode (Administrador de eventos → Probar eventos)" });
        var stage = string.IsNullOrWhiteSpace(req.Stage) ? MetaFunnel.Qualified : req.Stage.Trim().ToLowerInvariant();
        if (!MetaFunnel.IsStage(stage)) return BadRequest(new { error = $"stage tiene que ser {MetaFunnel.Qualified} o {MetaFunnel.DemoScheduled}" });
        var r = await _svc.SendTestAsync(req.LeadId, stage, req.TestEventCode, ct);
        var name = await _db.Leads.AsNoTracking().Where(l => l.Id == req.LeadId).Select(l => l.Name).FirstOrDefaultAsync(ct);
        return Ok(new { status = r.Status, reason = r.Reason, @event = r.Row is null ? null : ToRow(r.Row, name) });
    }

    /// <summary>Reintenta los envíos reales que Meta no aceptó (o quedaron colgados) de los últimos 7 días, mismo event_id.</summary>
    [HttpPost("retry")]
    public async Task<IActionResult> Retry([FromQuery] int max = 50, CancellationToken ct = default)
    {
        if (!Allowed()) return Unauthorized();
        var results = await _svc.RetryFailedAsync(max, ct);
        return Ok(new
        {
            retried = results.Count,
            sent = results.Count(r => r.Status == "sent"),
            results = results.Select(r => new { status = r.Status, reason = r.Reason, leadId = r.Row?.LeadId, stage = r.Row?.Stage }),
        });
    }

    private static EventRow ToRow(MetaConversionEvent r, string? leadName) => new(r.Id, r.LeadId, leadName, r.ProductKey, r.Stage,
        r.IsTest, r.EventName, r.EventId, r.ActionSource, r.Status, r.HttpStatus, r.Error, r.FallbackUsed, r.FirstHttpStatus,
        r.FirstError, r.MatchKeys, r.FbTraceId, r.Trigger, r.Attempts, r.OccurredAt, r.SentAt, r.UpdatedAt);

    private bool Allowed()
    {
        if (User.IsInRole("Admin")) return true;
        var expected = Encoding.UTF8.GetBytes(BotLearningController.RoutineKey(_jwt.SigningKey));
        var given = Encoding.UTF8.GetBytes(Request.Headers[KeyHeader].ToString());
        return given.Length == expected.Length && CryptographicOperations.FixedTimeEquals(given, expected);
    }
}
