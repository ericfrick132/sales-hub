using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SalesHub.Core.Domain.Entities;
using SalesHub.Core.Domain.Enums;
using SalesHub.Infrastructure.Persistence;

namespace SalesHub.Infrastructure.Services;

/// <summary>
/// Campaña de remarketing: le escribe a leads que ya nos hablaron y se enfriaron, con un
/// opener según la etapa donde quedó la charla, para coordinar una llamada.
///
/// Por qué así (docs/bases-de-venta-2026-09.md §8, medido sobre 785 reintentos reales):
/// - la antigüedad casi no cambia la tasa de respuesta (24-28% de 14 a 240 días); lo que
///   la mueve es si le debíamos respuesta (56%) y cuánto había escrito (16% → 43%). Ese es
///   el orden de la cola, con la antigüedad de desempate;
/// - el tope es POR LÍNEA y POR DÍA: para WhatsApp un chat de meses es casi un contacto
///   frío, y el ban del 30/07 salió de cold reach sin freno.
///
/// Encola directo en el Outbox (no por OutboxEnqueueHelper, que bloquea a propósito el
/// origen Remarketing) con <see cref="MessageOutbox.RemarketingCategory"/>, que los senders
/// reconocen para no aplicarle la política por origen.
/// </summary>
public class RemarketingService
{
    public const string Tag = "bot-remarketing";

    /// <summary>Prioridad de las filas: por debajo de los calientes (70), por encima del frío (50).</summary>
    public const int OutboxPriority = 60;

    private static readonly LeadStatus[] ExcludedStatuses =
        { LeadStatus.Closed, LeadStatus.Lost, LeadStatus.Blocked, LeadStatus.NoWhatsApp };

    private readonly ApplicationDbContext _db;
    private readonly ClaudeClient _claude;
    private readonly ILogger<RemarketingService> _log;

    public RemarketingService(ApplicationDbContext db, ClaudeClient claude, ILogger<RemarketingService> log)
    {
        _db = db; _claude = claude; _log = log;
    }

    // ── Etapas ─────────────────────────────────────────────────────────────

    /// <param name="Goal">Qué tiene que lograr el mensaje (para la personalización con IA).</param>
    /// <param name="Fallback">Opener sin IA. Placeholders: {saludo} {vendedor} {producto}.</param>
    public record StageDef(string Key, string Label, string Goal, string Fallback);

    public static readonly IReadOnlyList<StageDef> Stages = new StageDef[]
    {
        new("debemos_respuesta", "Le debíamos respuesta",
            "le debíamos respuesta a lo último que escribió. nombrá en pocas palabras qué había preguntado o dicho, pedí perdón corto por la demora y ofrecé explicárselo en una llamada de 10 minutos",
            "{saludo} soy {vendedor} de {producto}. nos escribiste hace un tiempo y te quedó sin respuesta, perdón. te llamo 10 minutos esta semana y lo vemos? decime qué día y horario te queda cómodo"),
        new("pidio_llamada", "Pidió llamada o demo",
            "había pedido una llamada o una demo y no se la hicimos. reconocelo como error nuestro y pedile qué día y horario le queda cómodo",
            "{saludo} soy {vendedor} de {producto}. me habías pedido una llamada y no te la hicimos, error nuestro. decime qué día y horario te queda cómodo esta semana y te llamo"),
        new("precio", "Se enfrió en el precio",
            "se enfrió después de hablar del precio. no repitas el número, ofrecé mostrarle en una llamada corta cuánto le recupera con su caso para que decida con números",
            "{saludo} soy {vendedor} de {producto}. hablamos del precio hace un tiempo y quedó ahí. si querés en 10 minutos por teléfono te muestro los números con tu caso y decidís tranquilo. qué día te queda cómodo esta semana?"),
        new("calificacion", "Quedó a mitad de las preguntas",
            "la charla quedó a mitad de las preguntas iniciales. retomá desde donde quedó y ofrecé una llamada de 10 minutos para decirle si le sirve o no, sin vueltas",
            "{saludo} soy {vendedor} de {producto}. habíamos empezado a charlar y quedó por la mitad. en una llamada de 10 minutos te digo si te sirve o no, sin vueltas. qué día y horario te queda cómodo esta semana?"),
        new("mail", "Se cortó al pedirle el mail",
            "se cortó cuando le pedimos el mail antes de contestarle lo que quería saber. reconocelo corto (mal nuestro) y ofrecé contarle todo en una llamada",
            "{saludo} soy {vendedor} de {producto}. la otra vez te pedí el mail antes de contestarte lo que querías saber, mal ahí. te cuento todo en una llamada de 10 minutos y si te sirve te dejo la cuenta armada en el momento. qué día te queda cómodo?"),
        new("pago", "Quedó en el pago",
            "había quedado por pagar o activar y el link de pago le falló a varios. ofrecé resolverlo en una llamada de 5 minutos",
            "{saludo} soy {vendedor} de {producto}. habías quedado por activar y no sé si el link de pago te anduvo, a varios les dio problemas. si querés lo resolvemos en una llamada de 5 minutos y te queda activo en el momento. qué día y horario te queda cómodo?"),
        new("video", "Le mandamos el video y no volvió",
            "le mandamos un video o una demo grabada y no volvió. proponé algo más útil: dejárselo armado con sus datos en una llamada de 15 minutos",
            "{saludo} soy {vendedor} de {producto}. te había mandado el video y no sé si llegaste a verlo. más fácil: en una llamada de 15 minutos te lo dejo armado con tus datos y lo ves andando. qué día te queda cómodo esta semana?"),
        new("cuenta_creada", "Cuenta creada sin acompañar",
            "le creamos la cuenta y nadie lo ayudó a arrancar (o se trabó entrando). hacete cargo corto y ofrecé dejársela andando con sus datos en una llamada de 15 minutos",
            "{saludo} soy {vendedor} de {producto}. te quedó la cuenta creada pero nadie te ayudó a arrancar, eso fue error nuestro. en una llamada de 15 minutos te la dejo andando con tus datos. qué día y horario te queda cómodo?"),
        new("mas_adelante", "Lo dejó para más adelante",
            "había dicho que lo veía más adelante (todavía no abría, o para cierto mes). preguntá cómo viene, mencioná la fecha si la dijo, y ofrecé dejarle el sistema armado antes de arrancar",
            "{saludo} soy {vendedor} de {producto}. me habías dicho que lo veías más adelante y quería saber cómo venía todo. si ya tenés fecha te dejo el sistema armado antes, así arrancás ordenado. te llamo 10 minutos esta semana?"),
        new("lo_veo", "Dijo \"lo veo y te aviso\"",
            "había dicho que lo miraba y no le hicimos seguimiento. preguntá si le quedó alguna duda, ofrecé una llamada corta y dale una salida fácil si no es el momento",
            "{saludo} soy {vendedor} de {producto}. me habías dicho que lo mirabas y se me pasó hacerte el seguimiento. si querés lo vemos en una llamada de 10 minutos esta semana, y si no es el momento me decís y no te escribo más"),
        new("ya_tiene_sistema", "Ya tenía otro sistema",
            "hace meses dijo que ya usaba otro sistema. no lo presiones: preguntá una sola cosa, si hay algo que le moleste del que usa, y ofrecé mostrarle solo eso en 10 minutos",
            "{saludo} soy {vendedor} de {producto}. hace un tiempo me dijiste que ya usabas otro sistema. te pregunto una sola cosa: hay algo que te moleste de cómo funciona? si hay algo te muestro solo eso en 10 minutos, si no lo dejamos acá"),
        new("general", "Se enfrió (otro)",
            "la charla quedó colgada. retomá con algo concreto de lo que se habló y ofrecé una llamada corta, con salida fácil si ya no le interesa",
            "{saludo} soy {vendedor} de {producto}. habíamos hablado hace un tiempo y quedó colgado. te llamo 10 minutos esta semana y vemos si te sirve? decime qué día y horario te queda cómodo, y si ya no te interesa me decís y no te escribo más"),
    };

    public static StageDef StageOf(string key) => Stages.FirstOrDefault(s => s.Key == key) ?? Stages[^1];

    // Mensajes del LEAD.
    private static readonly Regex NoRx = new(@"no (me )?interesa|no,? gracias|no necesito|no estoy interesad|borr[aá]me|no me escrib|dej[aá] de escrib|te voy a bloquear|denunci|\bspam\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex AlreadyRx = new(@"ya (tengo|uso|usamos|contamos|trabajo con|trabajamos con|estamos (usando|con)|contrat)|consegu[ií] otro", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex WrongRx = new(@"n[uú]mero equivocado|te equivocaste|no tengo m[aá]s (el )?(gym|gimnasio|negocio|local)|lo vend[ií]|no es (un )?(gym|gimnasio)|no soy (el|la) due", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex AutoRx = new(@"gracias por (comunicarte|escribirnos|escribir|contactarte|contactarnos|tu mensaje)|asistente virtual|te responder|responderemos|a la brevedad|en este momento no (estamos|podemos)|horario de atenci|te he conectado con|revisar[aá] tu mensaje|mensaje autom|vence en \d+ minutos|c[oó]digo de (verificaci|acceso)|tu c[oó]digo es", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Señales de que ya es CLIENTE (pagó, renueva, pide soporte de algo contratado): no es remarketing.
    private static readonly Regex CustomerInRx = new(@"comprobante|te transfer|ya (te )?(pagu|abon|transfer)|me venci[oó]|se me venci|renov|mi suscripci|mis socios no", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex CustomerOutRx = new(@"\bactivad[oa]\b|ya qued[oó] activ|bienvenid[oa]s|pago recibido|recibimos (tu|el) pago", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Una charla de prospecto que avanza tiene mediana ~12 mensajes; con más de esto es un cliente o un conocido.</summary>
    private const int MaxInboundForProspect = 60;
    private static readonly Regex CallRx = new(@"ll[aá]m(a|ame|ada|ar|en)|videollamada|reuni[oó]n|\bdemo\b|hablar con (alguien|una persona)|\bmeet\b|\bzoom\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex LaterRx = new(@"m[aá]s adelante|todav[ií]a no (abr|arranc|empec|tengo)|estoy por abrir|voy a abrir|abro en|inaugur|el mes que viene|la semana que viene|a fin de mes|en (enero|febrero|marzo|abril|mayo|junio|julio|agosto|septiembre|setiembre|octubre|noviembre|diciembre)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex AckRx = new(@"^\W*(ok+|oka?y?|oki|dale+|genial|perfecto|listo|bueno|b[aá]rbaro|joya|gracias|muchas gracias|lo (veo|miro|reviso|analizo|pienso)|lo vemos|te aviso|voy a (ver|mirar|chusmear))\b.{0,50}$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex SignalRx = new(@"precio|cu[aá]nto (sale|cuesta|es|ser[ií]a)|me interesa|quiero (contratar|probar|empezar|activar|pagar|arrancar)|c[oó]mo (pago|contrato|arranco)|\balias\b|\bcbu\b|ll[aá]m|\bdemo\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Mensajes NUESTROS: dónde quedó la charla (mismo criterio que docs/conversaciones/v2/analyze.py).
    private static readonly (string Stage, Regex Rx)[] OurStageRx =
    {
        ("pago", new(@"\balias\b|\bcbu\b|transfer|mercado ?pago|checkout|link de pago|abon", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
        ("mail", new(@"\bmail\b|correo|e-?mail", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
        ("precio", new(@"\$ ?\d|precio|por mes|mensual|anual|semestral|\bplan(es)?\b", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
        ("cuenta_creada", new(@"link|entr[aá] (directo|con|de ac[aá])|qued[aá]s adentro|te cre[eé] la cuenta|acceso", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
        ("video", new(@"video|loom|\bdemo\b|videollamada|\bmeet\b|\bzoom\b", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
        ("calificacion", new(@"c[oó]mo (se llama|llev[aá]s|tom[aá]s|manej)|cu[aá]ntos|qu[eé] (us[aá]s|sistema|rubro)|excel|papel", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
    };

    private static readonly Regex EmojiRx = new(@"[☀-➿]|\uD83C[\uDC00-\uDFFF]|\uD83D[\uDC00-\uDFFF]|\uD83E[\uDC00-\uDFFF]|️", RegexOptions.Compiled);

    /// <summary>Un lead de la cola, con la etapa y el orden calculados.</summary>
    public record Candidate(Guid LeadId, string Name, string ProductKey, Guid? CurrentSellerId,
        string Stage, int Score, bool Owed, int InboundCount, int IdleDays, DateTimeOffset LastAt, string LastInbound);

    // Clase con init (no record posicional): EF solo puede seguir filtrando/ordenando en SQL
    // sobre una proyección member-init.
    private sealed class Row
    {
        public Guid Id { get; init; }
        public string Name { get; init; } = "";
        public string ProductKey { get; init; } = "";
        public Guid? SellerId { get; init; }
        public DateTimeOffset? LastAt { get; init; }
        public int Nin { get; init; }
        public MessageDirection? LastDir { get; init; }
    }

    // ── Config ─────────────────────────────────────────────────────────────

    public async Task<RemarketingSettings> GetSettingsAsync(CancellationToken ct)
    {
        var s = await _db.RemarketingSettings.FirstOrDefaultAsync(x => x.Id == 1, ct);
        if (s is not null) return s;
        s = new RemarketingSettings();
        _db.RemarketingSettings.Add(s);
        await _db.SaveChangesAsync(ct);
        return s;
    }

    public static DateTimeOffset DayStartAr(DateTimeOffset now)
    {
        var ar = TimeZoneInfo.ConvertTime(now, LeadEntryService.ArTz);
        return new DateTimeOffset(ar.Date, ar.Offset);
    }

    // ── Cola ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Arma la cola ordenada: le debíamos respuesta → cuánto escribió → señal de compra → la
    /// más reciente. Excluye a quien dijo que no, número equivocado, negocios cerrados, a quien
    /// ya se le escribió por la campaña, a quien tiene algo encolado o una próxima acción futura.
    /// </summary>
    public async Task<List<Candidate>> RankAsync(RemarketingSettings s, int take, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var rows = await PoolQuery(s, now)
            .OrderByDescending(x => x.LastDir == MessageDirection.Inbound)
            .ThenByDescending(x => x.Nin)
            .ThenByDescending(x => x.LastAt)
            .Take(Math.Clamp(take * 4, 40, 400))
            .ToListAsync(ct);
        if (rows.Count == 0) return new();

        var ids = rows.Select(r => r.Id).ToList();
        var msgs = (await _db.ConversationMessages.AsNoTracking()
                .Where(m => ids.Contains(m.LeadId))
                .Select(m => new { m.LeadId, m.Direction, m.Text, m.Timestamp })
                .ToListAsync(ct))
            .GroupBy(m => m.LeadId)
            .ToDictionary(g => g.Key, g => g.OrderBy(m => m.Timestamp).Select(m => (m.Direction == MessageDirection.Inbound, m.Text ?? "")).ToList());

        var result = new List<Candidate>();
        foreach (var r in rows)
        {
            if (!msgs.TryGetValue(r.Id, out var thread)) continue;
            var idle = (int)(now - r.LastAt!.Value).TotalDays;
            var c = Classify(thread, idle);
            if (c is null) continue;
            var (stage, owed, nin, signal, lastIn) = c.Value;
            var score = (owed ? 1000 : 0) + Math.Min(nin, 20) * 20 + (signal ? 100 : 0) - Math.Min(idle, 400) / 4;
            result.Add(new Candidate(r.Id, r.Name, r.ProductKey, r.SellerId, stage, score, owed, nin, idle, r.LastAt.Value, lastIn));
        }
        return result.OrderByDescending(c => c.Score).Take(take).ToList();
    }

    /// <summary>Tamaño aproximado de la cola (antes de descartar a quien dijo que no, etc.).</summary>
    public Task<int> CountPoolAsync(RemarketingSettings s, CancellationToken ct)
        => PoolQuery(s, DateTimeOffset.UtcNow).CountAsync(ct);

    private IQueryable<Row> PoolQuery(RemarketingSettings s, DateTimeOffset now)
    {
        var minCut = now.AddDays(-Math.Max(1, s.MinIdleDays));
        DateTimeOffset? maxCut = s.MaxIdleDays is > 0 ? now.AddDays(-s.MaxIdleDays.Value) : null;
        var products = s.ProductKeys;
        return _db.Leads.AsNoTracking()
            .Where(l => l.WhatsappPhone != null && l.WhatsappPhone != ""
                     && !ExcludedStatuses.Contains(l.Status)
                     && (products.Count == 0 || products.Contains(l.ProductKey))
                     && !_db.Products.Any(p => p.ProductKey == l.ProductKey && p.AppManagedTransport)
                     && (l.NextActionAt == null || l.NextActionAt < now)
                     && !_db.RemarketingAttempts.Any(a => a.LeadId == l.Id)
                     && !_db.Outbox.Any(o => o.LeadId == l.Id && o.Status == OutboxStatus.Scheduled))
            .Select(l => new Row
            {
                Id = l.Id, Name = l.Name, ProductKey = l.ProductKey, SellerId = l.SellerId,
                LastAt = _db.ConversationMessages.Where(m => m.LeadId == l.Id).Max(m => (DateTimeOffset?)m.Timestamp),
                Nin = _db.ConversationMessages.Count(m => m.LeadId == l.Id && m.Direction == MessageDirection.Inbound),
                LastDir = _db.ConversationMessages.Where(m => m.LeadId == l.Id).OrderByDescending(m => m.Timestamp)
                    .Select(m => (MessageDirection?)m.Direction).FirstOrDefault(),
            })
            .Where(x => x.Nin > 0 && x.LastAt < minCut && (maxCut == null || x.LastAt >= maxCut));
    }

    /// <summary>
    /// Etapa de la charla. null = no se le escribe (dijo que no, número equivocado, cerró, o
    /// solo le contestó un contestador automático).
    /// </summary>
    public static (string Stage, bool Owed, int Nin, bool Signal, string LastInbound)? Classify(
        IReadOnlyList<(bool Inbound, string Text)> thread, int idleDays)
    {
        // Los contestadores automáticos de otros negocios no cuentan como mensajes del lead.
        var human = thread.Where(m => !m.Inbound || !AutoRx.IsMatch(m.Text)).ToList();
        var ins = human.Where(m => m.Inbound && !string.IsNullOrWhiteSpace(m.Text)).Select(m => m.Text).ToList();
        if (ins.Count == 0) return null;
        if (ins.Count > MaxInboundForProspect) return null;
        if (ins.Any(t => WrongRx.IsMatch(t) || CustomerInRx.IsMatch(t))) return null;
        if (human.Any(m => !m.Inbound && CustomerOutRx.IsMatch(m.Text))) return null;

        var lastTwo = ins.TakeLast(2).ToList();
        if (lastTwo.Any(t => NoRx.IsMatch(t))) return null;
        var already = lastTwo.Any(t => AlreadyRx.IsMatch(t));
        if (already && idleDays < 90) return null;

        var lastIn = ins[^1];
        var owed = human[^1].Inbound;
        var signal = ins.Any(t => SignalRx.IsMatch(t));
        if (already) return ("ya_tiene_sistema", owed, ins.Count, signal, lastIn);

        string stage;
        if (owed)
        {
            stage = CallRx.IsMatch(lastIn) ? "pidio_llamada"
                : LaterRx.IsMatch(lastIn) ? "mas_adelante"
                : AckRx.IsMatch(lastIn.Trim()) ? "lo_veo"
                : "debemos_respuesta";
        }
        else
        {
            var ours = string.Join(" ", human.Where(m => !m.Inbound).TakeLast(3).Select(m => m.Text));
            stage = OurStageRx.FirstOrDefault(x => x.Rx.IsMatch(ours)).Stage ?? "general";
            // Lo que dijo el lead pesa más que nuestro último mensaje.
            if (ins.TakeLast(3).Any(t => LaterRx.IsMatch(t))) stage = "mas_adelante";
            else if (ins.TakeLast(3).Any(t => CallRx.IsMatch(t))) stage = "pidio_llamada";
            else if (AckRx.IsMatch(lastIn.Trim()) && stage is "general" or "video" or "cuenta_creada") stage = "lo_veo";
        }
        return (stage, owed, ins.Count, signal, lastIn);
    }

    // ── Opener ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Escribe el opener. Con IA: Claude lo personaliza con la charla (nombra lo que había
    /// preguntado). Si la IA falla o devuelve algo que rompe las reglas de tono, sale el
    /// opener fijo de la etapa.
    /// </summary>
    public async Task<(string Text, bool Ai)> ComposeAsync(Candidate c, Seller seller, string productName,
        bool useAi, CancellationToken ct)
    {
        var st = StageOf(c.Stage);
        var vendedor = FirstWord(seller.DisplayName);
        var producto = productName.ToLowerInvariant();
        var nombre = FriendlyName(c.Name);
        var saludo = nombre is null ? "hola," : $"hola {nombre},";
        var fallback = st.Fallback.Replace("{saludo}", saludo).Replace("{vendedor}", vendedor).Replace("{producto}", producto);

        if (!useAi || !_claude.IsConfigured) return (fallback, false);

        var thread = await _db.ConversationMessages.AsNoTracking()
            .Where(m => m.LeadId == c.LeadId)
            .OrderByDescending(m => m.Timestamp)
            .Take(25)
            .Select(m => new { m.Direction, m.Text })
            .ToListAsync(ct);
        thread.Reverse();
        var chat = string.Join("\n", thread.Select(m =>
            (m.Direction == MessageDirection.Inbound ? "LEAD: " : "NOSOTROS: ") + Trunc((m.Text ?? "").Replace('\n', ' '), 300)));

        var system =
            $"Escribís UN mensaje de WhatsApp para retomar una charla de venta que se enfrió hace {c.IdleDays} días. " +
            $"Sos {vendedor}, del equipo de {producto}.\n" +
            "REGLAS:\n" +
            "- todo en minúscula. sin signos de apertura (nada de ¿ ni ¡). sin emojis.\n" +
            "- voseo argentino natural, sin muletillas (nada de che, capo, viste, boludo).\n" +
            "- máximo 3 oraciones y 350 caracteres.\n" +
            $"- empezá exactamente con: \"{saludo} soy {vendedor} de {producto}.\"\n" +
            "- nombrá en pocas palabras algo concreto de la charla (lo que preguntó o dónde quedó) para que se note que no es un mensaje masivo.\n" +
            "- no inventes datos, precios, descuentos ni funciones que no estén en la charla.\n" +
            "- terminá proponiendo una llamada corta esta semana y preguntando qué día y horario le queda cómodo.\n" +
            $"OBJETIVO DE ESTE MENSAJE: {st.Goal}.\n" +
            $"EJEMPLO DE TONO (no lo copies literal, adaptalo a la charla): {fallback}\n" +
            "Respondé SOLO con el texto del mensaje, sin comillas ni explicaciones.";

        try
        {
            var raw = await _claude.CompleteAsync(system, "CHARLA (la más vieja arriba):\n" + chat, 300, null, "remarketing", ct);
            var clean = Sanitize(raw);
            if (clean is not null && clean.StartsWith("hola")) return (clean, true);
            _log.LogInformation("Remarketing: opener de IA descartado para {Lead}, sale el fijo", c.LeadId);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex, "Remarketing: falló la IA para {Lead}, sale el fijo", c.LeadId);
        }
        return (fallback, false);
    }

    /// <summary>Aplica las reglas de tono a la salida de la IA; null si no se puede usar.</summary>
    public static string? Sanitize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var t = raw.Trim().Trim('"', '“', '”', '\'').Trim();
        t = EmojiRx.Replace(t, "").Replace("¿", "").Replace("¡", "");
        t = Regex.Replace(t, @"[ \t]{2,}", " ").Trim().ToLowerInvariant();
        if (t.Length is < 60 or > 450) return null;
        if (t.Contains('{') || Regex.IsMatch(t, @"bolud|\bche\b|x{3,}", RegexOptions.IgnoreCase)) return null;
        return t;
    }

    private static string FirstWord(string s)
    {
        var w = (s ?? "").Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
        return w.Length == 0 ? "el equipo" : w.ToLowerInvariant();
    }

    /// <summary>Nombre de pila si el nombre del lead parece de persona; null si es un negocio o basura.</summary>
    public static string? FriendlyName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var n = name.Trim();
        if (n.Equals("Contacto WhatsApp", StringComparison.OrdinalIgnoreCase)) return null;
        var words = n.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length > 3) return null;
        // Todo en mayúscula suele ser el nombre del gimnasio ("FUERZA TAUCAP").
        if (n.Length > 3 && n.Any(char.IsLetter) && n.Where(char.IsLetter).All(char.IsUpper)) return null;
        var first = words[0];
        if (first.Length < 3 || !first.All(char.IsLetter)) return null;
        var f = first.ToLowerInvariant();
        return NotNames.Contains(f) ? null : f;
    }

    private static readonly HashSet<string> NotNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "lead", "contacto", "cliente", "hola", "buenas", "gym", "gimnasio", "club", "centro", "estudio",
        "escuela", "academia", "fitness", "crossfit", "pilates", "box", "sport", "sports", "team", "the",
        "los", "las", "mis", "tus", "sus", "una", "uno", "info", "ventas", "admin", "recepcion", "recepción",
        "bonos", "conectar", "mi", "yo", "sin", "con", "del", "por", "para", "casa", "espacio", "sala",
    };

    private static string Trunc(string s, int max) => s.Length <= max ? s : s[..max] + "…";

    // ── Encolado ───────────────────────────────────────────────────────────

    /// <summary>Si la línea del vendedor puede mandar ahora mismo, y si no, por qué.</summary>
    public async Task<(bool Ok, string Reason, int? DeviceCap)> LineStatusAsync(Seller seller, CancellationToken ct)
    {
        if (!seller.IsActive) return (false, "vendedor desactivado", null);
        if (!seller.SendingEnabled) return (false, "envíos apagados para este vendedor", null);
        var fresh = DateTimeOffset.UtcNow.AddMinutes(-15);
        var device = await _db.Devices.AsNoTracking()
            .Where(d => d.SellerId == seller.Id && d.LastHeartbeatAt != null && d.LastHeartbeatAt > fresh)
            .OrderByDescending(d => d.LastHeartbeatAt)
            .FirstOrDefaultAsync(ct);
        if (device is not null) return (true, "celular vinculado", device.DailyNewChatCap ?? Device.DefaultDailyNewChatCap);
        var inst = seller.EvolutionInstance;
        if (inst is not null && inst.Status == InstanceStatus.Connected && !inst.ListenOnly) return (true, "línea conectada", null);
        if (inst is not null && inst.ListenOnly) return (false, "la línea es de solo escucha", null);
        return (false, "sin celular vinculado ni línea conectada", null);
    }

    public Task<int> UsedTodayAsync(Guid sellerId, CancellationToken ct)
    {
        var dayStart = DayStartAr(DateTimeOffset.UtcNow);
        return _db.RemarketingAttempts.CountAsync(a => a.SellerId == sellerId && a.EnqueuedAt >= dayStart, ct);
    }

    /// <summary>
    /// Encola lo que falta del cupo de hoy para una línea, repartido en lo que queda de la
    /// ventana horaria (con jitter). Devuelve cuántos encoló.
    /// </summary>
    public async Task<int> EnqueueForSellerAsync(RemarketingSettings s, Seller seller, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var nowAr = TimeZoneInfo.ConvertTime(now, LeadEntryService.ArTz);
        if (nowAr.Hour < s.SendHourStart || nowAr.Hour >= s.SendHourEnd) return 0;

        var remaining = Math.Max(0, s.PerLinePerDay) - await UsedTodayAsync(seller.Id, ct);
        if (remaining <= 0) return 0;

        var candidates = await RankAsync(s, remaining, ct);
        if (candidates.Count == 0) return 0;

        var products = await _db.Products.AsNoTracking().ToDictionaryAsync(p => p.ProductKey, p => p.DisplayName, ct);
        var windowEnd = new DateTimeOffset(nowAr.Date.AddHours(s.SendHourEnd), nowAr.Offset);
        var span = windowEnd - now;
        var step = TimeSpan.FromTicks(span.Ticks / Math.Max(1, candidates.Count));
        var rnd = Random.Shared;
        var count = 0;

        foreach (var c in candidates)
        {
            var lead = await _db.Leads.FirstOrDefaultAsync(l => l.Id == c.LeadId, ct);
            if (lead is null || string.IsNullOrWhiteSpace(lead.WhatsappPhone)) continue;

            var (text, ai) = await ComposeAsync(c, seller, products.GetValueOrDefault(lead.ProductKey, lead.ProductKey), s.PersonalizeWithAi, ct);
            // Primero sale enseguida; el resto repartido en la ventana, ±30% para no ser un reloj.
            var offset = count == 0 ? TimeSpan.Zero
                : step * count + TimeSpan.FromTicks((long)(step.Ticks * (rnd.NextDouble() * 0.6 - 0.3)));
            var outbox = new MessageOutbox
            {
                Id = Guid.NewGuid(),
                LeadId = lead.Id,
                SellerId = seller.Id,
                Channel = MessageChannel.WhatsApp,
                EvolutionInstance = seller.EvolutionInstance?.InstanceName ?? string.Empty,
                WhatsappPhone = lead.WhatsappPhone!,
                Message = text,
                StepIndex = 0,
                CadenceCategory = MessageOutbox.RemarketingCategory,
                ScheduledAt = now + offset,
                Priority = OutboxPriority,
            };
            _db.Outbox.Add(outbox);
            _db.RemarketingAttempts.Add(new RemarketingAttempt
            {
                Id = Guid.NewGuid(),
                LeadId = lead.Id,
                SellerId = seller.Id,
                Stage = c.Stage,
                Score = c.Score,
                Message = text,
                PersonalizedWithAi = ai,
                OutboxId = outbox.Id,
                EnqueuedAt = now,
            });

            // El que escribe es el que coordina la llamada: el lead pasa a esa línea, marcado
            // como asignación manual para que el rebalanceo no se lo lleve.
            lead.SellerId = seller.Id;
            lead.ManualAssignedAt = now;
            // Hasta que exista el flujo que coordina la llamada, el bot genérico no le contesta:
            // si responde, lo agarra el vendedor (ver OnReplyAsync).
            lead.BotMutedAt ??= now;
            if (!lead.Tags.Contains(Tag, StringComparer.OrdinalIgnoreCase))
                lead.Tags = lead.Tags.Append(Tag).ToList();   // lista nueva: el change tracker no ve los Add in-place
            lead.UpdatedAt = now;
            _db.LeadNotes.Add(new LeadNote
            {
                Id = Guid.NewGuid(),
                LeadId = lead.Id,
                Kind = LeadNoteKind.System,
                Text = $"Remarketing: se le escribe por {seller.DisplayName} (etapa: {StageOf(c.Stage).Label}).",
                CreatedAt = now,
            });
            count++;
        }

        await _db.SaveChangesAsync(ct);
        _log.LogInformation("Remarketing: {N} mensajes encolados por {Seller}", count, seller.DisplayName);
        return count;
    }

    /// <summary>
    /// Apagar la campaña (o sacar una línea) devuelve a la cola a los que todavía no salieron:
    /// cancela su fila del Outbox y borra el intento, así no quedan marcados como contactados.
    /// </summary>
    public async Task<int> CancelPendingAsync(IReadOnlyCollection<Guid>? onlySellers, CancellationToken ct)
    {
        var pending = await _db.Outbox
            .Where(o => o.Status == OutboxStatus.Scheduled && o.CadenceCategory == MessageOutbox.RemarketingCategory
                     && (onlySellers == null || onlySellers.Contains(o.SellerId)))
            .ToListAsync(ct);
        if (pending.Count == 0) return 0;
        var ids = pending.Select(o => (Guid?)o.Id).ToList();
        var attempts = await _db.RemarketingAttempts.Where(a => ids.Contains(a.OutboxId)).ToListAsync(ct);
        foreach (var o in pending) o.Status = OutboxStatus.Cancelled;
        _db.RemarketingAttempts.RemoveRange(attempts);
        await _db.SaveChangesAsync(ct);
        return pending.Count;
    }

    /// <summary>
    /// El lead contestó después de un mensaje de remarketing: queda registrado para la métrica
    /// y se le pone una próxima acción "coordinar la llamada" para que aparezca hoy en el CRM.
    /// No guarda: lo persiste quien llama, en la misma escritura que el mensaje entrante.
    /// </summary>
    public async Task OnReplyAsync(Lead lead, DateTimeOffset at, CancellationToken ct)
    {
        if (!lead.Tags.Contains(Tag, StringComparer.OrdinalIgnoreCase)) return;
        var attempt = await _db.RemarketingAttempts
            .Where(a => a.LeadId == lead.Id && a.RepliedAt == null && a.EnqueuedAt <= at)
            .OrderByDescending(a => a.EnqueuedAt)
            .FirstOrDefaultAsync(ct);
        if (attempt is null) return;
        // Solo cuenta si el mensaje ya había salido (una respuesta anterior al envío no es respuesta).
        var sent = attempt.OutboxId is { } oid
            && await _db.Outbox.AnyAsync(o => o.Id == oid && o.Status == OutboxStatus.Sent && o.SentAt <= at, ct);
        if (!sent) return;

        attempt.RepliedAt = at;
        lead.NextActionAt = DateTimeOffset.UtcNow;
        lead.NextActionNote = "Contestó al remarketing: coordinar la llamada";
        _db.LeadNotes.Add(new LeadNote
        {
            Id = Guid.NewGuid(),
            LeadId = lead.Id,
            Kind = LeadNoteKind.System,
            Text = "Contestó al mensaje de remarketing. Coordinar la llamada.",
            CreatedAt = DateTimeOffset.UtcNow,
        });
    }
}
