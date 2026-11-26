using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SalesHub.Core.Domain.Entities;
using SalesHub.Core.Domain.Enums;
using SalesHub.Infrastructure.Options;
using SalesHub.Infrastructure.Persistence;

namespace SalesHub.Infrastructure.Services;

/// <summary>
/// Etapas del embudo que se le avisan a Meta y cuándo se llega a cada una.
///
/// "qualified" = un dueño de gimnasio al que vale la pena llamar: terminó las preguntas de calificación del
/// bot (guion closer de GymHero: contó cómo cobra y cuántos alumnos tiene y el bot le ofrece la llamada;
/// guion viejo: respondió las preguntas antes del pase a la persona) o un vendedor lo pasó a mano a
/// "Interesados" en el CRM. NO alcanza con que el bot lo pase a la persona: ese pase pasa también cuando
/// pregunta el precio en el saludo o pregunta si es un bot, y le pone "Interesado" igual.
///
/// "demo_scheduled" = quedó agendada la demo: el bot la reservó en Google Calendar, en el guion closer
/// acordó la llamada (horario o "llamame ahora"), o un vendedor lo pasó a mano a "Demo agendada". Las
/// demos agendadas en la landing no: esas ya las mandan el navegador y el backend de GymHero.
/// </summary>
public static class MetaFunnel
{
    public const string Qualified = "qualified";
    public const string DemoScheduled = "demo_scheduled";

    /// <summary>Flag del Centro de control que apaga/prende los avisos (sin fila = MetaCapi:Enabled).</summary>
    public const string FlagKey = "meta-capi";

    /// <summary>Etiqueta que pone PublicDemoBookingController al agendar en la web.</summary>
    public const string WebBookedTag = "agendo-demo-web";

    public static bool IsStage(string? stage) => stage is Qualified or DemoScheduled;

    /// <summary>Pasos del guion closer que solo se alcanzan con el lead ya calificado (la llamada ofrecida en adelante).</summary>
    private static readonly HashSet<int> CloserQualifiedSteps = new()
    {
        OnboardingService.CloserLlamada, OnboardingService.CloserAgendada, OnboardingService.CloserLlamando,
        OnboardingService.CloserDatos, OnboardingService.CloserCuenta,
    };

    /// <summary>Pasos del guion closer con la llamada (la demo) acordada: con horario o "ahora".</summary>
    private static readonly HashSet<int> CloserDemoSteps = new() { OnboardingService.CloserAgendada, OnboardingService.CloserLlamando };

    /// <summary>
    /// Etapas alcanzadas en un turno del bot: lo que reporta el guion viejo (<see cref="OnboardingResult.FunnelStage"/>)
    /// más el salto de paso del guion closer (que se mira desde afuera: ese archivo no se toca para esto).
    /// </summary>
    public static List<string> FromOnboardingTurn(int? stepBefore, int? stepAfter, string? reported)
    {
        var stages = new List<string>();
        if (IsStage(reported)) stages.Add(reported!);
        if (stepAfter is { } after && after != stepBefore)
        {
            if (CloserQualifiedSteps.Contains(after) && !(stepBefore is { } b1 && CloserQualifiedSteps.Contains(b1)))
                stages.Add(Qualified);
            if (CloserDemoSteps.Contains(after) && !(stepBefore is { } b2 && CloserDemoSteps.Contains(b2)))
                stages.Add(DemoScheduled);
        }
        return stages.Distinct().ToList();
    }

    /// <summary>Cambio de etapa a mano en el CRM (tablero, modo llamadas o el PATCH del lead).</summary>
    public static List<string> FromStatusChange(LeadStatus before, LeadStatus after)
    {
        if (before == after) return new();
        return after switch
        {
            LeadStatus.Interested => new() { Qualified },
            LeadStatus.DemoScheduled => new() { DemoScheduled },
            _ => new(),
        };
    }

    /// <summary>Vino de un anuncio: click-to-WhatsApp (referral o texto pre-armado) o formulario de Meta.</summary>
    public static bool CameFromAd(Lead lead) =>
        !string.IsNullOrWhiteSpace(lead.CtwaClid) || !string.IsNullOrWhiteSpace(lead.AdId)
        || lead.Source is LeadSource.WhatsAppAd or LeadSource.MetaLeadAd;

    public static string EventIdFor(Guid leadId, string stage) => $"lead-{leadId:D}-{stage}";
}

/// <summary>
/// Manda a la Conversions API de Meta las etapas del embudo de un lead de anuncio (ver <see cref="MetaFunnel"/>),
/// una sola vez por lead y etapa, y deja cada envío en <see cref="MetaConversionEvent"/> con lo que contestó Meta.
///
/// Cómo sale cada evento:
///  - Con ctwa_clid y nombre de evento de mensajería (QualifiedLead): action_source business_messaging,
///    messaging_channel whatsapp, ctwa_clid + page_id (+ whatsapp_business_account_id si hay). Las líneas
///    son de WhatsApp Business por Evolution, no de la Cloud API, y Meta puede rechazarlo: en ese caso se
///    reintenta UNA vez como system_generated con el mismo event_id.
///  - Sin ctwa_clid (o etapa sin evento de mensajería, como Schedule): system_generated directo, con el
///    teléfono y el mail con hash SHA-256, external_id (id del lead con hash) y lead_id si es de un
///    formulario de Meta.
/// Nunca tira para arriba: lo llama <see cref="MetaConversionQueue"/> en segundo plano.
/// </summary>
public class MetaConversionService
{
    public const string SourceMessaging = "business_messaging";
    public const string SourceSystem = "system_generated";

    /// <summary>Meta rechaza el pedido entero si el event_time tiene más de 7 días: se deja un margen.</summary>
    private static readonly TimeSpan MaxEventAge = TimeSpan.FromDays(7) - TimeSpan.FromHours(1);

    private readonly ApplicationDbContext _db;
    private readonly IHttpClientFactory _http;
    private readonly MetaCapiOptions _opt;
    private readonly IConfiguration _config;
    private readonly ILogger<MetaConversionService> _log;

    public MetaConversionService(ApplicationDbContext db, IHttpClientFactory http, IOptions<MetaCapiOptions> options,
        IConfiguration config, ILogger<MetaConversionService> log)
    {
        _db = db; _http = http; _opt = options.Value; _config = config; _log = log;
    }

    public record Outcome(string Status, string? Reason, MetaConversionEvent? Row);

    /// <summary>Token con el que se manda y de dónde sale (null = no hay).</summary>
    public (string? Token, string? Source) Token()
    {
        if (!string.IsNullOrWhiteSpace(_opt.AccessToken)) return (_opt.AccessToken!.Trim(), "MetaCapi:AccessToken");
        var ads = _config["Meta:AdsAccessToken"];
        return string.IsNullOrWhiteSpace(ads) ? (null, null) : (ads.Trim(), "Meta:AdsAccessToken");
    }

    private string Version => !string.IsNullOrWhiteSpace(_opt.GraphVersion) ? _opt.GraphVersion!
        : string.IsNullOrWhiteSpace(_config["Meta:GraphVersion"]) ? "v21.0" : _config["Meta:GraphVersion"]!;

    public MetaCapiOptions.ProductSettings? ProductFor(string? productKey)
    {
        if (string.IsNullOrWhiteSpace(productKey)) return null;
        var p = _opt.Products.FirstOrDefault(kv => string.Equals(kv.Key, productKey, StringComparison.OrdinalIgnoreCase)).Value;
        return p is null || string.IsNullOrWhiteSpace(p.PixelId) ? null : p;
    }

    /// <summary>Estado del interruptor general (flag del Centro de control, o MetaCapi:Enabled si no tiene fila).</summary>
    public Task<bool> SwitchOnAsync(CancellationToken ct) => _db.IsFlagOnAsync(MetaFunnel.FlagKey, _opt.Enabled, ct);

    /// <summary>Lo que hace la cola: la etapa y, si es la demo, antes la de calificado (una demo agendada es un lead calificado).</summary>
    public async Task TrackAsync(Guid leadId, string stage, string trigger, DateTimeOffset occurredAt, CancellationToken ct)
    {
        if (!MetaFunnel.IsStage(stage) || _opt.Products.Count == 0) return;
        if (stage == MetaFunnel.DemoScheduled)
            await SendAsync(leadId, MetaFunnel.Qualified, trigger, occurredAt, testCode: null, retryRow: null, ct);
        await SendAsync(leadId, stage, trigger, occurredAt, testCode: null, retryRow: null, ct);
    }

    /// <summary>
    /// Prueba de aceptación: manda UN evento de ese lead con el test_event_code del pedido, por el mismo camino
    /// que el real (incluido el reintento) y con el mismo event_id, así Meta nunca lo cuenta dos veces. Queda en
    /// su propia fila (IsTest) y no ocupa la del envío real. Ignora el interruptor general, no el resto.
    /// </summary>
    public Task<Outcome> SendTestAsync(Guid leadId, string stage, string testEventCode, CancellationToken ct) =>
        SendAsync(leadId, stage, "test", DateTimeOffset.UtcNow, testEventCode.Trim(), retryRow: null, ct);

    /// <summary>
    /// Reintenta los envíos reales que Meta no aceptó (o que quedaron colgados) y tienen menos de 7 días: por
    /// ejemplo después de arreglar el token. Mismo event_id, así que Meta tampoco los cuenta dos veces.
    /// </summary>
    public async Task<List<Outcome>> RetryFailedAsync(int max, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var since = now - MaxEventAge;
        var stuck = now.AddMinutes(-2);
        var rows = await _db.MetaConversionEvents
            .Where(x => !x.IsTest && x.OccurredAt > since
                        && (x.Status == "failed" || (x.Status == "pending" && x.UpdatedAt < stuck)))
            .OrderBy(x => x.OccurredAt).Take(Math.Clamp(max, 1, 200)).ToListAsync(ct);
        var result = new List<Outcome>();
        foreach (var row in rows)
            result.Add(await SendAsync(row.LeadId, row.Stage, "retry", row.OccurredAt, testCode: null, retryRow: row, ct));
        return result;
    }

    private async Task<Outcome> SendAsync(Guid leadId, string stage, string trigger, DateTimeOffset occurredAt,
        string? testCode, MetaConversionEvent? retryRow, CancellationToken ct)
    {
        var isTest = testCode is not null;
        if (!MetaFunnel.IsStage(stage)) return new("skipped", $"etapa desconocida: {stage}", null);

        var lead = await _db.Leads.AsNoTracking().FirstOrDefaultAsync(l => l.Id == leadId, ct);
        if (lead is null) return new("skipped", "no existe el lead", null);
        if (lead.Tags.Contains("sandbox")) return new("skipped", "lead de prueba del simulador", null);
        if (!MetaFunnel.CameFromAd(lead)) return new("skipped", "no vino de un anuncio", null);
        if (stage == MetaFunnel.DemoScheduled && lead.Tags.Contains(MetaFunnel.WebBookedTag))
            return new("skipped", "agendó en la web: el Schedule ya lo mandan la landing y GymHero", null);
        var product = ProductFor(lead.ProductKey);
        if (product is null) return new("skipped", $"{lead.ProductKey} no tiene pixel en MetaCapi:Products", null);
        var (token, _) = Token();
        if (token is null) return new("skipped", "no hay token (Meta:AdsAccessToken ni MetaCapi:AccessToken)", null);
        if (!isTest && retryRow is null && !await SwitchOnAsync(ct)) return new("skipped", "apagado (flag meta-capi / MetaCapi:Enabled)", null);

        // ── La fila: una por lead y etapa. Si ya existe, la etapa ya se mandó (o se intentó): nunca dos veces. ──
        var row = retryRow ?? await _db.MetaConversionEvents
            .FirstOrDefaultAsync(x => x.LeadId == leadId && x.Stage == stage && x.IsTest == isTest, ct);
        if (row is not null && retryRow is null && !isTest) return new("already", $"ya estaba ({row.Status})", row);
        var now = DateTimeOffset.UtcNow;
        if (row is null)
        {
            row = new MetaConversionEvent
            {
                LeadId = leadId, ProductKey = lead.ProductKey, Stage = stage, IsTest = isTest,
                EventId = MetaFunnel.EventIdFor(leadId, stage), Status = "pending", Trigger = trigger,
                OccurredAt = occurredAt, CreatedAt = now, UpdatedAt = now,
            };
            _db.MetaConversionEvents.Add(row);
            try { await _db.SaveChangesAsync(ct); }
            catch (DbUpdateException)
            {
                // Otro proceso tomó la misma etapa al mismo tiempo: el índice único la deja en una sola.
                _db.Entry(row).State = EntityState.Detached;
                return new("already", "otro proceso ya la estaba mandando", null);
            }
        }
        if (isTest) { row.OccurredAt = now; row.Trigger = "test"; }
        else if (retryRow is not null && !(row.Trigger ?? "").EndsWith("retry"))
            row.Trigger = Clip(string.IsNullOrWhiteSpace(row.Trigger) ? "retry" : row.Trigger + "+retry", 32);
        // Una prueba repetida o un reintento no arrastran lo del intento anterior.
        row.FallbackUsed = false; row.FirstHttpStatus = null; row.FirstError = null; row.FbTraceId = null;

        if (now - row.OccurredAt > MaxEventAge)
            return await FinishAsync(row, "failed", null, "pasaron más de 7 días desde la etapa: Meta ya no lo acepta");

        // ── Con qué se identifica al lead ──
        var email = await _db.LeadOnboardings.AsNoTracking().Where(o => o.LeadId == leadId)
            .Select(o => o.Email).FirstOrDefaultAsync(ct);
        var id = new Identity(
            CtwaClid: string.IsNullOrWhiteSpace(lead.CtwaClid) ? null : lead.CtwaClid.Trim(),
            PhoneHash: NormalizePhone(PhoneOf(lead)) is { } ph ? Sha256(ph) : null,
            EmailHash: email is not null && email.Contains('@') ? Sha256(email.Trim().ToLowerInvariant()) : null,
            ExternalIdHash: Sha256(lead.Id.ToString("D")),
            LeadAdId: lead.Source == LeadSource.MetaLeadAd && long.TryParse(lead.ExternalId, out var lid) ? lid : null);

        var names = stage == MetaFunnel.Qualified ? _opt.Events.Qualified : _opt.Events.DemoScheduled;
        var systemOk = !string.IsNullOrWhiteSpace(names.System)
                       && ((id.PhoneHash ?? id.EmailHash) is not null || id.LeadAdId is not null);
        var messaging = id.CtwaClid is not null && !string.IsNullOrWhiteSpace(names.Messaging);
        if (!messaging && !systemOk)
            return await FinishAsync(row, "skipped", null, id.CtwaClid is null
                ? "sin ctwa_clid, teléfono ni mail: no hay con qué identificarlo"
                : "la etapa no tiene evento de mensajería y el lead no tiene teléfono ni mail");

        var code = testCode ?? (string.IsNullOrWhiteSpace(_opt.TestEventCode) ? null : _opt.TestEventCode!.Trim());
        var eventTime = row.OccurredAt.ToUnixTimeSeconds();
        var isLeadAd = lead.Source == LeadSource.MetaLeadAd;

        var first = messaging
            ? (Source: SourceMessaging, Name: names.Messaging.Trim())
            : (Source: SourceSystem, Name: names.System.Trim());
        var ev = BuildEvent(first.Name, first.Source, eventTime, row.EventId, id, product, isLeadAd);
        var a1 = await PostAsync(product.PixelId.Trim(), token, ev, code, ct);
        row.Attempts++;
        Apply(row, first.Source, first.Name, ev, a1);

        // business_messaging rechazado (con respuesta de Meta, no un timeout): UNA vez más como system_generated.
        if (!a1.Ok && first.Source == SourceMessaging && a1.HttpStatus is not null && systemOk)
        {
            row.FallbackUsed = true;
            row.FirstHttpStatus = a1.HttpStatus;
            row.FirstError = a1.Error;
            var ev2 = BuildEvent(names.System.Trim(), SourceSystem, eventTime, row.EventId, id, product, isLeadAd);
            var a2 = await PostAsync(product.PixelId.Trim(), token, ev2, code, ct);
            row.Attempts++;
            Apply(row, SourceSystem, names.System.Trim(), ev2, a2);
            return await FinishAsync(row, a2.Ok ? "sent" : "failed", a2.HttpStatus, a2.Error);
        }
        return await FinishAsync(row, a1.Ok ? "sent" : "failed", a1.HttpStatus, a1.Error);
    }

    private static void Apply(MetaConversionEvent row, string source, string name, Dictionary<string, object?> ev, Attempt a)
    {
        row.ActionSource = source;
        row.EventName = name;
        row.MatchKeys = ev["user_data"] is Dictionary<string, object?> ud ? string.Join(",", ud.Keys) : null;
        row.FbTraceId = a.FbTraceId;
    }

    private async Task<Outcome> FinishAsync(MetaConversionEvent row, string status, int? httpStatus, string? error)
    {
        var now = DateTimeOffset.UtcNow;
        row.Status = status;
        row.HttpStatus = httpStatus;
        row.Error = Clip(error, 500);
        if (status == "sent") row.SentAt = now;
        row.UpdatedAt = now;
        await _db.SaveChangesAsync(CancellationToken.None);
        if (status == "sent")
            _log.LogInformation("Meta CAPI: {Event} ({Source}{Test}) de lead {Lead} aceptado{Fallback}",
                row.EventName, row.ActionSource, row.IsTest ? ", prueba" : "", row.LeadId, row.FallbackUsed ? " después del reintento" : "");
        else
            _log.LogWarning("Meta CAPI: {Stage} de lead {Lead} quedó {Status} ({Http}): {Error}",
                row.Stage, row.LeadId, status, httpStatus?.ToString() ?? "sin respuesta", row.Error);
        return new(status, row.Error, row);
    }

    // ── Armado del evento ─────────────────────────────────────────────────────────────────────────

    public record Identity(string? CtwaClid, string? PhoneHash, string? EmailHash, string ExternalIdHash, long? LeadAdId);

    /// <summary>
    /// Un evento de la Conversions API. ctwa_clid, page_id y whatsapp_business_account_id solo van con
    /// business_messaging; lead_id y el custom_data de CRM solo con system_generated de un formulario de Meta.
    /// </summary>
    public static Dictionary<string, object?> BuildEvent(string eventName, string actionSource, long eventTime, string eventId,
        Identity id, MetaCapiOptions.ProductSettings product, bool isLeadAd)
    {
        var messaging = actionSource == SourceMessaging;
        var user = new Dictionary<string, object?>();
        if (messaging && id.CtwaClid is not null)
        {
            user["ctwa_clid"] = id.CtwaClid;
            if (!string.IsNullOrWhiteSpace(product.PageId)) user["page_id"] = product.PageId!.Trim();
            if (!string.IsNullOrWhiteSpace(product.WhatsAppBusinessAccountId))
                user["whatsapp_business_account_id"] = product.WhatsAppBusinessAccountId!.Trim();
        }
        if (id.PhoneHash is not null) user["ph"] = new[] { id.PhoneHash };
        if (id.EmailHash is not null) user["em"] = new[] { id.EmailHash };
        user["external_id"] = new[] { id.ExternalIdHash };
        if (!messaging && id.LeadAdId is { } leadId) user["lead_id"] = leadId;

        var ev = new Dictionary<string, object?>
        {
            ["event_name"] = eventName,
            ["event_time"] = eventTime,
            ["action_source"] = actionSource,
            ["event_id"] = eventId,
            ["user_data"] = user,
        };
        if (messaging) ev["messaging_channel"] = "whatsapp";
        if (!messaging && isLeadAd)
            ev["custom_data"] = new Dictionary<string, object?> { ["event_source"] = "crm", ["lead_event_source"] = "sales-hub" };
        return ev;
    }

    private static string? PhoneOf(Lead lead)
    {
        if (!string.IsNullOrWhiteSpace(lead.WhatsappPhone)) return lead.WhatsappPhone;
        if (lead.WhatsappJid is { } jid && jid.EndsWith("@s.whatsapp.net", StringComparison.OrdinalIgnoreCase)) return jid.Split('@')[0];
        return lead.RawPhone;
    }

    /// <summary>Solo dígitos y con código de país (Meta lo pide así). Argentina en el formato de WhatsApp (549…).</summary>
    public static string? NormalizePhone(string? phone)
    {
        if (string.IsNullOrWhiteSpace(phone)) return null;
        var digits = new string(phone.Where(char.IsDigit).ToArray()).TrimStart('0');
        if (digits.Length < 8) return null;
        if (digits.Length == 10 && !digits.StartsWith("54")) digits = "549" + digits;              // 11xxxxxxxx (AR sin código)
        else if (digits.Length == 12 && digits.StartsWith("54") && !digits.StartsWith("549")) digits = "549" + digits[2..];
        return digits;
    }

    public static string Sha256(string input) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(input))).ToLowerInvariant();

    // ── HTTP ─────────────────────────────────────────────────────────────────────────────────────

    private record Attempt(int? HttpStatus, bool Ok, string? Error, string? FbTraceId);

    private async Task<Attempt> PostAsync(string pixelId, string token, Dictionary<string, object?> ev, string? testCode, CancellationToken ct)
    {
        var payload = new Dictionary<string, object?> { ["data"] = new[] { ev } };
        if (!string.IsNullOrWhiteSpace(testCode)) payload["test_event_code"] = testCode;
        var seconds = Math.Clamp(_opt.TimeoutSeconds, 2, 30);
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, $"https://graph.facebook.com/{Version}/{Uri.EscapeDataString(pixelId)}/events")
            {
                Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"),
            };
            // El token va en el header y no en la URL: así no queda en los logs de HttpClient.
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(seconds));
            using var res = await _http.CreateClient("meta-capi").SendAsync(req, cts.Token);
            var body = await res.Content.ReadAsStringAsync(cts.Token);
            var (received, trace) = ReadSuccess(body);
            var ok = res.IsSuccessStatusCode && received is not 0;
            return new((int)res.StatusCode, ok, ok ? null : ShortError(body, token) ?? (received == 0 ? "events_received 0" : null), trace);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new(null, false, $"sin respuesta de Meta en {seconds} s", null);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            return new(null, false, Clip(Redact($"{ex.GetType().Name}: {ex.Message}", token), 300), null);
        }
    }

    private static (int? Received, string? Trace) ReadSuccess(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return (null, null);
            int? received = root.TryGetProperty("events_received", out var r) && r.ValueKind == JsonValueKind.Number ? r.GetInt32() : null;
            string? trace = root.TryGetProperty("fbtrace_id", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
            if (trace is null && root.TryGetProperty("error", out var err) && err.ValueKind == JsonValueKind.Object
                && err.TryGetProperty("fbtrace_id", out var et) && et.ValueKind == JsonValueKind.String) trace = et.GetString();
            return (received, Clip(trace, 64));
        }
        catch (JsonException) { return (null, null); }
    }

    /// <summary>De {"error":{...}} quedan el código, el subcódigo y los mensajes; cualquier otra cosa recortada. El token nunca sale.</summary>
    private static string? ShortError(string? body, string token)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;
        var text = body;
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("error", out var err)
                && err.ValueKind == JsonValueKind.Object)
            {
                var parts = new List<string>();
                foreach (var key in new[] { "code", "error_subcode" })
                    if (err.TryGetProperty(key, out var n) && n.ValueKind == JsonValueKind.Number) parts.Add($"{key} {n.GetRawText()}");
                foreach (var key in new[] { "message", "error_user_title", "error_user_msg" })
                    if (err.TryGetProperty(key, out var s) && s.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(s.GetString()))
                        parts.Add(s.GetString()!);
                if (parts.Count > 0) text = string.Join(" | ", parts);
            }
        }
        catch (JsonException) { /* no es JSON (un 502 del balanceador): va recortado */ }
        return Clip(Redact(text, token), 480);
    }

    private static string Redact(string text, string token)
    {
        if (!string.IsNullOrEmpty(token)) text = text.Replace(token, "[token]");
        text = Regex.Replace(text, @"access_token=[^&\s""']+", "access_token=[token]");
        return Regex.Replace(text, @"\s+", " ").Trim();
    }

    private static string? Clip(string? text, int max) => text is null ? null : text.Length > max ? text[..max] : text;
}
