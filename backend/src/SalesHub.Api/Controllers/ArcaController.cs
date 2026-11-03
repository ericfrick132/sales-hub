using System.Globalization;
using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SalesHub.Infrastructure.Arca;
using SalesHub.Infrastructure.Persistence;

namespace SalesHub.Api.Controllers;

/// <summary>
/// Nomenclador de actividades de ARCA (CLAE F.883) y rubro de cada lead.
///   GET  api/arca/activities?q=gimnasio      nomenclador (json), con búsqueda por código o texto  [cualquier usuario]
///   GET  api/arca/activities/{code}/search   términos para buscar ese rubro en Maps + app sugerida [cualquier usuario]
///   GET  api/arca/activities.csv             nomenclador completo para Excel                     [cualquier usuario]
///   GET  api/arca/summary?product=gymhero    leads por producto y rubro
///   GET  api/arca/leads.csv?product=…&amp;code=… listado de leads con su rubro ARCA (para Excel)
///   PUT  api/arca/leads/{id}                 { code } elige el rubro a mano (null lo devuelve al automático)
///   POST api/arca/reclassify                 reclasifica ya todo lo no manual
/// El resto es solo Admin. El buscador y los términos los usa el userscript de captura de Maps
/// con la sesión del vendedor, por eso esos no piden Admin.
/// </summary>
[ApiController]
[Route("api/arca")]
[Authorize]
public class ArcaController : ControllerBase
{
    private readonly ApplicationDbContext _db;
    private readonly ILogger<ArcaController> _log;

    public ArcaController(ApplicationDbContext db, ILogger<ArcaController> log) { _db = db; _log = log; }

    [HttpGet("activities")]
    public async Task<IActionResult> Activities([FromQuery] string? q, [FromQuery] int? limit, CancellationToken ct)
    {
        var query = _db.ArcaActivities.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(q))
        {
            var term = q.Trim();
            query = term.All(char.IsDigit)
                ? query.Where(a => a.Code.StartsWith(term))
                : query.Where(a => EF.Functions.ILike(a.Description, $"%{term}%")
                                   || EF.Functions.ILike(a.GroupDescription, $"%{term}%"));
        }
        return Ok(await query.OrderBy(a => a.Code).Take(limit is > 0 and <= 1000 ? limit.Value : 1000).ToListAsync(ct));
    }

    /// <summary>Qué escribir en Google Maps para encontrar negocios de este rubro y a qué app le sirven.</summary>
    [HttpGet("activities/{code}/search")]
    public async Task<IActionResult> SearchTerms(string code, CancellationToken ct)
    {
        code = code.Trim().PadLeft(6, '0');
        var a = await _db.ArcaActivities.AsNoTracking().FirstOrDefaultAsync(x => x.Code == code, ct);
        if (a is null) return NotFound(new { error = $"El código {code} no está en el nomenclador de ARCA" });
        return Ok(new
        {
            a.Code,
            a.Description,
            a.GroupDescription,
            terms = ArcaSearchTerms.For(a.Code, a.Description),
            suggestedProduct = ArcaSearchTerms.SuggestedProduct(a.Code),
        });
    }

    [HttpGet("activities.csv")]
    public async Task<IActionResult> ActivitiesCsv(CancellationToken ct)
    {
        var rows = await _db.ArcaActivities.AsNoTracking().OrderBy(a => a.Code).ToListAsync(ct);
        var sb = new StringBuilder();
        sb.AppendLine("codigo;descripcion;grupo;grupo_descripcion;division;division_descripcion;seccion;seccion_descripcion");
        foreach (var a in rows)
            sb.AppendLine(string.Join(';', Csv(a.Code), Csv(a.Description), Csv(a.GroupCode), Csv(a.GroupDescription),
                Csv(a.DivisionCode), Csv(a.DivisionDescription), Csv(a.Section), Csv(a.SectionDescription)));
        return CsvFile(sb, "nomenclador-arca-clae-f883.csv");
    }

    [HttpGet("summary")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> Summary([FromQuery] string? product, CancellationToken ct)
    {
        var leads = _db.Leads.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(product)) leads = leads.Where(l => l.ProductKey == product);

        var rows = await leads
            .GroupBy(l => new { l.ProductKey, l.ArcaActivityCode })
            .Select(g => new { g.Key.ProductKey, Code = g.Key.ArcaActivityCode, Leads = g.Count() })
            .ToListAsync(ct);
        var names = await _db.ArcaActivities.AsNoTracking().ToDictionaryAsync(a => a.Code, a => a.Description, ct);

        return Ok(new
        {
            rulesVersion = ArcaLeadClassifier.RulesVersion,
            total = rows.Sum(r => r.Leads),
            classified = rows.Where(r => r.Code != null).Sum(r => r.Leads),
            rows = rows
                .OrderBy(r => r.ProductKey).ThenByDescending(r => r.Leads)
                .Select(r => new
                {
                    product = r.ProductKey,
                    code = r.Code,
                    activity = r.Code != null && names.TryGetValue(r.Code, out var n) ? n : null,
                    leads = r.Leads,
                }),
        });
    }

    [HttpGet("leads.csv")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> LeadsCsv([FromQuery] string? product, [FromQuery] string? code, CancellationToken ct)
    {
        var leads = _db.Leads.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(product)) leads = leads.Where(l => l.ProductKey == product);
        if (!string.IsNullOrWhiteSpace(code)) leads = leads.Where(l => l.ArcaActivityCode != null && l.ArcaActivityCode.StartsWith(code));

        var rows = await leads
            .OrderBy(l => l.ProductKey).ThenBy(l => l.ArcaActivityCode).ThenBy(l => l.Name)
            .Select(l => new
            {
                l.Id, l.ProductKey, l.Name, l.City, l.Province, l.Country, l.WhatsappPhone, l.RawPhone,
                l.InstagramHandle, l.Website, l.Status, l.Source, l.SearchCategory, l.Types,
                l.ArcaActivityCode, l.ArcaActivityRule, l.CreatedAt,
            })
            .ToListAsync(ct);
        var acts = await _db.ArcaActivities.AsNoTracking().ToDictionaryAsync(a => a.Code, ct);

        var sb = new StringBuilder();
        sb.AppendLine("id;producto;nombre;ciudad;provincia;pais;whatsapp;telefono;instagram;web;estado;origen;categoria_busqueda;tipos_google;" +
                      "codigo_arca;actividad_arca;grupo_arca;grupo_arca_descripcion;seccion_arca;seccion_arca_descripcion;regla_rubro;creado");
        foreach (var l in rows)
        {
            acts.TryGetValue(l.ArcaActivityCode ?? "", out var a);
            sb.AppendLine(string.Join(';',
                Csv(l.Id.ToString()), Csv(l.ProductKey), Csv(l.Name), Csv(l.City), Csv(l.Province), Csv(l.Country),
                Csv(l.WhatsappPhone), Csv(l.RawPhone), Csv(l.InstagramHandle), Csv(l.Website),
                Csv(l.Status.ToString()), Csv(l.Source.ToString()), Csv(l.SearchCategory), Csv(string.Join(", ", l.Types ?? new())),
                Csv(l.ArcaActivityCode), Csv(a?.Description), Csv(a?.GroupCode), Csv(a?.GroupDescription),
                Csv(a?.Section), Csv(a?.SectionDescription), Csv(l.ArcaActivityRule),
                Csv(l.CreatedAt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))));
        }
        var suffix = string.IsNullOrWhiteSpace(product) ? "efcloud" : product;
        return CsvFile(sb, $"leads-{suffix}-rubro-arca.csv");
    }

    public record SetActivityRequest(string? Code);

    [HttpPut("leads/{id:guid}")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> SetLeadActivity(Guid id, [FromBody] SetActivityRequest req, CancellationToken ct)
    {
        var lead = await _db.Leads.FirstOrDefaultAsync(l => l.Id == id, ct);
        if (lead is null) return NotFound();

        if (string.IsNullOrWhiteSpace(req.Code))
        {
            // Vuelve al automático: el worker lo reclasifica en la próxima pasada.
            lead.ArcaActivityCode = null;
            lead.ArcaActivityRule = null;
            lead.ArcaRulesVersion = null;
        }
        else
        {
            var code = req.Code.Trim().PadLeft(6, '0');
            if (!await _db.ArcaActivities.AnyAsync(a => a.Code == code, ct))
                return BadRequest(new { error = $"El código {code} no está en el nomenclador de ARCA" });
            lead.ArcaActivityCode = code;
            lead.ArcaActivityRule = ArcaLeadClassifier.ManualRule;
            lead.ArcaRulesVersion = ArcaLeadClassifier.RulesVersion;
        }
        lead.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);
        return Ok(new { lead.Id, lead.ArcaActivityCode, lead.ArcaActivityRule });
    }

    [HttpPost("reclassify")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> Reclassify(CancellationToken ct)
    {
        var count = await ArcaLeadClassifier.ClassifyPendingAsync(_db, _log, ct: ct);
        return Ok(new { classified = count, rulesVersion = ArcaLeadClassifier.RulesVersion });
    }

    private static string Csv(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        var v = value.Replace("\r", " ").Replace("\n", " ");
        return v.IndexOfAny(new[] { ';', '"' }) >= 0 ? $"\"{v.Replace("\"", "\"\"")}\"" : v;
    }

    // BOM para que Excel abra bien los acentos.
    private FileContentResult CsvFile(StringBuilder sb, string fileName) =>
        File(Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(sb.ToString())).ToArray(), "text/csv; charset=utf-8", fileName);
}
