using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SalesHub.Core.Domain.Entities;
using SalesHub.Core.Domain.Enums;
using SalesHub.Infrastructure.Persistence;

namespace SalesHub.Api.Controllers;

/// <summary>
/// Tab "Pedidos web" del CRM: los que pidieron demo o que los llamen desde el formulario de las landings
/// (sección de precios o popup "¿Antes de irte?"). La web promete llamar en 10 minutos, así que la lista va
/// del pedido más nuevo al más viejo y marca los que siguen sin llamar.
/// <para>
/// El estado de llamada no se guarda aparte: sale del modo llamadas (<see cref="LeadNoteKind.Call"/>, que se
/// registra con <c>POST /api/crm/leads/{id}/calls</c>) posterior al último pedido, y de la etapa del lead
/// para "Demo agendada". Así una llamada hecha desde el Pipeline o el modo llamadas también cuenta acá.
/// </para>
/// </summary>
[ApiController]
[Route("api/crm/web-requests")]
[Authorize]
public class WebRequestsController : ControllerBase
{
    public const string Tag = "pidio-demo-web";

    /// <summary>Etiqueta de <c>CrmController.CallOutcomeLabel</c> → clave del filtro de esta vista.</summary>
    private static readonly (string Label, string Key)[] CallKeys =
    {
        ("No atendió", "no_atiende"), ("Atendió", "llamado"), ("Interesado", "interesado"),
        ("No le interesa", "no_interesa"), ("Volver a llamar", "volver"), ("Número equivocado", "equivocado"),
    };

    private readonly ApplicationDbContext _db;

    public WebRequestsController(ApplicationDbContext db) => _db = db;

    public record WebRequestRow(
        Guid Id, string Name, string ProductKey, string? ProductName, string? Phone, string? Email, string? Business,
        string? Plan, string? Page, bool FromExitPopup, DateTimeOffset RequestedAt, string CallState,
        int CallCount, DateTimeOffset? LastCallAt, string? LastCallText, string? LastCallByName,
        DateTimeOffset? NextActionAt, string? SellerName, string Status, bool Repeat);
    public record Facet(string Key, int Count);
    public record WebRequestList(int Total, List<WebRequestRow> Rows, List<Facet> Products, List<Facet> Statuses);

    /// <summary>
    /// <paramref name="status"/>: "pendiente" (sin llamar desde el pedido), "hoy" (volver a llamar vencido),
    /// "demo" o una clave de resultado (no_atiende, llamado, interesado, volver, no_interesa, equivocado).
    /// </summary>
    [HttpGet]
    public async Task<ActionResult<WebRequestList>> List(
        [FromQuery] string? productKey, [FromQuery] string? status, [FromQuery] string? q,
        [FromQuery] int limit = 200, CancellationToken ct = default)
    {
        var leads = _db.Leads.AsNoTracking()
            .Where(l => !l.Tags.Contains(SandboxController.Tag))
            .Where(l => l.Source == LeadSource.LandingForm || l.Tags.Contains(Tag));
        // Como el resto del CRM: el vendedor ve los suyos (los que puede registrar), el admin todos.
        if (!CurrentUser.IsAdmin(User))
        {
            var me = CurrentUser.Id(User);
            leads = leads.Where(l => l.SellerId == me);
        }

        // Son decenas o cientos: se traen los últimos y el estado se calcula en memoria.
        var raw = await leads
            .OrderByDescending(l => l.WebRequestAt ?? l.CreatedAt)
            .Take(2000)
            .Select(l => new
            {
                l.Id, l.Name, l.ProductKey, l.WhatsappPhone, l.RawPhone, l.Status, l.Source,
                l.WebRequestJson, l.RawDataJson, l.NextActionAt, l.CreatedAt,
                RequestedAt = l.WebRequestAt ?? l.CreatedAt,
                SellerName = l.Seller != null ? l.Seller.DisplayName : null,
            })
            .ToListAsync(ct);

        var ids = raw.Select(r => r.Id).ToList();
        var calls = await _db.LeadNotes.AsNoTracking()
            .Where(n => ids.Contains(n.LeadId) && n.Kind == LeadNoteKind.Call)
            .OrderByDescending(n => n.CreatedAt)
            .Select(n => new { n.LeadId, n.CreatedAt, n.Text, By = n.Seller != null ? n.Seller.DisplayName : null })
            .ToListAsync(ct);
        var callsByLead = calls.GroupBy(c => c.LeadId).ToDictionary(g => g.Key, g => g.ToList());
        var productNames = await _db.Products.AsNoTracking().ToDictionaryAsync(p => p.ProductKey, p => p.DisplayName, ct);

        var now = DateTimeOffset.UtcNow;
        var all = raw.Select(r =>
        {
            var req = ParseRequest(r.WebRequestJson ?? (r.Source == LeadSource.LandingForm ? r.RawDataJson : null));
            // Solo cuentan las llamadas hechas después del último pedido: si vuelve a pedir, vuelve a "Sin llamar".
            var since = callsByLead.TryGetValue(r.Id, out var list) ? list.Where(c => c.CreatedAt >= r.RequestedAt).ToList() : new();
            var last = since.FirstOrDefault();
            var state = r.Status is LeadStatus.DemoScheduled or LeadStatus.Closed ? "demo"
                : last is null ? "pendiente"
                : CallKeys.FirstOrDefault(k => last.Text.StartsWith($"Llamada: {k.Label}")).Key ?? "llamado";
            return new WebRequestRow(
                r.Id, r.Name, r.ProductKey, productNames.GetValueOrDefault(r.ProductKey),
                r.WhatsappPhone ?? r.RawPhone, req.Email, req.Business, req.Plan, req.Page,
                req.Page?.Contains("#salida") == true, r.RequestedAt, state,
                since.Count, last?.CreatedAt, last?.Text, last?.By, r.NextActionAt, r.SellerName, r.Status.ToString(),
                r.RequestedAt > r.CreatedAt.AddMinutes(1));
        }).ToList();

        bool Due(WebRequestRow x) => x.CallState == "volver" && x.NextActionAt <= now;

        var scoped = all.AsEnumerable();
        if (!string.IsNullOrWhiteSpace(productKey)) scoped = scoped.Where(x => x.ProductKey == productKey);
        if (!string.IsNullOrWhiteSpace(q))
        {
            var term = q.Trim().ToLowerInvariant();
            var digits = new string(term.Where(char.IsDigit).ToArray());
            scoped = scoped.Where(x => x.Name.ToLowerInvariant().Contains(term)
                || (x.Email?.ToLowerInvariant().Contains(term) ?? false)
                || (x.Business?.ToLowerInvariant().Contains(term) ?? false)
                || (digits.Length >= 4 && (x.Phone?.Contains(digits) ?? false)));
        }
        var scopedList = scoped.ToList();

        var statuses = scopedList.GroupBy(x => x.CallState).Select(g => new Facet(g.Key, g.Count()))
            .Append(new Facet("hoy", scopedList.Count(Due))).ToList();
        var products = all.GroupBy(x => x.ProductKey).Select(g => new Facet(g.Key, g.Count()))
            .OrderByDescending(f => f.Count).ToList();

        var filtered = status switch
        {
            null or "" => scopedList,
            "hoy" => scopedList.Where(Due).ToList(),
            _ => scopedList.Where(x => x.CallState == status).ToList(),
        };

        return new WebRequestList(filtered.Count, filtered.Take(Math.Clamp(limit, 1, 1000)).ToList(), products, statuses);
    }

    private record ParsedRequest(string? Email, string? Business, string? Plan, string? Page);

    /// <summary>El body de <see cref="PublicLandingLeadsController.LandingLeadRequest"/> serializado (PascalCase).</summary>
    private static ParsedRequest ParseRequest(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new(null, null, null, null);
        try
        {
            using var doc = JsonDocument.Parse(json);
            string? Get(string name) =>
                doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(v.GetString()) ? v.GetString()!.Trim() : null;
            return new(Get("Email"), Get("Business"), Get("Plan"), Get("Page"));
        }
        catch (JsonException)
        {
            return new(null, null, null, null);
        }
    }
}
