using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SalesHub.Api.Dtos;
using SalesHub.Api.Services;
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
    private readonly ConversationService _conversations;
    private readonly WaReaderClient _reader;
    private readonly ILogger<PhoneLinesController> _log;

    public PhoneLinesController(ApplicationDbContext db, IEvolutionClient evo, ListenOnlyLines listenOnly,
        LeadEntryService entries, ConversationService conversations, WaReaderClient reader,
        ILogger<PhoneLinesController> log)
    {
        _db = db; _evo = evo; _listenOnly = listenOnly; _entries = entries; _conversations = conversations;
        _reader = reader; _log = log;
    }

    public record PhoneLineDto(
        Guid Id, string InstanceName, string Label, string? Phone, string Status, bool ListenOnly,
        string? ProductKey, List<string> ExtraProductKeys, DateTimeOffset? ConnectedAt,
        DateTimeOffset? DisconnectedAt, DateTimeOffset CreatedAt, int LeadsToday, int Leads7d,
        bool ImportHistory, int HistoryImportPasses, DateTimeOffset? HistoryImportStartedAt,
        DateTimeOffset? HistoryImportedAt, int HistoryImportedMessages,
        List<Guid> ProspectSellerIds, List<string> ProspectSkipWords, int Prospects,
        int HistoryTotalChats, int HistoryDoneChats);

    /// <param name="ImportHistory">Cargar también los chats de antes de escanear (por defecto sí).</param>
    /// <param name="ProspectSellerIds">Vendedores que se reparten los contactos del historial como
    /// prospectos para llamar. Vacío = el historial entra a Conversaciones y nada más.</param>
    /// <param name="ProspectSkipWords">Palabras que dejan un chat afuera (delivery, bancos, etc.).</param>
    public record SavePhoneLineRequest(string Label, string ProductKey, List<string>? ExtraProductKeys,
        bool ListenOnly = true, bool ImportHistory = true,
        List<Guid>? ProspectSellerIds = null, List<string>? ProspectSkipWords = null);

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
            ProspectSellerIds = await ValidSellersAsync(req.ProspectSellerIds, ct),
            ProspectSkipWords = CleanWords(req.ProspectSkipWords),
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
        var owners = await ValidSellersAsync(req.ProspectSellerIds, ct);
        // Sumar vendedores (o prender los prospectos) en un teléfono ya cargado: se repasa el
        // historial para que los contactos que todavía no son lead entren ahora.
        if (owners.Count > 0 && !owners.SequenceEqual(line.ProspectSellerIds)) line.HistoryImportPasses = 0;
        line.ProspectSellerIds = owners;
        line.ProspectSkipWords = CleanWords(req.ProspectSkipWords);
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

    /// <summary>
    /// Vuelve a recorrer los chats del teléfono AHORA sin tener que re-escanear el QR:
    /// deja las pasadas en cero y el worker lo agarra en el próximo minuto. Es idempotente —
    /// los números y mensajes que ya están no se duplican — así que sirve para forzar la carga
    /// cuando la sesión estuvo inestable o se acaba de elegir quién toma los prospectos.
    /// </summary>
    [HttpPost("{id:guid}/import-now")]
    public async Task<IActionResult> ImportNow(Guid id, CancellationToken ct)
    {
        if (!CurrentUser.IsAdmin(User)) return Forbid();
        var line = await _db.EvolutionInstances.FirstOrDefaultAsync(i => i.Id == id && i.SellerId == null, ct);
        if (line is null) return NotFound(new { error = "No existe ese teléfono." });
        if (line.ConnectedAt is null) return BadRequest(new { error = "Escaneá el QR primero: todavía no se vinculó ningún celu." });

        line.ImportHistory = true;
        line.HistoryImportPasses = 0;
        line.HistoryImportStartedAt = null;
        line.HistoryImportTotalChats = 0;
        line.HistoryImportDoneChats = 0;
        line.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);
        return Accepted(new { ok = true });
    }

    public record ChatListChat(string Phone, string? Name, DateTimeOffset? LastMessageAt);
    public record ChatListRequest(List<ChatListChat> Chats);

    /// <summary>
    /// Carga los chats que leyó <c>tools/wa-chatlist</c> (un cliente de WhatsApp aparte, con una
    /// baileys nueva) como prospectos de remarketing de este teléfono. Existe porque la Evolution
    /// del droplet corre baileys 7.0.0-rc.9: al sincronizar el historial tira el número real y
    /// deja sólo el LID, así que de ahí salen chats que nadie puede llamar.
    /// Mismas reglas que la importación normal: reparto entre los vendedores del teléfono, bot
    /// muteado y sin cadencia. Idempotente: un número que ya es lead no se toca.
    /// De los chats entra el número, el nombre de la agenda y la fecha del último mensaje:
    /// NADA del contenido. El filtro de palabras lo aplica el lector, que es el único que ve
    /// los textos, y de paso nunca salen de ahí.
    /// </summary>
    [HttpPost("{id:guid}/chatlist")]
    [AllowAnonymous]
    public async Task<IActionResult> ChatList(Guid id, [FromBody] ChatListRequest req, CancellationToken ct)
    {
        // Lo llama el lector (servicio interno, con su clave) o un admin logueado.
        if (!_reader.IsReader(Request) && !CurrentUser.IsAdmin(User)) return Forbid();
        var line = await _db.EvolutionInstances.FirstOrDefaultAsync(i => i.Id == id && i.SellerId == null, ct);
        if (line is null) return NotFound(new { error = "No existe ese teléfono." });
        if (line.ProspectSellerIds.Count == 0)
            return BadRequest(new { error = "Elegí primero quién toma los prospectos de este teléfono." });

        var words = line.ProspectSkipWords.Select(Norm).Where(w => w.Length > 0).ToList();
        var own = line.ConnectedPhoneNumber is null ? null : Suffix(line.ConnectedPhoneNumber);
        int created = 0, already = 0, filtered = 0;

        foreach (var chat in req.Chats ?? new List<ChatListChat>())
        {
            ct.ThrowIfCancellationRequested();
            var phone = new string((chat.Phone ?? "").Where(char.IsDigit).ToArray());
            if (phone.Length < 8) continue;
            if (own is not null && Suffix(phone) == own) continue;

            // Segunda pasada del filtro por si el nombre lo delata (el lector ya filtró por texto).
            if (words.Count > 0 && words.Any(w => Norm(chat.Name).Contains(w, StringComparison.Ordinal)))
            {
                filtered++;
                continue;
            }

            // adoptExisting: los que ya eran leads también entran a remarketing (decisión de Eric
            // 23/09/2026), guardándose el origen viejo como etiqueta.
            var outcome = await _conversations.EnsureProspectAsync(
                line.InstanceName, $"{phone}@s.whatsapp.net", chat.Name,
                line.ProspectSellerIds, chat.LastMessageAt, ct, adoptExisting: true);
            if (outcome == ConversationService.ProspectOutcome.Created) created++;
            else if (outcome == ConversationService.ProspectOutcome.Adopted) already++;
        }

        if (created > 0)
        {
            line.HistoryImportProspects += created;
            line.UpdatedAt = DateTimeOffset.UtcNow;
            await _db.SaveChangesAsync(ct);
        }
        _log.LogInformation("Chatlist de {Line}: {Created} prospectos nuevos, {Already} leads existentes pasados a remarketing, {Filtered} filtrados",
            line.InstanceName, created, already, filtered);
        return Ok(new { created, alreadyLeads = already, filteredByWords = filtered });
    }

    /// <summary>Últimos 8 dígitos: la parte estable del número en todos los formatos.</summary>
    private static string? Suffix(string? phone)
    {
        var d = new string((phone ?? "").TakeWhile(c => c != '@').Where(char.IsDigit).ToArray());
        return d.Length >= 8 ? d[^8..] : d.Length > 0 ? d : null;
    }

    /// <summary>Minúsculas y sin tildes, para comparar como se escribe de verdad.</summary>
    private static string Norm(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        var d = s.Trim().ToLowerInvariant().Normalize(NormalizationForm.FormD);
        return new string(d.Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark).ToArray());
    }

    /// <summary>
    /// Arranca la lectura de los chats de un celular: devuelve el QR para escanear. Lo hace el
    /// servicio <c>wa-reader</c> (Node + Baileys nueva), porque la Evolution de producción
    /// descarta el número real de los chats direccionados por LID.
    /// </summary>
    [HttpPost("{id:guid}/wa-read")]
    public async Task<IActionResult> StartWaRead(Guid id, CancellationToken ct)
    {
        if (!CurrentUser.IsAdmin(User)) return Forbid();
        var line = await _db.EvolutionInstances.AsNoTracking().FirstOrDefaultAsync(i => i.Id == id && i.SellerId == null, ct);
        if (line is null) return NotFound(new { error = "No existe ese teléfono." });
        if (line.ProspectSellerIds.Count == 0)
            return BadRequest(new { error = "Elegí primero quién toma los prospectos de este teléfono." });
        // El filtro de palabras viaja al lector: es el único que ve los textos de los chats.
        return await _reader.ProxyAsync(HttpMethod.Post, $"sessions/{id}", ct,
            new { skipWords = line.ProspectSkipWords });
    }

    /// <summary>Cómo va la lectura: QR pendiente, sincronizando, o el resultado de la carga.</summary>
    [HttpGet("{id:guid}/wa-read")]
    public async Task<IActionResult> WaReadStatus(Guid id, CancellationToken ct)
    {
        if (!CurrentUser.IsAdmin(User)) return Forbid();
        return await _reader.ProxyAsync(HttpMethod.Get, $"sessions/{id}", ct);
    }

    /// <summary>Corta la lectura y desvincula el dispositivo (también se usa al terminar).</summary>
    [HttpDelete("{id:guid}/wa-read")]
    public async Task<IActionResult> StopWaRead(Guid id, CancellationToken ct)
    {
        if (!CurrentUser.IsAdmin(User)) return Forbid();
        return await _reader.ProxyAsync(HttpMethod.Delete, $"sessions/{id}", ct);
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
        line.HistoryImportTotalChats = 0;
        line.HistoryImportDoneChats = 0;
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
            i.ImportHistory, i.HistoryImportPasses, i.HistoryImportStartedAt, i.HistoryImportedAt, i.HistoryImportedMessages,
            i.ProspectSellerIds, i.ProspectSkipWords, i.HistoryImportProspects,
            i.HistoryImportTotalChats, i.HistoryImportDoneChats);
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

    /// <summary>Los vendedores que existen y están activos, en el orden en que llegaron.</summary>
    private async Task<List<Guid>> ValidSellersAsync(List<Guid>? ids, CancellationToken ct)
    {
        if (ids is not { Count: > 0 }) return new List<Guid>();
        var wanted = ids.Distinct().ToList();
        var valid = await _db.Sellers.AsNoTracking()
            .Where(s => wanted.Contains(s.Id) && s.IsActive)
            .Select(s => s.Id)
            .ToListAsync(ct);
        return wanted.Where(valid.Contains).ToList();
    }

    private static List<string> CleanWords(List<string>? words) =>
        (words ?? new List<string>())
            .Select(w => (w ?? "").Trim())
            .Where(w => w.Length > 1)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(100)
            .ToList();

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
