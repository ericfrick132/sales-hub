using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SalesHub.Api.Dtos;
using SalesHub.Core.Abstractions;
using SalesHub.Core.Domain.Entities;
using SalesHub.Core.Domain.Enums;
using SalesHub.Infrastructure.Persistence;
using SalesHub.Infrastructure.Services;

namespace SalesHub.Api.Controllers;

/// <summary>
/// CRUD de los teléfonos que se vinculan escaneando un QR (instancias de Evolution SIN
/// vendedor). Agregar uno crea la instancia y devuelve el QR para escanear; cada teléfono
/// queda con su nombre, las apps que atiende y el candado de solo escucha. Las líneas de
/// vendedor se manejan desde su vendedor y no aparecen acá.
/// </summary>
[ApiController]
[Route("api/phone-lines")]
[Authorize]
public class PhoneLinesController : ControllerBase
{
    private readonly ApplicationDbContext _db;
    private readonly IEvolutionClient _evo;
    private readonly ListenOnlyLines _listenOnly;
    private readonly LeadEntryService _entries;
    private readonly ILogger<PhoneLinesController> _log;

    public PhoneLinesController(ApplicationDbContext db, IEvolutionClient evo, ListenOnlyLines listenOnly,
        LeadEntryService entries, ILogger<PhoneLinesController> log)
    {
        _db = db; _evo = evo; _listenOnly = listenOnly; _entries = entries; _log = log;
    }

    public record PhoneLineDto(
        Guid Id, string InstanceName, string Label, string? Phone, string Status, bool ListenOnly,
        string? ProductKey, List<string> ExtraProductKeys, DateTimeOffset? ConnectedAt,
        DateTimeOffset? DisconnectedAt, DateTimeOffset CreatedAt, int LeadsToday, int Leads7d,
        bool ImportHistory, int HistoryImportPasses, DateTimeOffset? HistoryImportStartedAt,
        DateTimeOffset? HistoryImportedAt, int HistoryImportedMessages);

    /// <param name="ImportHistory">Cargar también los chats de antes de escanear (por defecto sí).</param>
    public record SavePhoneLineRequest(string Label, string ProductKey, List<string>? ExtraProductKeys,
        bool ListenOnly = true, bool ImportHistory = true);

    [HttpGet]
    public async Task<ActionResult<List<PhoneLineDto>>> List(CancellationToken ct)
    {
        if (!CurrentUser.IsAdmin(User)) return Forbid();
        var lines = await _db.EvolutionInstances.AsNoTracking()
            .Where(i => i.SellerId == null)
            .OrderBy(i => i.CreatedAt)
            .ToListAsync(ct);

        var today = LeadEntryService.TodayAr();
        var entries = await _entries.GetEntriesAsync(today.AddDays(-6), today, ct);
        return lines.Select(i => ToDto(i, entries)).ToList();
    }

    [HttpPost]
    public async Task<ActionResult<PhoneLineDto>> Create([FromBody] SavePhoneLineRequest req, CancellationToken ct)
    {
        if (!CurrentUser.IsAdmin(User)) return Forbid();
        var label = (req.Label ?? "").Trim();
        if (label.Length == 0) return BadRequest(new { error = "Poné un nombre para reconocer el teléfono." });
        var apps = await ValidAppsAsync(req, ct);
        if (apps.Error is not null) return BadRequest(new { error = apps.Error });

        var line = new EvolutionInstance
        {
            Id = Guid.NewGuid(),
            Label = label.Length > 80 ? label[..80] : label,
            InstanceName = await UniqueInstanceNameAsync(label, ct),
            ProductKey = apps.Main,
            ExtraProductKeys = apps.Extra,
            ListenOnly = req.ListenOnly,
            ImportHistory = req.ImportHistory,
        };
        _db.EvolutionInstances.Add(line);
        await _db.SaveChangesAsync(ct);

        try
        {
            await _evo.EnsureInstanceAsync(line.InstanceName, ct);
        }
        catch (Exception ex)
        {
            // Sin la instancia en Evolution el teléfono no se puede escanear: no dejamos una
            // fila que parezca registrada y no sirva.
            _log.LogWarning(ex, "No se pudo crear la instancia {Name} en Evolution", line.InstanceName);
            _db.EvolutionInstances.Remove(line);
            await _db.SaveChangesAsync(ct);
            return StatusCode(502, new { error = "WhatsApp (Evolution) no respondió al crear la línea. Probá de nuevo en un rato." });
        }

        _listenOnly.Invalidate();
        return ToDto(line, new List<LeadEntryService.Entry>());
    }

    [HttpPut("{id:guid}")]
    public async Task<ActionResult<PhoneLineDto>> Update(Guid id, [FromBody] SavePhoneLineRequest req, CancellationToken ct)
    {
        if (!CurrentUser.IsAdmin(User)) return Forbid();
        var line = await _db.EvolutionInstances.FirstOrDefaultAsync(i => i.Id == id && i.SellerId == null, ct);
        if (line is null) return NotFound(new { error = "No existe ese teléfono." });

        var label = (req.Label ?? "").Trim();
        if (label.Length == 0) return BadRequest(new { error = "Poné un nombre para reconocer el teléfono." });
        var apps = await ValidAppsAsync(req, ct);
        if (apps.Error is not null) return BadRequest(new { error = apps.Error });

        line.Label = label.Length > 80 ? label[..80] : label;
        line.ProductKey = apps.Main;
        line.ExtraProductKeys = apps.Extra;
        line.ListenOnly = req.ListenOnly;
        // Prenderlo en un teléfono que ya estaba vinculado lo carga ahora (repasar no duplica).
        if (req.ImportHistory && !line.ImportHistory) line.HistoryImportPasses = 0;
        line.ImportHistory = req.ImportHistory;
        line.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);
        _listenOnly.Invalidate();

        var today = LeadEntryService.TodayAr();
        return ToDto(line, await _entries.GetEntriesAsync(today.AddDays(-6), today, ct));
    }

    /// <summary>
    /// QR para vincular el teléfono (WhatsApp → Dispositivos vinculados). El modal lo pide cada
    /// 15 s: cada llamada refresca el estado y, apenas conecta, guarda el número vinculado.
    /// </summary>
    [HttpGet("{id:guid}/qr")]
    public async Task<ActionResult<QrCodeResponse>> Qr(Guid id, CancellationToken ct)
    {
        if (!CurrentUser.IsAdmin(User)) return Forbid();
        var line = await _db.EvolutionInstances.FirstOrDefaultAsync(i => i.Id == id && i.SellerId == null, ct);
        if (line is null) return NotFound(new { error = "No existe ese teléfono." });

        await _evo.EnsureInstanceAsync(line.InstanceName, ct);
        var info = await _evo.GetInstanceStatusAsync(line.InstanceName, ct);
        var connected = info.Status is "open" or "connected";
        var qr = connected ? null : await _evo.GetQrCodeAsync(line.InstanceName, ct);

        var now = DateTimeOffset.UtcNow;
        line.LastQrCodeBase64 = qr;
        line.QrCodeGeneratedAt = qr is null ? line.QrCodeGeneratedAt : now;
        line.LastStatusCheckAt = now;
        line.UpdatedAt = now;
        line.Status = info.Status switch
        {
            "open" or "connected" => InstanceStatus.Connected,
            "connecting" or "qr" => InstanceStatus.Connecting,
            "close" or "disconnected" or "not_found" => InstanceStatus.Disconnected,
            _ => InstanceStatus.Unknown
        };
        if (connected)
        {
            line.ConnectedAt ??= now;
            // connectionState no trae el número; fetchInstances sí (el dueño de la sesión).
            var owners = await _evo.GetInstanceOwnersAsync(ct);
            if (owners.TryGetValue(line.InstanceName, out var owner) && !string.IsNullOrWhiteSpace(owner))
                line.ConnectedPhoneNumber = owner;
        }
        await _db.SaveChangesAsync(ct);
        return new QrCodeResponse(qr, info.Status);
    }

    /// <summary>Cierra la sesión de WhatsApp del teléfono. Queda registrado: se reconecta con otro QR.</summary>
    [HttpPost("{id:guid}/logout")]
    public async Task<IActionResult> Logout(Guid id, CancellationToken ct)
    {
        if (!CurrentUser.IsAdmin(User)) return Forbid();
        var line = await _db.EvolutionInstances.FirstOrDefaultAsync(i => i.Id == id && i.SellerId == null, ct);
        if (line is null) return NotFound(new { error = "No existe ese teléfono." });

        await _evo.LogoutInstanceAsync(line.InstanceName, ct);
        line.Status = InstanceStatus.Disconnected;
        line.DisconnectedAt = DateTimeOffset.UtcNow;
        line.ConnectedAt = null;
        // Si después se escanea otro celu, su historial se carga de nuevo.
        line.HistoryImportPasses = 0;
        line.LastQrCodeBase64 = null;
        line.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }

    /// <summary>
    /// Borra el teléfono: sesión, instancia en Evolution y registro. Los chats y leads que
    /// entraron por él se quedan (en el gráfico figura como "(borrada)").
    /// </summary>
    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        if (!CurrentUser.IsAdmin(User)) return Forbid();
        var line = await _db.EvolutionInstances.FirstOrDefaultAsync(i => i.Id == id && i.SellerId == null, ct);
        if (line is null) return NotFound(new { error = "No existe ese teléfono." });

        await _evo.DeleteInstanceAsync(line.InstanceName, ct);
        _db.EvolutionInstances.Remove(line);
        await _db.SaveChangesAsync(ct);
        _listenOnly.Invalidate();
        return NoContent();
    }

    private static PhoneLineDto ToDto(EvolutionInstance i, List<LeadEntryService.Entry> entries)
    {
        var today = LeadEntryService.TodayAr();
        var mine = entries.Where(e => string.Equals(e.Line, i.InstanceName, StringComparison.OrdinalIgnoreCase)).ToList();
        return new PhoneLineDto(
            i.Id, i.InstanceName, LeadEntryService.DisplayName(i), i.ConnectedPhoneNumber, i.Status.ToString(),
            i.ListenOnly, i.ProductKey, i.ExtraProductKeys, i.ConnectedAt, i.DisconnectedAt, i.CreatedAt,
            mine.Count(e => e.Day == today), mine.Count,
            i.ImportHistory, i.HistoryImportPasses, i.HistoryImportStartedAt, i.HistoryImportedAt, i.HistoryImportedMessages);
    }

    /// <summary>
    /// La app principal es obligatoria: cuando escribe un número desconocido, el lead se crea con
    /// la app de la línea; sin ninguna, ese chat se descarta y el teléfono no contaría nada.
    /// </summary>
    private async Task<(string Main, List<string> Extra, string? Error)> ValidAppsAsync(SavePhoneLineRequest req, CancellationToken ct)
    {
        var main = (req.ProductKey ?? "").Trim().ToLowerInvariant();
        if (main.Length == 0) return ("", new(), "Elegí qué app atiende el teléfono.");
        var wanted = (req.ExtraProductKeys ?? new List<string>())
            .Select(k => (k ?? "").Trim().ToLowerInvariant())
            .Where(k => k.Length > 0 && k != main)
            .Distinct()
            .ToList();
        var all = wanted.Append(main).ToList();
        var valid = await _db.Products.AsNoTracking()
            .Where(p => all.Contains(p.ProductKey))
            .Select(p => p.ProductKey)
            .ToListAsync(ct);
        if (!valid.Contains(main)) return ("", new(), "Esa app no existe.");
        return (main, wanted.Where(valid.Contains).ToList(), null);
    }

    private static readonly Regex NonSlug = new("[^a-z0-9]+", RegexOptions.Compiled);

    /// <summary>"Celu de Rosario" → "tel_celu-de-rosario" (y -2, -3… si ya existe).</summary>
    private async Task<string> UniqueInstanceNameAsync(string label, CancellationToken ct)
    {
        var plain = new string(label.Normalize(NormalizationForm.FormD)
            .Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
            .ToArray()).ToLowerInvariant();
        var slug = NonSlug.Replace(plain, "-").Trim('-');
        if (slug.Length > 40) slug = slug[..40].Trim('-');
        if (slug.Length == 0) slug = "telefono";

        var baseName = $"tel_{slug}";
        var taken = await _db.EvolutionInstances.AsNoTracking()
            .Where(i => i.InstanceName.StartsWith(baseName))
            .Select(i => i.InstanceName)
            .ToListAsync(ct);
        var set = new HashSet<string>(taken, StringComparer.OrdinalIgnoreCase);
        if (!set.Contains(baseName)) return baseName;
        for (var n = 2; ; n++)
            if (!set.Contains($"{baseName}-{n}")) return $"{baseName}-{n}";
    }
}
