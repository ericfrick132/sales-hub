using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SalesHub.Core.Abstractions;
using SalesHub.Core.Domain.Entities;
using SalesHub.Core.Domain.Enums;
using SalesHub.Infrastructure.Persistence;
using SalesHub.Infrastructure.Services;

namespace SalesHub.Workers;

/// <summary>
/// Re-engagement centralizado: cada N minutos trae los leads a re-contactar desde los otros
/// productos propios (TurnosPro/GymHero) por su endpoint de export (auth X-Api-Key), los crea
/// como leads (Source=ProductReengage), los mete en el MISMO runner de outreach, y los marca
/// en el origen para no traerlos dos veces. Gateado por el flag 'reengage'.
/// Config: LeadImport:Sources[] = { ProductKey, BaseUrl, ApiKey }.
/// </summary>
public class LeadImportWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly IConfiguration _config;
    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<LeadImportWorker> _log;

    public LeadImportWorker(IServiceScopeFactory scopes, IConfiguration config, IHttpClientFactory httpFactory, ILogger<LeadImportWorker> log)
    {
        _scopes = scopes; _config = config; _httpFactory = httpFactory; _log = log;
    }

    private record SourceCfg(string ProductKey, string BaseUrl, string ApiKey);
    // ExternalId puede venir como string (Guid en TurnosPro) o number (int en GymHero):
    // lo tomamos como JsonElement y lo normalizamos a string con .ToString().
    private record ExportLead(JsonElement ExternalId, string? Name, string? BusinessName, string? Phone, string? Email, string? Status);

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var intervalMin = _config.GetValue<int?>("LeadImport:IntervalMinutes") ?? 30;
        _log.LogInformation("LeadImportWorker started (cada {Min} min, gate flag 'reengage')", intervalMin);
        await Task.Delay(TimeSpan.FromMinutes(1), ct);
        while (!ct.IsCancellationRequested)
        {
            try { await TickAsync(ct); }
            catch (Exception ex) { _log.LogError(ex, "LeadImportWorker tick failed"); }
            await Task.Delay(TimeSpan.FromMinutes(intervalMin), ct);
        }
    }

    private List<SourceCfg> ReadSources()
    {
        var list = new List<SourceCfg>();
        foreach (var c in _config.GetSection("LeadImport:Sources").GetChildren())
        {
            var pk = c["ProductKey"]; var url = c["BaseUrl"]; var key = c["ApiKey"];
            if (!string.IsNullOrWhiteSpace(pk) && !string.IsNullOrWhiteSpace(url) && !string.IsNullOrWhiteSpace(key))
                list.Add(new SourceCfg(pk!, url!.TrimEnd('/'), key!));
        }
        return list;
    }

    private async Task TickAsync(CancellationToken ct)
    {
        using (var gate = _scopes.CreateScope())
        {
            var gdb = gate.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            if (!await gdb.IsFlagOnAsync("reengage", _config.GetValue<bool>("LeadImport:AutoStart", true), ct)) return;
        }

        var sources = ReadSources();
        if (sources.Count == 0) { _log.LogInformation("LeadImport: sin sources configurados (LeadImport:Sources)"); return; }

        var http = _httpFactory.CreateClient();
        http.Timeout = TimeSpan.FromSeconds(30);
        foreach (var src in sources)
        {
            try { await ImportSourceAsync(src, http, ct); }
            catch (Exception ex) { _log.LogError(ex, "LeadImport: falló source {Pk}", src.ProductKey); }
        }
    }

    private async Task ImportSourceAsync(SourceCfg src, HttpClient http, CancellationToken ct)
    {
        var getReq = new HttpRequestMessage(HttpMethod.Get, $"{src.BaseUrl}?limit=200");
        getReq.Headers.Add("X-Api-Key", src.ApiKey);
        var resp = await http.SendAsync(getReq, ct);
        if (!resp.IsSuccessStatusCode) { _log.LogWarning("LeadImport {Pk}: export {Code}", src.ProductKey, (int)resp.StatusCode); return; }
        var leads = await resp.Content.ReadFromJsonAsync<List<ExportLead>>(cancellationToken: ct) ?? new();
        if (leads.Count == 0) return;

        using var scope = _scopes.CreateScope();
        var ingest = scope.ServiceProvider.GetRequiredService<ILeadIngestService>();

        var toMark = new List<string>();
        var created = 0;
        foreach (var l in leads)
        {
            var extId = l.ExternalId.ToString();
            var result = await ingest.IngestAsync(new LeadIngestRequest(
                src.ProductKey, LeadSource.ProductReengage, extId, l.Name, l.BusinessName, l.Phone, l.Email), ct);

            if (result.Outcome == LeadIngestOutcome.NoProduct)
            {
                _log.LogWarning("LeadImport {Pk}: producto inexistente en SalesHub", src.ProductKey);
                return; // no tiene sentido seguir con este source
            }

            // Created / Duplicate / NoPhone → ya está resuelto en origen: marcamos para no re-traerlo.
            toMark.Add(extId);
            if (result.Outcome == LeadIngestOutcome.Created) created++;
        }

        foreach (var id in toMark)
        {
            try
            {
                var markReq = new HttpRequestMessage(HttpMethod.Post, $"{src.BaseUrl}/{id}/mark");
                markReq.Headers.Add("X-Api-Key", src.ApiKey);
                await http.SendAsync(markReq, ct);
            }
            catch (Exception ex) { _log.LogWarning(ex, "LeadImport {Pk}: no pude marcar {Id}", src.ProductKey, id); }
        }
        _log.LogInformation("LeadImport {Pk}: {Created} leads nuevos, {Marked} marcados", src.ProductKey, created, toMark.Count);
    }
}
