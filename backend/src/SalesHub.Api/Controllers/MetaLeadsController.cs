using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SalesHub.Core.Abstractions;
using SalesHub.Core.Domain.Enums;
using SalesHub.Infrastructure.Persistence;

namespace SalesHub.Api.Controllers;

/// <summary>
/// Ingesta de Meta Lead Ads (formularios instantáneos). Meta pega a este webhook por cada lead;
/// bajamos los datos del formulario por la Graph API, creamos el Lead en el embudo (reusando el
/// mismo ILeadIngestService que el Hub: dedup por producto+teléfono, asigna vendedor y ENCOLA el
/// primer WhatsApp por la cola humanizada) y guardamos las respuestas de calificación + la campaña.
/// Cuando el lead responde, lo toma el ConversationAgentService como cualquier otro.
/// Config (todo opcional, fail-safe si falta): Meta:VerifyToken, Meta:PageAccessToken,
/// Meta:AppSecret, Meta:GraphVersion (def v21.0), Meta:DefaultProductKey, Meta:FormProductMap (form_id→productKey).
/// </summary>
[ApiController]
[Route("api/webhooks/meta-leads")]
[AllowAnonymous]
public class MetaLeadsController : ControllerBase
{
    private readonly ILeadIngestService _ingest;
    private readonly IPhoneNormalizer _phones;
    private readonly ApplicationDbContext _db;
    private readonly IConfiguration _config;
    private readonly IHttpClientFactory _http;
    private readonly ILogger<MetaLeadsController> _log;

    public MetaLeadsController(
        ILeadIngestService ingest, IPhoneNormalizer phones, ApplicationDbContext db, IConfiguration config,
        IHttpClientFactory http, ILogger<MetaLeadsController> log)
    {
        _ingest = ingest; _phones = phones; _db = db; _config = config; _http = http; _log = log;
    }

    /// <summary>Verificación del webhook (Meta hace un GET con hub.challenge al suscribir).</summary>
    [HttpGet]
    public IActionResult Verify()
    {
        var mode = Request.Query["hub.mode"].ToString();
        var token = Request.Query["hub.verify_token"].ToString();
        var challenge = Request.Query["hub.challenge"].ToString();
        var expected = _config["Meta:VerifyToken"];

        if (mode == "subscribe" && !string.IsNullOrEmpty(expected) && token == expected)
            return Content(challenge, "text/plain");

        _log.LogWarning("Meta webhook verify rechazado (mode={Mode})", mode);
        return StatusCode(403);
    }

    /// <summary>Evento leadgen: por cada lead bajamos sus datos y lo ingerimos. Siempre 200 (Meta reintenta si no).</summary>
    [HttpPost]
    public async Task<IActionResult> Receive(CancellationToken ct)
    {
        string body;
        using (var reader = new StreamReader(Request.Body, Encoding.UTF8))
            body = await reader.ReadToEndAsync(ct);

        // Firma opcional: si hay AppSecret, validamos X-Hub-Signature-256. Si no matchea, no procesamos
        // (pero devolvemos 200 para no gatillar la tormenta de reintentos de Meta).
        var appSecret = _config["Meta:AppSecret"];
        if (!string.IsNullOrEmpty(appSecret) && !ValidSignature(body, appSecret))
        {
            _log.LogWarning("Meta webhook: firma X-Hub-Signature-256 inválida — ignorado");
            return Ok();
        }

        JsonDocument doc;
        try { doc = JsonDocument.Parse(body); }
        catch (Exception ex) { _log.LogWarning(ex, "Meta webhook: body no es JSON"); return Ok(); }

        using (doc)
        {
            if (!doc.RootElement.TryGetProperty("entry", out var entries) || entries.ValueKind != JsonValueKind.Array)
                return Ok();

            foreach (var entry in entries.EnumerateArray())
            {
                if (!entry.TryGetProperty("changes", out var changes) || changes.ValueKind != JsonValueKind.Array)
                    continue;

                foreach (var change in changes.EnumerateArray())
                {
                    if (GetStr(change, "field") != "leadgen") continue;
                    if (!change.TryGetProperty("value", out var value)) continue;

                    var leadgenId = GetStr(value, "leadgen_id");
                    if (string.IsNullOrEmpty(leadgenId)) continue;

                    var formId = GetStr(value, "form_id");
                    var campaign = GetStr(value, "campaign_name") ?? GetStr(value, "campaign_id") ?? GetStr(value, "ad_id");
                    var adId = GetStr(value, "ad_id");

                    try { await ProcessLeadAsync(leadgenId, formId, campaign, adId, ct); }
                    catch (Exception ex) { _log.LogError(ex, "Meta lead {LeadId}: falló el procesamiento", leadgenId); }
                }
            }
        }

        return Ok();
    }

    private async Task ProcessLeadAsync(string leadgenId, string? formId, string? campaign, string? adId, CancellationToken ct)
    {
        var token = _config["Meta:PageAccessToken"];
        if (string.IsNullOrEmpty(token))
        {
            _log.LogWarning("Meta:PageAccessToken no configurado — no puedo bajar el lead {LeadId}", leadgenId);
            return;
        }

        var version = string.IsNullOrWhiteSpace(_config["Meta:GraphVersion"]) ? "v21.0" : _config["Meta:GraphVersion"];
        var url = $"https://graph.facebook.com/{version}/{Uri.EscapeDataString(leadgenId)}"
                + $"?fields=field_data,campaign_name,ad_id,form_id,created_time&access_token={Uri.EscapeDataString(token!)}";

        var client = _http.CreateClient();
        var resp = await client.GetAsync(url, ct);
        var json = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
        {
            _log.LogWarning("Graph API {Status} bajando lead {LeadId}: {Body}", resp.StatusCode, leadgenId, json);
            return;
        }

        using var lead = JsonDocument.Parse(json);
        var root = lead.RootElement;
        campaign ??= GetStr(root, "campaign_name") ?? GetStr(root, "ad_id");
        adId ??= GetStr(root, "ad_id");

        // field_data: [{ name, values: [..] }]. Armamos un mapa name→primer valor.
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (root.TryGetProperty("field_data", out var fd) && fd.ValueKind == JsonValueKind.Array)
        {
            foreach (var f in fd.EnumerateArray())
            {
                var fname = GetStr(f, "name");
                if (string.IsNullOrEmpty(fname)) continue;
                if (f.TryGetProperty("values", out var vals) && vals.ValueKind == JsonValueKind.Array && vals.GetArrayLength() > 0)
                    fields[fname!] = vals[0].GetString() ?? "";
            }
        }

        var phoneRaw = FirstOf(fields, "phone_number", "phone", "telefono", "teléfono", "celular", "whatsapp");
        // Estricto primero (arregla 54 duplicado, 0/15, 9 doble); si no da, cae al naive
        // de siempre y el sweep /leads/repair-phones lo agarra después con CheckNumbers.
        var phone = _phones.NormalizeArStrict(phoneRaw) ?? NormalizePhone(phoneRaw);
        if (phone.Length < 8)
        {
            _log.LogWarning("Meta lead {LeadId}: sin teléfono válido — descartado", leadgenId);
            return;
        }

        var name = FirstOf(fields, "full_name", "name", "nombre")
                   ?? string.Join(' ', new[] { FirstOf(fields, "first_name"), FirstOf(fields, "last_name") }
                          .Where(s => !string.IsNullOrWhiteSpace(s)));
        if (string.IsNullOrWhiteSpace(name)) name = null;
        var email = FirstOf(fields, "email", "mail", "correo");

        var productKey = ResolveProduct(formId);
        if (productKey is null)
        {
            _log.LogWarning("Meta lead {LeadId} form {Form}: sin productKey mapeado (Meta:FormProductMap / DefaultProductKey) — descartado",
                leadgenId, formId);
            return;
        }

        var result = await _ingest.IngestAsync(new LeadIngestRequest(
            productKey, LeadSource.MetaLeadAd, leadgenId, name, null, phone, email), ct);

        if (result.Outcome == LeadIngestOutcome.Created && result.LeadId is Guid id)
        {
            // Guardamos las respuestas de calificación + la campaña en el lead (contexto para el agente
            // y para scorearlo). El opener ya lo encoló IngestAsync por la cola humanizada.
            var row = await _db.Leads.FirstOrDefaultAsync(l => l.Id == id, ct);
            if (row is not null)
            {
                var qa = fields.Where(kv => !IsContactField(kv.Key))
                               .Select(kv => $"{kv.Key}: {kv.Value}").ToList();
                var notes = new StringBuilder("Lead Ad de Meta.");
                if (!string.IsNullOrWhiteSpace(campaign)) notes.Append($" Campaña: {campaign}.");
                if (qa.Count > 0) notes.Append(" Respuestas — ").Append(string.Join("; ", qa)).Append('.');
                row.Notes = notes.ToString();
                // Id del anuncio del form: viaja al tenant en el bot-register y a Meta CAPI cuando paga.
                if (!string.IsNullOrWhiteSpace(adId)) { row.AdId = adId; row.AdTitle ??= campaign; }
                if (!string.IsNullOrWhiteSpace(campaign)) row.SearchCategory = campaign;
                row.RawDataJson = json;
                await _db.SaveChangesAsync(ct);
            }
        }

        _log.LogInformation("Meta lead {LeadId} → {Product} ({Outcome}); campaña={Campaign}",
            leadgenId, productKey, result.Outcome, campaign ?? "-");
    }

    private string? ResolveProduct(string? formId)
    {
        if (!string.IsNullOrEmpty(formId))
        {
            var map = _config.GetSection("Meta:FormProductMap").Get<Dictionary<string, string>>();
            if (map is not null && map.TryGetValue(formId, out var pk) && !string.IsNullOrWhiteSpace(pk))
                return pk;
        }
        var def = _config["Meta:DefaultProductKey"];
        return string.IsNullOrWhiteSpace(def) ? null : def;
    }

    private bool ValidSignature(string body, string appSecret)
    {
        if (!Request.Headers.TryGetValue("X-Hub-Signature-256", out var header)) return false;
        var sig = header.ToString();
        const string prefix = "sha256=";
        if (!sig.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;

        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(appSecret));
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(body));
        var computed = Convert.ToHexString(hash).ToLowerInvariant();
        var provided = sig[prefix.Length..].ToLowerInvariant();
        var a = Encoding.UTF8.GetBytes(computed);
        var b = Encoding.UTF8.GetBytes(provided);
        return a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b);
    }

    private static bool IsContactField(string name)
    {
        var n = name.ToLowerInvariant();
        return n is "phone_number" or "phone" or "telefono" or "teléfono" or "celular" or "whatsapp"
            or "full_name" or "name" or "nombre" or "first_name" or "last_name"
            or "email" or "mail" or "correo";
    }

    private static string? FirstOf(Dictionary<string, string> fields, params string[] keys)
    {
        foreach (var k in keys)
            if (fields.TryGetValue(k, out var v) && !string.IsNullOrWhiteSpace(v)) return v;
        return null;
    }

    private static string? GetStr(JsonElement el, string prop)
        => el.ValueKind == JsonValueKind.Object && el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;

    /// <summary>Normaliza un teléfono argentino al formato de Evolution (549 + área + número).</summary>
    private static string NormalizePhone(string? phone)
    {
        if (string.IsNullOrWhiteSpace(phone)) return string.Empty;
        var digits = Regex.Replace(phone, @"[^0-9]", "");
        if (digits.Length == 0) return digits;
        if (digits.StartsWith("549")) return digits;
        if (digits.StartsWith("54") && digits.Length >= 11) return "549" + digits.Substring(2);
        return "549" + digits;
    }
}
