using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SalesHub.Core.Abstractions;
using SalesHub.Core.Domain.Entities.Social;
using SalesHub.Infrastructure.Persistence;

namespace SalesHub.Infrastructure.Services.Social;

/// <summary>
/// Intake maestro por WhatsApp: el número MAESTRO (config en <c>inspiration_settings</c>)
/// manda contenido a la línea configurada y el bot lo rutea a INSPIRACIÓN (módulo
/// Posteos) o al PDF de requerimientos (batch de transcripción). Convive con el relay
/// de transcripción en la misma línea: este corre PRIMERO pero sólo para el maestro,
/// y deja pasar los audios sueltos (siguen yendo a transcripción clásica).
///
/// Dos formas de uso:
///  - SESIÓN primero: mandás "insp" o "pdf" → por 30 min todo va directo a ese modo
///    sin preguntas ("listo" cierra; en inspiración, al cerrar pregunta el tema).
///  - CONTENIDO primero: mandás una imagen/idea sin sesión → el bot pregunta
///    "¿Qué hago con esto? 1. Inspiración 2. PDF" y tu número rutea lo acumulado.
///
/// Las inspiraciones quedan <see cref="InspirationItem.PendingTopic"/> hasta que
/// contestás el tema (menú numerado de temas existentes, o texto = tema nuevo).
///
/// Loop-guard: nuestras respuestas llegan como fromMe cuando el maestro es la propia
/// línea (self-chat); todas arrancan con <see cref="BotPrefix"/> y esos textos se ignoran.
/// </summary>
public class InspirationIntakeRelay
{
    /// <summary>Prefijo de TODA respuesta del bot — corta el loop en self-chat.</summary>
    private const string BotPrefix = "💡";

    /// <summary>
    /// Cuánto vive el contenido sin rutear / las inspiraciones pendientes de tema (la "sesión"
    /// del intake). Pasado esto se purga. El menú se muestra por debounce al dejar de escribir,
    /// así que 5 min alcanzan para juntar la ráfaga y contestar.
    /// </summary>
    private static readonly TimeSpan PendingWindow = TimeSpan.FromMinutes(5);

    /// <summary>Duración (sliding) de una sesión de modo abierta con "insp"/"pdf".</summary>
    private static readonly TimeSpan SessionTtl = TimeSpan.FromMinutes(30);

    /// <summary>Máximo de bytes de imagen que guardamos (WhatsApp comprime a ~2-3 MB igual).</summary>
    private const int MaxImageBytes = 15 * 1024 * 1024;

    private readonly ApplicationDbContext _db;
    private readonly IEvolutionClient _evo;
    private readonly TranscriptionBatchAccumulator _batch;
    private readonly ILogger<InspirationIntakeRelay> _log;

    private static readonly Regex NonDigit = new(@"\D", RegexOptions.Compiled);
    private static readonly Regex UrlRegex = new(@"https?://\S+", RegexOptions.Compiled);

    // ── Estado en memoria (single-user; mismo patrón que el relay de transcripción) ──

    // Dedup de message IDs por si Evolution reentrega el webhook.
    private static readonly ConcurrentDictionary<string, DateTimeOffset> Recent = new();
    private static readonly TimeSpan RecentTtl = TimeSpan.FromMinutes(10);

    // Cooldown del menú de TEMA (respuesta puntual, no naguea): evita doble pregunta en ráfaga.
    private static DateTimeOffset _lastTopicAskAt = DateTimeOffset.MinValue;
    private static readonly TimeSpan AskCooldown = TimeSpan.FromMinutes(2);

    // El menú de RUTEO ("¿qué hago con esto?") NO se manda al toque: se DEBOUNCEA. Mientras el
    // maestro tira contenido seguido se re-arma el timer; cuando deja de escribir MenuDebounce,
    // el timer manda el menú UNA vez (out-of-band, con IEvolutionClient de un scope nuevo). Así
    // no naguea en medio de la ráfaga y aparece recién cuando parás.
    private static readonly TimeSpan MenuDebounce = TimeSpan.FromSeconds(15);
    private static System.Threading.Timer? _routeMenuTimer;
    private static readonly object MenuTimerLock = new();
    private static string? _menuInstance;
    private static string? _menuReplyJid;
    private static IServiceScopeFactory? _scopeFactory;

    private const string RouteMenuText =
        "¿Qué hago con esto? Contestá:\n" +
        "1. 💡 Inspiración (posteos)\n" +
        "2. 📄 PDF de requerimientos\n" +
        "3. 📅 Tema del día (tiñe los posteos de hoy de todas las apps)\n" +
        "Tip: mandá \"insp\" o \"pdf\" ANTES la próxima y no te pregunto. \"cancelar\" descarta.";

    // Menú numerado de temas que mandamos con la última pregunta: "2" se resuelve
    // contra este snapshot (si el server reinició en el medio, re-mandamos fresco).
    private static volatile IReadOnlyList<MenuOption>? _lastMenu;
    private sealed record MenuOption(string Topic, string? ProductKey);

    // Sesión de modo abierta con "insp"/"pdf" (sliding TTL).
    private enum IntakeMode { Inspiration, Pdf }
    private sealed record Session(IntakeMode Mode, DateTimeOffset Until);
    private static volatile Session? _session;

    // Contenido que llegó SIN sesión y espera la respuesta "1/2" para rutearse.
    private sealed record UnroutedItem(IntakeKind Kind, string? Caption, string? Mime, string? Text, string RawJson, DateTimeOffset At);
    private static readonly object UnroutedLock = new();
    private static readonly List<UnroutedItem> Unrouted = new();
    private const int UnroutedCap = 30;

    private sealed record ConfigSnapshot(bool Enabled, string? InstanceName, string? MasterSuffix);
    private sealed record CacheEntry(ConfigSnapshot Snap, DateTimeOffset Until);
    private static volatile CacheEntry? _cache;
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(15);

    public InspirationIntakeRelay(ApplicationDbContext db, IEvolutionClient evo, TranscriptionBatchAccumulator batch,
        IServiceScopeFactory scopeFactory, ILogger<InspirationIntakeRelay> log)
    {
        _db = db; _evo = evo; _batch = batch; _log = log;
        // El relay es Scoped (uno por request) pero el estado del debounce es estático; el
        // scope factory es singleton, así que lo capturamos una vez para el timer out-of-band.
        _scopeFactory ??= scopeFactory;
    }

    /// <summary>Fuerza recargar la config en el próximo mensaje (llamar al guardar cambios).</summary>
    public static void InvalidateCache() => _cache = null;

    /// <summary>
    /// true si el mensaje era del número maestro en la línea configurada y lo procesamos
    /// (el webhook NO debe pasarlo al flujo normal ni al relay de transcripción).
    /// false = sigue el flujo normal (incluye los audios sueltos del maestro).
    /// </summary>
    public async Task<bool> TryHandleAsync(ConversationService.IncomingMessage incoming, CancellationToken ct)
    {
        var cfg = await GetConfigAsync(ct);
        if (!cfg.Enabled || cfg.InstanceName is null || cfg.MasterSuffix is null) return false;
        if (!string.Equals(incoming.InstanceName, cfg.InstanceName, StringComparison.OrdinalIgnoreCase))
            return false;

        // Solo el número maestro. En self-chat (el maestro ES la línea) los mensajes
        // vienen fromMe con remoteJid = el propio número, así que el suffix matchea igual.
        var phone = NormalizeDigits(incoming.FromPhone ?? ExtractPhone(incoming.FromJid));
        if (phone is null || phone.Length < 6) return false;
        if (Suffix(phone) != cfg.MasterSuffix) return false;

        // fromMe (saliente de la línea) SOLO vale en self-chat real (el maestro ES la línea:
        // owner == remoteJid == maestro). Cualquier otro fromMe hacia el chat del maestro es
        // tráfico de apps/bots (notificaciones TurnosPro, respuestas nuestras, etc.) → NO es
        // input del usuario y lo dejamos pasar al flujo normal (que ya lo ignora).
        if (incoming.FromMe)
        {
            var owner = NormalizeDigits(ExtractPhone(incoming.SenderJid));
            if (owner is null || Suffix(owner) != cfg.MasterSuffix) return false;
        }

        var (kind, caption, mime) = Classify(incoming.RawJson);
        if (kind is null) return false; // sticker/video/etc: no es nuestro

        // Loop-guard: nuestra propia respuesta en self-chat llega como texto fromMe con el prefijo.
        if (kind == IntakeKind.Text && incoming.Text.TrimStart().StartsWith(BotPrefix, StringComparison.Ordinal))
            return true; // es nuestro eco: interceptar y no hacer nada

        var session = CurrentSession();

        // Audio: sólo lo agarramos dentro de una sesión PDF; suelto sigue a transcripción.
        if (kind == IntakeKind.Audio)
        {
            if (session?.Mode != IntakeMode.Pdf) return false;
            if (IsDuplicate(incoming)) return true;
            RenewSession(session);
            _batch.Enqueue(incoming.InstanceName, phone, new BatchItem(BatchItemKind.Audio, null, incoming.RawJson), await BatchWindowAsync(ct));
            return true;
        }

        if (IsDuplicate(incoming)) return true;

        return kind switch
        {
            IntakeKind.Image => await HandleImageAsync(incoming, phone, session, caption, mime, ct),
            IntakeKind.Text => await HandleTextAsync(incoming, phone, session, ct),
            _ => false,
        };
    }

    // ── Imagen ───────────────────────────────────────────────────────────────
    private async Task<bool> HandleImageAsync(ConversationService.IncomingMessage incoming, string phone, Session? session, string? caption, string? mime, CancellationToken ct)
    {
        if (session?.Mode == IntakeMode.Pdf)
        {
            RenewSession(session);
            _batch.Enqueue(incoming.InstanceName, phone, new BatchItem(BatchItemKind.Image, caption, incoming.RawJson), await BatchWindowAsync(ct));
            return true;
        }

        if (session?.Mode == IntakeMode.Inspiration)
        {
            RenewSession(session);
            await SaveImageInspirationAsync(incoming, caption, mime, ct); // sin preguntar: el tema va al cerrar con "listo"
            return true;
        }

        // Sin sesión pero ya esperando el TEMA (ráfaga post-ruteo): se suma en silencio.
        if (await HasPendingTopicAsync(ct))
        {
            await SaveImageInspirationAsync(incoming, caption, mime, ct);
            return true;
        }

        // Sin contexto: a la cola sin rutear + preguntar qué hacer.
        AddUnrouted(new UnroutedItem(IntakeKind.Image, caption, mime, null, incoming.RawJson, DateTimeOffset.UtcNow));
        ScheduleRouteMenu(incoming.InstanceName, incoming.FromJid);
        return true;
    }

    // ── Texto ────────────────────────────────────────────────────────────────
    private async Task<bool> HandleTextAsync(ConversationService.IncomingMessage incoming, string phone, Session? session, CancellationToken ct)
    {
        var text = incoming.Text.Trim();
        if (text.Length == 0) return true;
        var lower = text.ToLowerInvariant();

        // "tema ..." / "tema del día ..." → fija el tema del día (tiñe los posteos de hoy).
        // "tema off" / "tema clear" lo borra.
        if (lower is "tema" or "tema del día" or "tema del dia"
            || lower.StartsWith("tema ", StringComparison.Ordinal)
            || lower.StartsWith("tema:", StringComparison.Ordinal)
            || lower.StartsWith("tema del día ", StringComparison.Ordinal)
            || lower.StartsWith("tema del dia ", StringComparison.Ordinal))
        {
            await HandleDailyThemeAsync(incoming, text, ct);
            return true;
        }

        // Dentro de una sesión, el texto es contenido (salvo "listo" que la cierra).
        if (session is not null)
        {
            if (lower is "listo" or "cerrar" or "salir")
            {
                _session = null;
                if (session.Mode == IntakeMode.Inspiration && await HasPendingTopicAsync(ct))
                {
                    _lastTopicAskAt = DateTimeOffset.MinValue; // forzar la pregunta
                    await AskTopicIfDueAsync(incoming, ct);
                }
                else
                {
                    await ReplyAsync(incoming, session.Mode == IntakeMode.Pdf
                        ? "cerré el modo PDF. El PDF sale solo tras unos segundos de inactividad 📄."
                        : "cerré el modo inspiración.", ct);
                }
                return true;
            }

            RenewSession(session);
            if (session.Mode == IntakeMode.Pdf)
            {
                _batch.Enqueue(incoming.InstanceName, phone, new BatchItem(BatchItemKind.Text, text, null), await BatchWindowAsync(ct));
                return true;
            }
            await SaveTextInspirationAsync(text, ct); // idea de texto, tema al cerrar
            return true;
        }

        // ¿Estamos esperando el TEMA de inspiraciones pendientes?
        if (await HasPendingTopicAsync(ct))
        {
            await HandleTopicAnswerAsync(incoming, text, ct);
            return true;
        }

        // ¿Hay contenido sin rutear esperando el "1/2"?
        if (HasUnrouted())
        {
            await HandleRouteAnswerAsync(incoming, phone, text, lower, ct);
            return true;
        }

        // Comandos para abrir sesión de modo.
        if (lower is "insp" or "inspiracion" or "inspiración" or "ideas")
        {
            _session = new Session(IntakeMode.Inspiration, DateTimeOffset.UtcNow + SessionTtl);
            await ReplyAsync(incoming,
                "modo INSPIRACIÓN abierto (30 min): todo lo que mandes lo guardo directo. " +
                "Mandá \"listo\" al terminar y te pregunto el tema.", ct);
            return true;
        }
        if (lower is "pdf" or "requerimiento" or "req")
        {
            _session = new Session(IntakeMode.Pdf, DateTimeOffset.UtcNow + SessionTtl);
            await ReplyAsync(incoming,
                "modo PDF abierto (30 min): mandame imágenes/texto/audios y tras unos segundos " +
                "de inactividad te devuelvo el PDF armado. \"listo\" cierra el modo.", ct);
            return true;
        }

        // Sin contexto: idea de texto / link → a la cola sin rutear + preguntar.
        AddUnrouted(new UnroutedItem(IntakeKind.Text, null, null, text, incoming.RawJson, DateTimeOffset.UtcNow));
        ScheduleRouteMenu(incoming.InstanceName, incoming.FromJid);
        return true;
    }

    // ── Tema del día (WhatsApp) ──────────────────────────────────────────────
    private async Task HandleDailyThemeAsync(ConversationService.IncomingMessage incoming, string text, CancellationToken ct)
    {
        // Saco el prefijo "tema"/"tema del día"/"tema:".
        var body = text;
        foreach (var pre in new[] { "tema del día", "tema del dia", "tema:", "tema" })
            if (body.StartsWith(pre, StringComparison.OrdinalIgnoreCase)) { body = body[pre.Length..].TrimStart(':', ' ').Trim(); break; }
        await SetDailyThemeAsync(incoming, body, ct);
    }

    /// <summary>
    /// Fija el tema del día global (o lo saca si <paramref name="body"/> viene vacío/"off"/"clear").
    /// Reusado por el comando "tema:" y por la opción 3 del menú de ruteo.
    /// </summary>
    private async Task SetDailyThemeAsync(ConversationService.IncomingMessage incoming, string body, CancellationToken ct)
    {
        var row = await _db.DailyThemes.FirstOrDefaultAsync(t => t.Id == 1, ct);
        if (row is null) { row = new DailyTheme { Id = 1 }; _db.DailyThemes.Add(row); }

        if (body.Length == 0 || body.Equals("off", StringComparison.OrdinalIgnoreCase) || body.Equals("clear", StringComparison.OrdinalIgnoreCase))
        {
            row.Text = string.Empty; row.ValidUntil = null; row.UpdatedAt = DateTimeOffset.UtcNow;
            await _db.SaveChangesAsync(ct);
            await ReplyAsync(incoming, "saqué el tema del día. Los próximos posteos vuelven al mix normal.", ct);
            return;
        }

        // Vigente hasta fin del día en hora AR.
        var nowAr = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, ArTz);
        var endAr = new DateTimeOffset(nowAr.Year, nowAr.Month, nowAr.Day, 23, 59, 59, nowAr.Offset);
        row.Text = Truncate(body, 500);
        row.ProductKey = null; // global (todas las apps)
        row.ValidUntil = endAr.ToUniversalTime();
        row.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);

        await ReplyAsync(incoming,
            $"listo, tema del día: \"{row.Text}\" ✅. Va a teñir los posteos de hoy de todas las apps (hasta las 23:59). " +
            "Mandá \"tema off\" para sacarlo.", ct);
    }

    private static readonly TimeZoneInfo ArTz = ResolveArTz();
    private static TimeZoneInfo ResolveArTz()
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById("America/Argentina/Buenos_Aires"); }
        catch { return TimeZoneInfo.CreateCustomTimeZone("AR", TimeSpan.FromHours(-3), "AR", "AR"); }
    }

    // ── Respuesta al "¿qué hago con esto?" (1/2) ─────────────────────────────
    private async Task HandleRouteAnswerAsync(ConversationService.IncomingMessage incoming, string phone, string text, string lower, CancellationToken ct)
    {
        if (lower is "cancelar")
        {
            var n = TakeUnrouted().Count;
            await ReplyAsync(incoming, $"listo, descarté {n} mensaje(s).", ct);
            return;
        }

        if (lower is "1" or "insp" or "inspiracion" or "inspiración" or "ideas")
        {
            var items = TakeUnrouted();
            var saved = 0;
            foreach (var u in items)
            {
                if (u.Kind == IntakeKind.Image)
                {
                    if (await SaveImageInspirationFromRawAsync(incoming.InstanceName, u.RawJson, u.Caption, u.Mime, ct)) saved++;
                }
                else if (!string.IsNullOrWhiteSpace(u.Text))
                {
                    await SaveTextInspirationAsync(u.Text!, ct);
                    saved++;
                }
            }
            if (saved == 0)
            {
                await ReplyAsync(incoming, "no pude guardar nada de eso 😕 (¿imagen vencida?). Probá mandarlo de nuevo.", ct);
                return;
            }
            _lastTopicAskAt = DateTimeOffset.MinValue; // forzar la pregunta del tema
            await AskTopicIfDueAsync(incoming, ct);
            return;
        }

        if (lower is "2" or "pdf" or "requerimiento" or "req")
        {
            var items = TakeUnrouted();
            var window = await BatchWindowAsync(ct);
            foreach (var u in items)
            {
                var bi = u.Kind == IntakeKind.Image
                    ? new BatchItem(BatchItemKind.Image, u.Caption, u.RawJson)
                    : new BatchItem(BatchItemKind.Text, u.Text, null);
                _batch.Enqueue(incoming.InstanceName, phone, bi, window);
            }
            await ReplyAsync(incoming, $"dale, armo el PDF con {items.Count} mensaje(s) 📄 — sale en unos segundos.", ct);
            return;
        }

        // 3 = tema del día: el/los texto(s) que mandaste se fijan como tema (global, todas las apps).
        if (lower is "3" or "tema del día" or "tema del dia")
        {
            var items = TakeUnrouted();
            var body = string.Join(" ", items
                .Select(u => u.Kind == IntakeKind.Image ? u.Caption : u.Text)
                .Where(s => !string.IsNullOrWhiteSpace(s))!).Trim();
            if (string.IsNullOrWhiteSpace(body))
            {
                await ReplyAsync(incoming, "para el tema del día mandame el texto (ej: \"Argentina ganó\"). Una imagen sola no me sirve de tema 🙂.", ct);
                return;
            }
            await SetDailyThemeAsync(incoming, body, ct);
            return;
        }

        // Cualquier otro texto mientras espera el ruteo: lo tratamos como más contenido.
        AddUnrouted(new UnroutedItem(IntakeKind.Text, null, null, text, incoming.RawJson, DateTimeOffset.UtcNow));
        ScheduleRouteMenu(incoming.InstanceName, incoming.FromJid);
    }

    // ── Respuesta al "¿para qué tema es?" ────────────────────────────────────
    private async Task HandleTopicAnswerAsync(ConversationService.IncomingMessage incoming, string text, CancellationToken ct)
    {
        var since = DateTimeOffset.UtcNow - PendingWindow;
        var pending = await _db.InspirationItems
            .Where(i => i.PendingTopic && i.CreatedAt >= since)
            .OrderBy(i => i.CreatedAt)
            .ToListAsync(ct);
        if (pending.Count == 0) return;

        if (string.Equals(text, "cancelar", StringComparison.OrdinalIgnoreCase))
        {
            _db.InspirationItems.RemoveRange(pending);
            await _db.SaveChangesAsync(ct);
            await ReplyAsync(incoming, $"listo, descarté {pending.Count} inspiración(es).", ct);
            return;
        }

        string? productKey; string topic;

        // Respuesta numérica → opción del último menú que mandamos.
        if (int.TryParse(text, out var n))
        {
            var menu = _lastMenu;
            if (menu is null || n < 1 || n > menu.Count)
            {
                // Menú perdido (reinicio) o número fuera de rango: re-mandamos fresco.
                _lastTopicAskAt = DateTimeOffset.MinValue;
                await AskTopicIfDueAsync(incoming, ct);
                return;
            }
            (topic, productKey) = (menu[n - 1].Topic, menu[n - 1].ProductKey);
        }
        else
        {
            (productKey, topic) = await ParseTopicAsync(text, ct);
        }

        foreach (var item in pending)
        {
            item.Topic = topic;
            item.ProductKey = productKey ?? item.ProductKey;
            item.PendingTopic = false;
        }
        await _db.SaveChangesAsync(ct);
        _lastMenu = null;

        var appLabel = productKey is null ? "" : $" ({productKey})";
        await ReplyAsync(incoming,
            $"guardé {pending.Count} inspiración(es) en \"{topic}\"{appLabel} ✅. " +
            "Las uso para los próximos posteos (tema + estilo visual).", ct);
    }

    // ── Preguntas del bot ────────────────────────────────────────────────────

    /// <summary>
    /// (Re)arma el debounce del menú de ruteo: mientras llega contenido seguido se resetea; a los
    /// <see cref="MenuDebounce"/> de silencio, el timer manda el menú UNA vez. No manda nada acá.
    /// </summary>
    private void ScheduleRouteMenu(string instance, string replyJid)
    {
        lock (MenuTimerLock)
        {
            _menuInstance = instance;
            _menuReplyJid = replyJid;
            _routeMenuTimer ??= new System.Threading.Timer(static _ => _ = FireRouteMenuAsync());
            _routeMenuTimer.Change(MenuDebounce, Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>
    /// Callback del debounce (corre fuera de un webhook): manda el menú de ruteo si TODAVÍA hay
    /// contenido sin rutear y no se abrió una sesión en el ínterin. Usa un scope nuevo para el
    /// IEvolutionClient (el relay original ya se disposeó).
    /// </summary>
    private static async Task FireRouteMenuAsync()
    {
        string? instance, jid;
        lock (MenuTimerLock) { instance = _menuInstance; jid = _menuReplyJid; }
        var sf = _scopeFactory;
        if (instance is null || jid is null || sf is null) return;

        // Si abrió sesión (insp/pdf) o ya no queda nada sin rutear (contestó/canceló), no molestamos.
        var s = _session;
        if (s is not null && DateTimeOffset.UtcNow < s.Until) return;
        if (!HasUnrouted()) return;

        try
        {
            using var scope = sf.CreateScope();
            var evo = scope.ServiceProvider.GetRequiredService<IEvolutionClient>();
            await evo.SendTextAsync(instance, jid, $"{BotPrefix} {RouteMenuText}", CancellationToken.None);
        }
        catch (Exception ex)
        {
            try
            {
                using var scope = sf.CreateScope();
                scope.ServiceProvider.GetRequiredService<ILogger<InspirationIntakeRelay>>()
                    .LogWarning(ex, "No pude mandar el menú de ruteo (debounce)");
            }
            catch { /* best-effort */ }
        }
    }

    /// <summary>
    /// Pregunta "¿para qué es?" con un MENÚ NUMERADO: primero los temas ya usados
    /// (los más recientes) y después TODAS las apps (elegir una app guarda en su tema
    /// "general"). Texto libre sigue creando un tema nuevo. La respuesta numérica
    /// banca 2 dígitos, así que el menú puede pasar de 9.
    /// Se saltea si ya preguntamos hace poco (ráfaga de imágenes).
    /// </summary>
    private async Task AskTopicIfDueAsync(ConversationService.IncomingMessage incoming, CancellationToken ct)
    {
        if (DateTimeOffset.UtcNow - _lastTopicAskAt < AskCooldown) return;
        _lastTopicAskAt = DateTimeOffset.UtcNow;

        var topics = await _db.InspirationItems.AsNoTracking()
            .Where(i => !i.PendingTopic && i.Topic != "sin-clasificar")
            .GroupBy(i => new { i.Topic, i.ProductKey })
            .Select(g => new { g.Key.Topic, g.Key.ProductKey, Last = g.Max(x => x.CreatedAt) })
            .OrderByDescending(x => x.Last)
            .Take(6)
            .ToListAsync(ct);
        var apps = await _db.PostingProfiles.AsNoTracking()
            .OrderBy(p => p.ProductKey).Select(p => p.ProductKey).ToListAsync(ct);

        var menu = topics.Select(t => new MenuOption(t.Topic, t.ProductKey)).ToList();
        foreach (var app in apps)
            if (!menu.Any(m => m.Topic == "general" && m.ProductKey == app))
                menu.Add(new MenuOption("general", app));

        if (menu.Count == 0)
        {
            _lastMenu = null;
            await ReplyAsync(incoming,
                "¡buena! ¿Para qué es esto? Contestame el tema (ej: \"motivación\" o \"gymhero motivación\")." +
                " Mandá \"cancelar\" para descartarla.", ct);
            return;
        }

        _lastMenu = menu;

        var sb = new StringBuilder();
        sb.AppendLine("¡buena! ¿Para qué es? Contestá el número:");
        for (var i = 0; i < menu.Count; i++)
        {
            // Las entradas de app (tema "general") se muestran como la app a secas.
            sb.AppendLine(menu[i].Topic == "general" && menu[i].ProductKey is not null
                ? $"{i + 1}. {menu[i].ProductKey}"
                : $"{i + 1}. {menu[i].Topic}{(menu[i].ProductKey is null ? "" : $" ({menu[i].ProductKey})")}");
        }
        sb.Append("O escribí un tema nuevo (ej: \"gymhero motivación\"). \"cancelar\" descarta.");
        await ReplyAsync(incoming, sb.ToString(), ct);
    }

    // ── Persistencia de inspiraciones ────────────────────────────────────────

    private async Task SaveImageInspirationAsync(ConversationService.IncomingMessage incoming, string? caption, string? mime, CancellationToken ct)
    {
        if (!await SaveImageInspirationFromRawAsync(incoming.InstanceName, incoming.RawJson, caption, mime, ct))
            await ReplyAsync(incoming, "no pude bajar esa imagen 😕. Probá mandarla de nuevo.", ct);
    }

    private async Task<bool> SaveImageInspirationFromRawAsync(string instance, string rawJson, string? caption, string? mime, CancellationToken ct)
    {
        var bytes = await _evo.GetMediaBase64Async(instance, rawJson, ct);
        if (bytes is null || bytes.Length == 0 || bytes.Length > MaxImageBytes)
        {
            _log.LogWarning("Inspiración: no se pudo bajar/guardar la imagen (instance={I}, bytes={B})", instance, bytes?.Length ?? 0);
            return false;
        }

        _db.InspirationItems.Add(new InspirationItem
        {
            Id = Guid.NewGuid(),
            Topic = "sin-clasificar",
            Note = string.IsNullOrWhiteSpace(caption) ? null : caption.Trim(),
            MimeType = string.IsNullOrWhiteSpace(mime) ? "image/jpeg" : mime,
            ImageContent = bytes,
            SizeBytes = bytes.Length,
            PendingTopic = true,
        });
        await _db.SaveChangesAsync(ct);
        return true;
    }

    private async Task SaveTextInspirationAsync(string text, CancellationToken ct)
    {
        var url = UrlRegex.Match(text).Value;
        _db.InspirationItems.Add(new InspirationItem
        {
            Id = Guid.NewGuid(),
            Topic = "sin-clasificar",
            Note = text,
            SourceUrl = string.IsNullOrEmpty(url) ? null : url,
            PendingTopic = true,
        });
        await _db.SaveChangesAsync(ct);
    }

    private Task<bool> HasPendingTopicAsync(CancellationToken ct)
    {
        var since = DateTimeOffset.UtcNow - PendingWindow;
        return _db.InspirationItems.AsNoTracking().AnyAsync(i => i.PendingTopic && i.CreatedAt >= since, ct);
    }

    /// <summary>
    /// "gymhero motivación" → (gymhero, "motivación"); "humor padel" → (null, "humor padel").
    /// La primera palabra sólo se toma como app si matchea un productKey real.
    /// </summary>
    private async Task<(string? ProductKey, string Topic)> ParseTopicAsync(string text, CancellationToken ct)
    {
        var normalized = text.Trim();
        var firstSpace = normalized.IndexOf(' ');
        var firstWord = (firstSpace < 0 ? normalized : normalized[..firstSpace]).Trim().ToLowerInvariant();

        var productKey = await _db.PostingProfiles.AsNoTracking()
            .Where(p => p.ProductKey.ToLower() == firstWord)
            .Select(p => p.ProductKey)
            .FirstOrDefaultAsync(ct);

        if (productKey is null) return (null, Truncate(normalized, 128));

        var rest = firstSpace < 0 ? "" : normalized[(firstSpace + 1)..].Trim();
        return (productKey, Truncate(rest.Length == 0 ? "general" : rest, 128));
    }

    // ── Sesión / cola sin rutear ─────────────────────────────────────────────

    private static Session? CurrentSession()
    {
        var s = _session;
        if (s is null) return null;
        if (DateTimeOffset.UtcNow >= s.Until) { _session = null; return null; }
        return s;
    }

    private static void RenewSession(Session s) => _session = s with { Until = DateTimeOffset.UtcNow + SessionTtl };

    private static void AddUnrouted(UnroutedItem item)
    {
        lock (UnroutedLock)
        {
            // Purga de viejos (>60 min) — si quedó colgado, no contamina el próximo envío.
            var cutoff = DateTimeOffset.UtcNow - PendingWindow;
            Unrouted.RemoveAll(u => u.At < cutoff);
            if (Unrouted.Count < UnroutedCap) Unrouted.Add(item);
        }
    }

    private static bool HasUnrouted()
    {
        lock (UnroutedLock)
        {
            var cutoff = DateTimeOffset.UtcNow - PendingWindow;
            Unrouted.RemoveAll(u => u.At < cutoff);
            return Unrouted.Count > 0;
        }
    }

    private static List<UnroutedItem> TakeUnrouted()
    {
        lock (UnroutedLock)
        {
            var items = Unrouted.ToList();
            Unrouted.Clear();
            return items;
        }
    }

    /// <summary>Ventana de debounce del batch PDF (config de transcripción; default 10s).</summary>
    private async Task<int> BatchWindowAsync(CancellationToken ct)
    {
        var s = await _db.TranscriptionSettings.AsNoTracking().FirstOrDefaultAsync(ct);
        return s is null || s.BatchWindowSeconds < 1 ? 10 : s.BatchWindowSeconds;
    }

    private bool IsDuplicate(ConversationService.IncomingMessage incoming)
    {
        if (incoming.MessageId is null) return false;
        CleanupRecent();
        return !Recent.TryAdd(incoming.MessageId, incoming.Timestamp);
    }

    private Task ReplyAsync(ConversationService.IncomingMessage incoming, string text, CancellationToken ct)
        => _evo.SendTextAsync(incoming.InstanceName, incoming.FromJid, $"{BotPrefix} {text}", ct);

    private async Task<ConfigSnapshot> GetConfigAsync(CancellationToken ct)
    {
        var entry = _cache;
        if (entry is not null && DateTimeOffset.UtcNow < entry.Until) return entry.Snap;

        var s = await _db.InspirationSettings.AsNoTracking().FirstOrDefaultAsync(ct);
        var masterDigits = s?.MasterPhone is null ? null : NormalizeDigits(s.MasterPhone);
        var snap = new ConfigSnapshot(
            s?.Enabled == true,
            s?.InstanceName,
            masterDigits is null || masterDigits.Length < 6 ? null : Suffix(masterDigits));

        _cache = new CacheEntry(snap, DateTimeOffset.UtcNow + CacheTtl);
        return snap;
    }

    private enum IntakeKind { Image, Text, Audio }

    /// <summary>Clasifica desde el JSON crudo: imagen (o doc imagen), texto, audio, o nada nuestro.</summary>
    private static (IntakeKind? Kind, string? Caption, string? Mime) Classify(string rawJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(rawJson);
            if (!doc.RootElement.TryGetProperty("message", out var body) || body.ValueKind != JsonValueKind.Object)
                return (null, null, null);

            if (body.TryGetProperty("imageMessage", out var img))
                return (IntakeKind.Image, StringProp(img, "caption"), StringProp(img, "mimetype"));
            if (body.TryGetProperty("audioMessage", out _))
                return (IntakeKind.Audio, null, null);
            if (body.TryGetProperty("documentMessage", out var docMsg))
            {
                var mime = StringProp(docMsg, "mimetype");
                if (mime is not null && mime.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
                    return (IntakeKind.Image, StringProp(docMsg, "caption"), mime);
                return (null, null, null);
            }
            if (body.TryGetProperty("conversation", out var conv) && conv.ValueKind == JsonValueKind.String)
                return (IntakeKind.Text, null, null);
            if (body.TryGetProperty("extendedTextMessage", out var ext)
                && ext.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String)
                return (IntakeKind.Text, null, null);

            return (null, null, null);
        }
        catch
        {
            return (null, null, null);
        }
    }

    private static string? StringProp(JsonElement el, string prop) =>
        el.ValueKind == JsonValueKind.Object && el.TryGetProperty(prop, out var v)
            && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static string Truncate(string s, int n) => s.Length <= n ? s : s[..n];

    private static string? NormalizeDigits(string? phone)
    {
        if (string.IsNullOrWhiteSpace(phone)) return null;
        var digits = NonDigit.Replace(phone, "");
        return string.IsNullOrWhiteSpace(digits) ? null : digits;
    }

    private static string Suffix(string digits) => digits.Length >= 8 ? digits[^8..] : digits;

    private static string? ExtractPhone(string? jid)
    {
        if (string.IsNullOrWhiteSpace(jid)) return null;
        var at = jid.IndexOf('@');
        return at > 0 ? jid[..at] : jid;
    }

    private static void CleanupRecent()
    {
        if (Recent.Count < 64) return;
        var cutoff = DateTimeOffset.UtcNow - RecentTtl;
        foreach (var kv in Recent)
            if (kv.Value < cutoff) Recent.TryRemove(kv.Key, out _);
    }
}
