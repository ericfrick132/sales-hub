using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SalesHub.Core.Domain.Entities;
using SalesHub.Core.Domain.Enums;
using SalesHub.Infrastructure.Persistence;

namespace SalesHub.Infrastructure.Services;

/// <summary>
/// Guion "closer" de GymHero (12/11/2026, armado con Eric a partir del análisis de 1.369 chats): habla de lo que
/// le pasa al lead antes que del producto, una pregunta por vez, precio en el momento, la llamada se ofrece para
/// ya, y el seguimiento sigue aunque el chat lo haya tomado Mateo. El texto aprobado vive en
/// ~/Desktop/respuestas-claude/guion-bot-gymhero.txt; si cambia ahí, cambia acá.
///
/// Se prende con el flag de runtime <c>closer-gymhero</c> y solo toma leads NUEVOS (Step 0) de anuncio, formulario
/// de Meta u orgánicos de WhatsApp; los que ya estaban en el guion viejo lo terminan ahí. El estado propio
/// (cómo cobra, alumnos, sedes, llamada) va en <see cref="LeadOnboarding.ScriptState"/>.
/// </summary>
public partial class OnboardingService
{
    // ── Pasos del guion closer (≤ CloserStepMax, no chocan con los del guion viejo) ───────────────
    public const int CloserCobro = -100;      // preguntamos cómo lleva las cuotas
    public const int CloserComplica = -101;   // qué le complica (excel/papel)
    public const int CloserLista = -102;      // tendrás la lista de alumnos?
    public const int CloserSistema = -103;    // qué te llevó a mirar otras opciones (otro sistema)
    public const int CloserAbre = -104;       // para cuándo abrís
    public const int CloserAlumnos = -105;    // cuántos alumnos + clases/pase libre/ambas
    public const int CloserSedes = -106;      // una sede o más
    public const int CloserLlamada = -107;    // ofrecimos la llamada (ahora o en un rato / mañana a la mañana o a la tarde)
    public const int CloserHora = -108;       // ofrecimos dos horarios
    public const int CloserAgendada = -109;   // llamada agendada (CallAt)
    public const int CloserDatos = -110;      // pedimos nombre del gym + mail para la cuenta
    public const int CloserCuenta = -111;     // cuenta creada
    public const int CloserLlamando = -112;   // dijo "ahora": Mateo lo llama
    public const int CloserFin = -113;        // rechazo / no es cliente: no se le escribe más
    public const int CloserDecisor = -114;    // pedimos el contacto de quien gestiona el gimnasio
    public const int CloserStepMax = -100;

    public const string CloserFlag = "closer-gymhero";
    public const string CloserProduct = "gymhero";
    private const string DemoAdminUrl = "https://gymhero.fitness/demo-admin";

    /// <summary>Simulador: corre el guion closer aunque el flag esté apagado.</summary>
    public bool ForceCloser { get; set; }

    public sealed class CloserState
    {
        public string? Cobro { get; set; }          // excel | papel | sistema | abre
        public string? Sistema { get; set; }
        public int? Alumnos { get; set; }
        public string? Tipo { get; set; }           // clases | libre | ambas
        public int? Sedes { get; set; }
        public bool PlanesMostrados { get; set; }
        public bool CasoUsado { get; set; }
        public bool AlumnosRepreguntado { get; set; }
        public DateTimeOffset? CallAt { get; set; }
        public bool CallReminded { get; set; }
        public bool CallAlerted { get; set; }
        public List<DateTimeOffset> Offered { get; set; } = new();
        public DateTimeOffset? AbreFecha { get; set; }
        public bool AbreAvisado { get; set; }
        public string? NombreGym { get; set; }
    }

    private static readonly JsonSerializerOptions StateJson = new(JsonSerializerDefaults.Web);
    public static CloserState ReadState(LeadOnboarding ob)
    {
        if (string.IsNullOrWhiteSpace(ob.ScriptState)) return new CloserState();
        try { return JsonSerializer.Deserialize<CloserState>(ob.ScriptState!, StateJson) ?? new CloserState(); }
        catch { return new CloserState(); }
    }
    private static void WriteState(LeadOnboarding ob, CloserState s) => ob.ScriptState = JsonSerializer.Serialize(s, StateJson);

    /// <summary>¿Este lead corre el guion closer? Solo GymHero, con el flag, por WhatsApp, y si es nuevo o ya está en él.</summary>
    public async Task<bool> UsesCloserAsync(Lead lead, OnboardingConfig cfg, LeadOnboarding? ob, CancellationToken ct)
    {
        if (cfg.ProductKey != CloserProduct) return false;
        if (ob is not null && ob.Step <= CloserStepMax) return true;
        if (ob is not null && ob.Step != 0) return false;
        if (lead.Source is not (LeadSource.WhatsAppAd or LeadSource.WhatsAppInbound or LeadSource.MetaLeadAd)) return false;
        return ForceCloser || await _db.IsFlagOnAsync(CloserFlag, false, ct);
    }

    // ── Normalización y palabras que busca ───────────────────────────────────────────────────────
    private static readonly Regex Rep3 = new(@"(.)\1{2,}", RegexOptions.Compiled);
    private static readonly Regex RepEnd = new(@"([aeious])\1+\b", RegexOptions.Compiled);
    private static readonly Regex Spc = new(@"\s+", RegexOptions.Compiled);

    /// <summary>Minúscula, sin tildes, letras repetidas achicadas ("Siii" → "si"). Deja números, @ y signos.</summary>
    public static string CNorm(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        var t = text.ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(t.Length);
        foreach (var c in t) if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark) sb.Append(c);
        t = Rep3.Replace(sb.ToString(), "$1");
        t = RepEnd.Replace(t, "$1");
        return Spc.Replace(t, " ").Trim();
    }

    private static Regex R(string p) => new(p, RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex AbreRx = R(@"todavia no abr|voy a abrir|vamos a abrir|estoy por abrir|estamos por abrir|abro en|abrimos en|abro el|recien arranco|recien empiezo|estoy armando|estamos armando|\bproyecto\b|todavia no tengo alumnos|por inaugurar|inauguro|inauguramos");
    private static readonly Regex SistemaNegadoRx = R(@"ningun (sistema|programa|software|app)|no (uso|usamos|tengo|tenemos) (ningun |ninguna )?(sistema|programa|software|app|aplicacion)|sin (sistema|app)");
    private static readonly Regex SistemaRx = R(@"\b(sistema|app|aplicacion|programa|software|plataforma)\b|\bevo\b|acceso ?gym|crossfy|crosshero|fitco|siguefit|mindbody|trainingym|boxmagic|wodbuster");
    private static readonly Regex SistemaNombreRx = R(@"\b(evo|acceso ?gym|crossfy|crosshero|fitco|siguefit|mindbody|trainingym|boxmagic|wodbuster)\b");
    private static readonly Regex ExcelRx = R(@"\b(excel|exel|excell|planilla|planillas|sheets|drive|hoja de calculo)\b");
    private static readonly Regex PapelRx = R(@"\b(papel|cuaderno|libreta|a mano|agenda|fichas|ficha|anoto|anotado|por escrito|escrito|de memoria|en la cabeza|nada|ninguno)\b");
    private static readonly Regex MediosRx = R(@"mercado ?pago|transferencia|efectivo|debito");

    private static readonly Regex DebeRx = R(@"quien (me )?debe|\bdeben\b|deudor|moroso|atrasad|quien pago|quien no pago|control|\bsaber\b");
    private static readonly Regex CobrarRx = R(@"\bcobr|cobranza|perseguir|reclamar|que paguen|\bpagos?\b");
    private static readonly Regex DosRx = R(@"\blas dos\b|\bambas\b|\bambos\b|\btodo\b|un poco de todo");
    private static readonly Regex AvisosRx = R(@"\bavis|recordar|recordatorio|vencimiento|\bmensajes\b|le avisa|whatsapp automatico");
    private static readonly Regex NadaRx = R(@"^(nada|no me complica|me arreglo bien|nada en particular|ninguna)\b");

    private static readonly Regex SiRx = R(@"^(si|sisi|la tengo|tengo|dale|obvio|claro|ok|okey|bueno|seguro|si tengo|si la tengo)\b");
    private static readonly Regex NoRx = R(@"^(no|no tengo|nada|de memoria|ni ahi)\b");

    private static readonly Regex ClasesRx = R(@"\bclases?\b|\bturnos?\b|horario|grupal|cupo|reserva");
    private static readonly Regex LibreRx = R(@"pase libre|\blibre\b|musculacion|aparatos|cuando quieren|cuando quiere");
    private static readonly Regex MixtoRx = R(@"\blas dos\b|\bambas\b|\bambos\b|\bmixto\b|de todo");
    private static readonly Regex SedesMasRx = R(@"\bsedes\b|sucursal|dos locales|otro local|otra sede|\bvarias\b|mas de una|\b(2|3|4|dos|tres|cuatro)\b");
    private static readonly Regex SedesStrongRx = R(@"\bsedes\b|sucursal|dos locales|otro local|otra sede");
    private static readonly Regex SedeUnaRx = R(@"\buna\b|\b1\b|solo una|un solo local|uno solo|una sola|\buno\b");

    private static readonly Regex AhoraRx = R(@"^(ahora|ya|dale|si|llamame|cuando quieras|estoy|de una|ok|bueno|listo|joya|obvio)\b|👍");
    private static readonly Regex LuegoRx = R(@"en un rato|mas tarde|despues|ahora no|no puedo|estoy trabajando|estoy ocupad|estoy en clase|estoy dando|en otro momento");
    private static readonly Regex SinLlamadaRx = R(@"por escrito|por mensaje|no puedo hablar|no me gusta hablar|mandame info|pasame el link|lo miro yo|quiero probar|prefiero verlo|por aca no|sin llamada|no quiero llamada");
    private static readonly Regex FranjaRx = R(@"a la (manana|tarde|noche)|al mediodia|\b(manana|lunes|martes|miercoles|jueves|viernes|sabado|domingo)\b|\b\d{1,2}(:\d{2})? ?(hs|h|horas)?\b");

    private static readonly Regex PrecioRx = R(@"precio|cuanto (sale|cuesta|es|cobran|vale|seria)|que sale|\bcosto|\bvalor|tarifa|cuanto\?");
    private static readonly Regex GratisRx = R(@"gratis|gratuito|sin costo|\bfree\b|no se paga|era gratis");
    private static readonly Regex LoVeoRx = R(@"lo veo|lo miro|lo chequeo|ahi veo|lo chusmeo|despues te digo|te aviso|lo pienso|lo analizo|lo voy a ver|lo voy a pensar");
    private static readonly Regex SocioRx = R(@"\bmi soci[oa]\b|con mi soci|mi pareja|mi marido|mi esposa|mi mujer|lo tengo que consultar|tengo que consultarlo");
    private static readonly Regex DecisorRx = R(@"\bel dueno\b|\bla duena\b|mi jefe|mi jefa|no soy (el|la) duen|trabajo aca|soy empleado|soy empleada");
    private static readonly Regex CaroRx = R(@"\bcaro\b|es mucho|no me alcanza|presupuesto|no tengo plata|mucha plata|muy caro");
    private static readonly Regex HuellaRx = R(@"huella|facial|reconocimiento|molinete|lector|biometric");
    private static readonly Regex NoClienteRx = R(@"busco (un )?gimnasio|quiero anotarme|cuanto sale la cuota|quiero entrenar|busco trabajo|\bcv\b|curriculum|\bvendo\b|ofrezco|equivocad|no tengo (un )?gimnasio|no tengo gym|no soy (de un )?gimnasio");
    private static readonly Regex RechazoRx = R(@"no me interesa|^no gracias|no quiero(?! (llamada|que me llames|hablar|por telefono))|no me escriban|^baja$|\bstop\b|no me jodas|dejen de escribir");
    private static readonly Regex PersonaRx = R(@"sos un bot|sos real|es un bot|sos una persona|hablar con (alguien|una persona|un humano|un asesor)");
    private static readonly Regex ComoFuncionaRx = R(@"como funciona|como es|de que se trata|que hace|que es esto|info\b|informacion");

    private static readonly Regex NumRx = new(@"\b(\d{1,4})\b", RegexOptions.Compiled);
    private static readonly Dictionary<string, int> NumWords = new()
    {
        ["diez"] = 10, ["quince"] = 15, ["veinte"] = 20, ["treinta"] = 30, ["cuarenta"] = 40, ["cincuenta"] = 50,
        ["sesenta"] = 60, ["setenta"] = 70, ["ochenta"] = 80, ["noventa"] = 90, ["cien"] = 100, ["ciento"] = 100,
        ["doscientos"] = 200, ["trescientos"] = 300,
    };

    /// <summary>Cantidad de alumnos: "entre 40 y 50" → 50, "unos 20" → 20, "pocos" → 15. null = no dijo.</summary>
    public static int? ParseAlumnos(string norm)
    {
        // Números que no son alumnos: "4 modalidades", "2 veces", "3 dias", "x 3".
        var limpio = Regex.Replace(norm, @"\b\d+\s*(modalidades?|veces|vez|dias?|clases?|canchas?|sedes?|horas?|hs|anos?|meses|profes?|profesores?|turnos?|planes?)\b|\bx\s*\d+", " ");
        var nums = NumRx.Matches(limpio).Select(m => int.Parse(m.Groups[1].Value)).Where(n => n > 0 && n < 5000).ToList();
        if (Regex.IsMatch(limpio, @"\bentre \d+ y \d+") && nums.Count >= 2) return Math.Max(nums[0], nums[1]);
        if (nums.Count > 0) return nums[0];
        foreach (var (w, n) in NumWords) if (Regex.IsMatch(norm, $@"\b{w}\b")) return n;
        if (Regex.IsMatch(norm, @"\b(pocos|poquitos|muy pocos)\b")) return 15;
        return null;
    }

    public static string? ParseTipo(string norm)
    {
        var mixto = MixtoRx.IsMatch(norm) || (ClasesRx.IsMatch(norm) && LibreRx.IsMatch(norm));
        if (mixto) return "ambas";
        if (LibreRx.IsMatch(norm)) return "libre";
        if (ClasesRx.IsMatch(norm)) return "clases";
        return null;
    }

    /// <summary>Cómo lleva las cuotas (paso 1), en el orden del guion. null = no se entiende.</summary>
    public static string? ParseCobro(string norm)
    {
        if (AbreRx.IsMatch(norm)) return "abre";
        if (SistemaRx.IsMatch(norm) && !SistemaNegadoRx.IsMatch(norm)) return "sistema";
        if (ExcelRx.IsMatch(norm)) return "excel";
        if (PapelRx.IsMatch(norm) || SistemaNegadoRx.IsMatch(norm)) return "papel";
        if (MediosRx.IsMatch(norm)) return "papel";
        return null;
    }

    // ── Textos (los del txt aprobado) ─────────────────────────────────────────────────────────────
    private const string NLc = "[NUEVO_MENSAJE]";
    private const string TOpener = "{saludo}! como estas?" + NLc + "soy mateo, de gymhero" + NLc +
                                   "contame, hoy como llevas las cuotas de los alumnos? excel, papel, algun sistema?";
    private const string TCobroRe = "y las cuotas hoy como las vas llevando? en excel, en papel?";
    private const string TComplica = "y que es lo que mas te complica, saber quien te debe o andar cobrando?";
    private const string TDebe = "claro. usando gymhero, el que se atraso te aparece marcado y no tenes que buscarlo en la planilla";
    private const string TCobrar = "claro. usando gymhero le mandas el link de mercado pago y cuando paga se marca como pago automaticamente, sin que tengas que anotarlo a mano";
    private const string TCobrarY = "y le mandas el link de mercado pago y cuando paga se marca como pago automaticamente, sin que tengas que anotarlo a mano";
    private const string TListaExcel = "tendras la lista de alumnos en excel? te la subimos nosotros al sistema sin costo";
    private const string TListaPapel = "tendras la lista de alumnos en papel? le sacas una foto y te la subimos nosotros al sistema sin costo";
    private const string TSistema = "ah, y que te llevo a mirar otras opciones?";
    private const string TAbre = "ah buenisimo, para cuando abris?";
    private const string TAbre2 = "te escribo unos dias antes y te lo dejamos armado, asi el dia que abris ya lo tenes andando";
    private const string TAlumnos = "cuantos alumnos tenes mas o menos? y van a clases con horario, pase libre o ambas?";
    private const string TAlumnosRe = "y alumnos mas o menos cuantos tenes?";
    private const string TAlumnosSolo = "y alumnos mas o menos cuantos?";
    private const string TClases1 = "en ese caso cada alumno reserva su lugar desde el celu y vos ves cuantos van a cada clase, con cupo maximo de alumnos";
    private const string TClases2 = "lo bueno es que cuando llega muestra su QR en la entrada y te aparece en verde si esta al dia o en rojo si debe";
    private const string TLibre1 = "en ese caso cuando llegan muestran su QR desde el celu y te aparece en verde si esta al dia o en rojo si debe";
    private const string TLibre2 = "lo bueno es que las clases con horario tambien se reservan desde el mismo celu";
    private const string TAmbas1 = "en ese caso los de clases reservan su lugar desde el celu y vos ves cuantos van a cada una, con cupo maximo de alumnos";
    private const string TAmbas2 = "y los de pase libre muestran su QR al llegar y te aparece en verde si estan al dia o en rojo si deben";
    private const string TSedes = "y tenes una sede o mas de una?";
    private const string TCaso = "los gimnasios que usan gymhero hoy cobran un 50% mas que cuando arrancaron, imaginate eso en pesos cuanto es";
    private const string TPlanesChico = "con esa cantidad tenes dos opciones:\n" +
        "gratis: hasta 30 alumnos, solo un 5% de las cuotas que te paguen por mercado pago. efectivo y transferencia sin costo. el ingreso con QR es desde el starter\n" +
        "starter $50.000 por mes: sin limite de alumnos y sin comision de gymhero";
    private const string TPlanesGrande = "hay dos planes, por mes:\n" +
        "starter $50.000: alumnos y cuotas, cobro por mercado pago, reservas de clases y acceso con QR\n" +
        "coaching pro $69.000: todo lo del starter + ingreso con huella o reconocimiento facial (el lector va aparte), rutinas para cada alumno y la app con tu marca";
    private const string TPlanesTodos = "hay tres opciones, por mes:\n" +
        "gratis: hasta 30 alumnos, solo un 5% de las cuotas que te paguen por mercado pago. el ingreso con QR es desde el starter\n" +
        "starter $50.000: sin limite de alumnos, cobro por mercado pago, reservas de clases y acceso con QR\n" +
        "coaching pro $69.000: todo lo del starter + ingreso con huella o reconocimiento facial (el lector va aparte), rutinas para cada alumno y la app con tu marca";
    private const string TMultisede = "\nmulti-sede $99.000: todo lo de coaching pro para hasta 3 sedes";
    private const string TPrueba = "cualquiera de los dos lo probas 7 dias gratis, sin poner tarjeta";
    private const string TLlamadaChico = "queres que te llame por aca 5 minutos y te muestro como queda, asi ves cual te conviene? te queda mejor ahora o en un rato?";
    private const string TLlamadaGrande = "queres que te llame por aca 5 minutos y te muestro los dos andando, asi ves cual te sirve? te queda mejor ahora o en un rato?";
    private const string TLlamadaManana = "queres que te llame mañana 5 minutos y te lo muestro andando? te queda mejor a la mañana o a la tarde?";
    private const string TLlamoYa = "{dale|listo}, te llamo en 5";
    private const string TDemoMientras = "mientras tanto te paso la demo para que veas como funciona realmente: " + DemoAdminUrl;
    private const string TDatos = "listo, me queres pasar el nombre del gym y tu mail? asi te armo la cuenta";
    private const string TCuenta1 = "te mande el acceso a tu mail";
    private const string TCuenta2 = "me queres pasar el excel o una foto de la planilla? hoy mismo te cargo los alumnos";
    private const string TLoVeo = "dale! mañana te escribo y me contas que te parecio";
    private const string TSocio = "si, conviene que lo vean juntos. quieren que los llame a los dos 10 minutos? les queda mejor hoy a la tarde o mañana?";
    private const string TDecisor = "dale, gracias! me podrias pasar el numero o el mail de quien se encarga de la gestion del gimnasio? asi le escribo directo";
    private const string TCaroGrande = "te entiendo. arranca con el starter y probalo los 7 dias, no tenes que poner tarjeta";
    private const string TCaroChico = "con el gratis no pagas nada por mes, arrancas con ese y despues ves";
    private const string THuella = "si! funciona con lectores ZKTeco o Hikvision y te asesoramos con el equipo" + NLc +
                                   "ya lo armamos en muchos gimnasios y tenemos varios modelos para recomendarte, o usamos el que ya tengas";
    private const string TAvisos = "si, eso tambien lo hace";
    private const string TGratisGrande = "el gratis es hasta 30 alumnos. para tu cantidad lo probas 7 dias gratis, sin poner tarjeta";
    private const string TNoCliente = "ah, perdon! esto es un sistema para dueños de gimnasios. que andes bien";
    private const string TRechazo = "dale, sin drama. cualquier cosa me escribis";

    // ── Motor ─────────────────────────────────────────────────────────────────────────────────────
    private async Task<OnboardingResult> ProcessCloserAsync(Lead lead, LeadOnboarding ob, string msg, string burst, OnboardingConfig cfg, CancellationToken ct)
    {
        var s = ReadState(ob);
        var n = CNorm(string.IsNullOrWhiteSpace(burst) ? msg : burst);
        var now = DateTimeOffset.UtcNow;
        var parts = new List<string>();
        string? notify = null;

        OnboardingResult Done(string? pending = null, bool offScript = false, bool provisioned = false)
        {
            WriteState(ob, s);
            ob.UpdatedAt = now;
            // Cualquier respuesta del lead reinicia la serie de seguimientos.
            ob.FollowUpCount = 0; ob.LastFollowUpAt = null;
            var reply = parts.Count == 0 ? null : Spin(string.Join(NLc, parts), lead.Id, ob.Step);
            return new OnboardingResult(reply, OffScript: offScript, Provisioned: provisioned, PendingQuestion: pending, Notify: notify);
        }

        // Autorespuesta de otro negocio: no se contesta (y queda marcada para no re-evaluarla en cada tick).
        if (AiSuggestionService.IsAutoResponder(msg))
        {
            lead.AiSuggestedReplyAt = now;
            await _db.SaveChangesAsync(ct);
            return new OnboardingResult(null, OffScript: false, Provisioned: false);
        }

        // ── Arranque: saludo + cómo lleva las cuotas (si ya preguntó el precio, va el precio primero) ──
        if (ob.Step == 0)
        {
            ob.Step = CloserCobro;
            ob.ContactName ??= lead.Name;
            parts.Add(TOpener);
            if (msg.Length > 0 && !LooksLikePrefill(msg) && PrecioRx.IsMatch(n))
            {
                parts.RemoveAt(0);
                parts.Add("{saludo}! como estas?" + NLc + "soy mateo, de gymhero");
                parts.Add(TPlanesTodos);
                parts.Add(TAlumnosSolo);
                s.PlanesMostrados = true;
                ob.Step = CloserAlumnos;
            }
            await _db.SaveChangesAsync(ct);
            return Done();
        }

        if (ob.Step == CloserFin) { lead.AiSuggestedReplyAt = now; await _db.SaveChangesAsync(ct); return new OnboardingResult(null, false, false); }

        // ── Lo que pueden decir en cualquier paso ────────────────────────────────────────────────
        if (RechazoRx.IsMatch(n) && ob.Step != CloserDatos)
        {
            parts.Add(TRechazo); ob.Step = CloserFin; lead.Status = LeadStatus.Lost; lead.ClosedAt ??= now;
            await _db.SaveChangesAsync(ct); return Done();
        }
        if (NoClienteRx.IsMatch(n) && !AbreRx.IsMatch(n))
        {
            parts.Add(TNoCliente); ob.Step = CloserFin; lead.Status = LeadStatus.Lost; lead.ClosedAt ??= now;
            await _db.SaveChangesAsync(ct); return Done();
        }
        if (PersonaRx.IsMatch(n))
        {
            notify = "pregunta si es un bot o pide una persona: contestale vos";
            lead.AiSuggestedReplyAt = now; await _db.SaveChangesAsync(ct); return Done();
        }
        // En la llamada en curso / agendada o con la cuenta ya creada, lo sigue Mateo: el bot solo avisa.
        if (ob.Step is CloserLlamando or CloserAgendada or CloserCuenta)
        {
            notify = ob.Step == CloserCuenta ? $"escribio despues de crear la cuenta: {Short(msg)}" : $"escribio con la llamada pendiente: {Short(msg)}";
            lead.AiSuggestedReplyAt = now; await _db.SaveChangesAsync(ct); return Done();
        }

        var precio = PrecioRx.IsMatch(n);
        var gratis = GratisRx.IsMatch(n);
        if (precio || (gratis && s.Alumnos is null))
        {
            if (s.Alumnos is null)
            {
                parts.Add(TPlanesTodos); parts.Add(TAlumnosSolo); s.PlanesMostrados = true; ob.Step = CloserAlumnos;
                await _db.SaveChangesAsync(ct); return Done();
            }
            AddPlanes(parts, s, await MateoAvailableAsync(cfg, ct)); ob.Step = CloserLlamada;
            await _db.SaveChangesAsync(ct); return Done();
        }
        if (gratis)
        {
            parts.Add(s.Alumnos is > 30 ? TGratisGrande : TPlanesChico.Split('\n')[1]);
            parts.Add(Pending(ob.Step, s));
            await _db.SaveChangesAsync(ct); return Done();
        }
        if (CaroRx.IsMatch(n))
        {
            parts.Add(s.Alumnos is > 30 or null ? TCaroGrande : TCaroChico);
            if (!s.CasoUsado) { parts.Add(TCaso); s.CasoUsado = true; }
            if (ob.Step == CloserSistema)
            {
                parts.Add(Migracion(s)); parts.Add(TAlumnos); ob.Step = CloserAlumnos;
            }
            else if (Pending(ob.Step, s) is { Length: > 0 } pend) parts.Add(pend);
            await _db.SaveChangesAsync(ct); return Done();
        }
        // Pidió ver el sistema: la demo, y sigue donde estaba.
        if (DemoPideRx.IsMatch(n) && ob.Step is not (CloserLlamada or CloserHora or CloserDatos))
        {
            parts.Add("te paso la demo para que veas como funciona realmente: " + DemoAdminUrl);
            if (Pending(ob.Step, s) is { Length: > 0 } pend2) parts.Add(pend2);
            await _db.SaveChangesAsync(ct); return Done();
        }
        // Pasó el mail ya en el paso de la llamada: se le arma la cuenta.
        if (ob.Step is CloserLlamada or CloserHora && EmailRx.IsMatch(burst))
        {
            ob.Step = CloserDatos;
            return await CloserProvisionAsync(lead, ob, s, burst, cfg, parts, ct, Done);
        }
        if (HuellaRx.IsMatch(n)) { parts.Add(THuella); parts.Add(Pending(ob.Step, s)); await _db.SaveChangesAsync(ct); return Done(); }
        if (SocioRx.IsMatch(n))
        {
            parts.Add(TSocio); ob.Step = CloserHora; s.Offered = new();
            await _db.SaveChangesAsync(ct); return Done();
        }
        if (DecisorRx.IsMatch(n) && ob.Step != CloserDecisor)
        {
            parts.Add(TDecisor); ob.Step = CloserDecisor;
            await _db.SaveChangesAsync(ct); return Done();
        }
        if (LoVeoRx.IsMatch(n) && ob.Step is not (CloserCobro or CloserComplica or CloserLista or CloserAlumnos or CloserSedes))
        {
            parts.Add(TLoVeo);
            ob.FollowUpCount = 2; // el próximo seguimiento es el de mañana a las 10
            WriteState(ob, s); ob.UpdatedAt = now; await _db.SaveChangesAsync(ct);
            return new OnboardingResult(Spin(string.Join(NLc, parts), lead.Id, ob.Step), false, false);
        }

        // ── El paso del guion ──────────────────────────────────────────────────────────────────
        switch (ob.Step)
        {
            case CloserCobro:
            {
                var cobro = ParseCobro(n);
                if (cobro is null)
                {
                    if (Regex.IsMatch(n, @"\by vos\b|\by tu\b|como estas|como andas|como va|^bien\b|todo bien") && IntentText.WordCount(IntentText.Normalize(msg)) <= 6)
                    { parts.Add("todo bien!"); parts.Add(TCobroRe); break; } // "bien y vos?": se le contesta y se repite la pregunta
                    if (ComoFuncionaRx.IsMatch(n) || msg.Contains('?'))
                        return Done(pending: TCobroRe, offScript: true);
                    if (LooksLikePrefill(msg) || IntentText.WordCount(IntentText.Normalize(msg)) <= 3)
                    { lead.AiSuggestedReplyAt = now; await _db.SaveChangesAsync(ct); return Done(); } // "hola", el anuncio otra vez: espera la respuesta
                    return Done(pending: TCobroRe, offScript: true);
                }
                s.Cobro = cobro;
                if (cobro == "sistema")
                {
                    s.Sistema = SistemaNombreRx.Match(n) is { Success: true } sm ? sm.Value : null;
                    parts.Add(TSistema); ob.Step = CloserSistema;
                }
                else if (cobro == "abre") { parts.Add(TAbre); ob.Step = CloserAbre; }
                else { parts.Add(TComplica); ob.Step = CloserComplica; }
                break;
            }
            case CloserComplica:
            {
                var debe = DebeRx.IsMatch(n); var cobrar = CobrarRx.IsMatch(n);
                if (DosRx.IsMatch(n)) debe = cobrar = true;
                var lista = s.Cobro == "excel" ? TListaExcel : TListaPapel;
                if (debe && cobrar) { parts.Add(TDebe); parts.Add(TCobrarY); parts.Add(lista); ob.Step = CloserLista; }
                else if (debe) { parts.Add(TDebe); parts.Add(lista); ob.Step = CloserLista; }
                else if (cobrar) { parts.Add(TCobrar); parts.Add(lista); ob.Step = CloserLista; }
                else if (AvisosRx.IsMatch(n)) { parts.Add(TAvisos); parts.Add(lista); ob.Step = CloserLista; }
                else if (NadaRx.IsMatch(n)) { parts.Add("bien ahi"); parts.Add(TAlumnos); ob.Step = CloserAlumnos; }
                else { ob.Step = CloserLista; await _db.SaveChangesAsync(ct); return Done(pending: lista, offScript: true); }
                break;
            }
            case CloserLista:
            {
                if (SedesStrongRx.IsMatch(n)) s.Sedes = 2;
                var a = ParseAlumnos(n); if (a is not null) s.Alumnos = a;
                var t = ParseTipo(n); if (t is not null) s.Tipo = t;
                if (NoRx.IsMatch(n)) parts.Add("no hay drama, la armamos juntos");
                else if (SiRx.IsMatch(n)) parts.Add("genial, despues me la pasas");
                else if (msg.Contains('?')) { await _db.SaveChangesAsync(ct); return Done(pending: TAlumnos, offScript: true); }
                await AfterAlumnosAsync(parts, ob, s, cfg, ct);
                break;
            }
            case CloserSistema:
            {
                // La IA responde lo que cuente; después va la migración y la pregunta de alumnos.
                s.Sistema ??= SistemaNombreRx.Match(n) is { Success: true } sm2 ? sm2.Value : null;
                if (ParseAlumnos(n) is { } a2)
                {
                    s.Alumnos = a2; s.Tipo = ParseTipo(n) ?? s.Tipo;
                    parts.Add(Migracion(s));
                    await AfterAlumnosAsync(parts, ob, s, cfg, ct);
                    break;
                }
                ob.Step = CloserAlumnos;
                await _db.SaveChangesAsync(ct);
                return Done(pending: $"{Migracion(s)}{NLc}{TAlumnos}", offScript: true);
            }
            case CloserAbre:
            {
                var fecha = ParseFecha(n, now);
                s.AbreFecha = fecha;
                parts.Add(TAbre2); parts.Add(TAlumnos); ob.Step = CloserAlumnos;
                break;
            }
            case CloserAlumnos:
            {
                if (SedesStrongRx.IsMatch(n)) s.Sedes = 2;
                var a = ParseAlumnos(n); if (a is not null) s.Alumnos = a;
                var t = ParseTipo(n); if (t is not null) s.Tipo = t;
                if (s.Alumnos is null && !s.AlumnosRepreguntado)
                {
                    if (msg.Contains('?')) { await _db.SaveChangesAsync(ct); return Done(pending: TAlumnosSolo, offScript: true); }
                    s.AlumnosRepreguntado = true;
                    if (t is not null) AddTipo(parts, t);
                    parts.Add(TAlumnosSolo);
                    break;
                }
                await AfterAlumnosAsync(parts, ob, s, cfg, ct, tipoYaDicho: s.AlumnosRepreguntado && t is null);
                break;
            }
            case CloserSedes:
            {
                if (SedesMasRx.IsMatch(n) && !Regex.IsMatch(n, @"^(una|1|solo una|uno)\b")) s.Sedes = 2;
                else if (SedeUnaRx.IsMatch(n)) s.Sedes = 1;
                else if (msg.Contains('?')) { await _db.SaveChangesAsync(ct); return Done(pending: TSedes, offScript: true); }
                else s.Sedes = 1;
                AddPlanes(parts, s, await MateoAvailableAsync(cfg, ct)); ob.Step = CloserLlamada;
                break;
            }
            case CloserLlamada:
            case CloserHora:
            {
                var mateoOn = await MateoAvailableAsync(cfg, ct);
                var pick = PickTime(n, s.Offered, now);
                if (pick is { } when)
                {
                    s.CallAt = when; s.CallReminded = false; s.CallAlerted = false;
                    parts.Add($"listo, te llamo {SayDay(when, now)} a las {SayHour(when)}");
                    ob.Step = CloserAgendada;
                    notify = $"llamada agendada {SayDay(when, now)} a las {SayHour(when)} (hora AR). llamalo por whatsapp desde la linea de gymhero";
                }
                else if (SinLlamadaRx.IsMatch(n)) { parts.Add(TDatos); ob.Step = CloserDatos; }
                else if (ob.Step == CloserLlamada && mateoOn && AhoraRx.IsMatch(n) && !LuegoRx.IsMatch(n))
                {
                    parts.Add(TLlamoYa); ob.Step = CloserLlamando;
                    notify = "dijo que si a la llamada AHORA: llamalo ya por whatsapp desde la linea de gymhero";
                }
                else if (LuegoRx.IsMatch(n) || (!mateoOn && AhoraRx.IsMatch(n)))
                {
                    s.Offered = NextSlots(now, await MateoHoursAsync(cfg, ct));
                    var d = SayDay(s.Offered[0], now);
                    parts.Add($"dale, {(d == "hoy" ? "" : d + " ")}a las {SayHour(s.Offered[0])} o a las {SayHour(s.Offered[1])}?");
                    parts.Add(TDemoMientras);
                    ob.Step = CloserHora;
                }
                else { await _db.SaveChangesAsync(ct); return Done(pending: ob.Step == CloserHora ? null : (mateoOn ? "te queda mejor ahora o en un rato?" : "te queda mejor a la mañana o a la tarde?"), offScript: true); }
                break;
            }
            case CloserDatos:
                return await CloserProvisionAsync(lead, ob, s, burst.Length > 0 ? burst : msg, cfg, parts, ct, Done);
            case CloserDecisor:
            {
                notify = $"paso el contacto de quien gestiona el gimnasio: {Short(msg)}";
                parts.Add("genial, gracias!");
                ob.Step = CloserFin;
                break;
            }
            default:
                return Done(offScript: true);
        }

        await _db.SaveChangesAsync(ct);
        return Done();
    }

    private async Task AfterAlumnosAsync(List<string> parts, LeadOnboarding ob, CloserState s, OnboardingConfig cfg, CancellationToken ct, bool tipoYaDicho = false)
    {
        if (s.Alumnos is null && !s.AlumnosRepreguntado)
        {
            parts.Add(TAlumnos); ob.Step = CloserAlumnos; return;
        }
        if (s.Tipo is not null && !tipoYaDicho) AddTipo(parts, s.Tipo);
        var mateoOn = await MateoAvailableAsync(cfg, ct);
        if (s.PlanesMostrados)
        {
            // Ya vio los planes (preguntó el precio de entrada): no se los repetimos, solo lo que le toca y la llamada.
            var chico = s.Alumnos is <= 30;
            parts.Add(chico ? "con esa cantidad te entra el plan gratis, o el starter si queres sin comision" : TPrueba);
            parts.Add(mateoOn ? (chico ? TLlamadaChico : TLlamadaGrande) : TLlamadaManana);
            ob.Step = CloserLlamada; return;
        }
        if (s.Sedes is null) { parts.Add(TSedes); ob.Step = CloserSedes; return; }
        AddPlanes(parts, s, mateoOn); ob.Step = CloserLlamada;
    }

    private static string Migracion(CloserState s) =>
        $"y los alumnos te los pasamos nosotros {(string.IsNullOrWhiteSpace(s.Sistema) ? "desde el que usas" : "desde " + s.Sistema)}, sin costo";
    private static readonly Regex DemoPideRx = R(@"(pasa|manda|envia)(me)? (el |la )?(link|demo|video)|quiero ver(lo)? (la demo|como funciona|andando)|tenes (una |algun )?(demo|video)");

    private static void AddTipo(List<string> parts, string tipo)
    {
        switch (tipo)
        {
            case "clases": parts.Add(TClases1); parts.Add(TClases2); break;
            case "libre": parts.Add(TLibre1); parts.Add(TLibre2); break;
            default: parts.Add(TAmbas1); parts.Add(TAmbas2); break;
        }
    }

    private static void AddPlanes(List<string> parts, CloserState s, bool mateoOn)
    {
        var chico = s.Alumnos is <= 30;
        var lista = s.Alumnos is null ? TPlanesTodos : chico ? TPlanesChico : TPlanesGrande;
        if (s.Sedes is > 1) lista += TMultisede;
        parts.Add(lista);
        if (s.Alumnos is null) parts.Add("los pagos los probas 7 dias gratis, sin poner tarjeta");
        else if (!chico) parts.Add(TPrueba);
        if (!s.CasoUsado) { parts.Add(TCaso); s.CasoUsado = true; }
        parts.Add(mateoOn ? (chico ? TLlamadaChico : TLlamadaGrande) : TLlamadaManana);
        s.PlanesMostrados = true;
    }

    /// <summary>La pregunta pendiente del paso (para repetirla después de atender otra cosa).</summary>
    private static string Pending(int step, CloserState s) => step switch
    {
        CloserCobro => TCobroRe,
        CloserComplica => TComplica,
        CloserLista => s.Cobro == "excel" ? TListaExcel : TListaPapel,
        CloserSistema => TSistema,
        CloserAbre => "para cuando abris?",
        CloserAlumnos => s.Alumnos is null ? TAlumnosSolo : TAlumnos,
        CloserSedes => TSedes,
        CloserLlamada => "te queda mejor ahora o en un rato?",
        CloserDatos => "me queres pasar el nombre del gym y tu mail?",
        _ => "",
    };

    private async Task<OnboardingResult> CloserProvisionAsync(Lead lead, LeadOnboarding ob, CloserState s, string burst, OnboardingConfig cfg,
        List<string> parts, CancellationToken ct, Func<string?, bool, bool, OnboardingResult> done)
    {
        var em = EmailRx.Match(burst);
        if (em.Success) ob.Email = FixEmailTypos(em.Value.Trim());
        else if (burst.Contains('@')) { parts.Add("me lo pasas completo, con el @ y lo que sigue?"); await _db.SaveChangesAsync(ct); return done(null, false, false); }

        var resto = em.Success ? burst.Replace(em.Value, " ") : burst;
        resto = Regex.Replace(resto, @"\b(mail|email|correo|nombre|el gym|gimnasio se llama|se llama|es|mi)\b[:]?", " ", RegexOptions.IgnoreCase);
        resto = Regex.Replace(resto, @"\s+", " ").Trim(' ', ',', '.', '-', ':', '\n');
        var words = resto.Length == 0 ? 0 : resto.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
        if (resto.Length > 0 && words <= 6 && !resto.Contains('?') && !IsBusinessNameSuspicious(resto) && !LooksLikePrefill(resto))
            s.NombreGym = resto;

        if (ob.Email is null) { parts.Add(s.NombreGym is null ? "me queres pasar el nombre del gym y tu mail?" : "y tu mail?"); await _db.SaveChangesAsync(ct); return done(null, false, false); }
        if (s.NombreGym is null) { parts.Add("y el nombre del gym?"); await _db.SaveChangesAsync(ct); return done(null, false, false); }

        ob.GymName = s.NombreGym;
        var url = DryRunProvision ? "https://simulado.invalid/acceso" : await _provision.RegisterAsync(cfg.ProvisionUrl, cfg.ProvisionNameField,
            PresentableBusinessName(ob.GymName, null), ob.Email!, ob.ContactName, cfg.ProductKey, ct, null, lead);
        if (string.IsNullOrWhiteSpace(url))
        {
            parts.Add("uh, no pude crear la cuenta con ese mail. me lo confirmas escribiendolo de nuevo?");
            ob.Email = null;
            await _db.SaveChangesAsync(ct);
            return new OnboardingResult(Spin(string.Join(NLc, parts), lead.Id, ob.Step), false, false,
                Notify: "fallo el alta de la cuenta con el mail que paso: revisalo");
        }
        var now = DateTimeOffset.UtcNow;
        ob.AccessUrl = url; ob.ProvisionedAt = now; ob.Step = CloserCuenta;
        // El guion closer hace su propio acompañamiento: los mensajes post-alta del guion viejo no salen.
        ob.FirstStepsOfferedAt = now; ob.FirstStepsSentAt = now; ob.CheckinSentAt = now; ob.DiscountNudgeSentAt = now;
        lead.Status = LeadStatus.Closed; lead.ClosedAt ??= now;
        var mailed = DryRunProvision || await SendAccessEmailAsync(ob.Email!, lead.Product?.DisplayName ?? "GymHero", url, ct);
        if (!mailed) await _alerter.AlertAsync($"fallo el mail con el acceso de GymHero para {ob.Email} (lead: {lead.Name}). reenvialo a mano: {url}", ct);
        parts.Add(mailed ? TCuenta1 : "en un rato te llega el acceso a tu mail");
        parts.Add(TCuenta2);
        WriteState(ob, s); ob.UpdatedAt = now;
        await _db.SaveChangesAsync(ct);
        _log.LogInformation("Closer GymHero: cuenta creada lead={Lead}", lead.Id);
        return new OnboardingResult(Spin(string.Join(NLc, parts), lead.Id, ob.Step), false, Provisioned: true,
            Notify: $"creo la cuenta ({ob.GymName}). pedile la planilla y cargale los alumnos");
    }

    // ── Seguimientos (los corre el worker; siguen aunque Mateo haya tomado el chat) ─────────────────
    /// <summary>
    /// Qué mandarle a un lead del guion closer que se quedó callado. lastInboundAt = su último mensaje,
    /// lastOutboundAt = el nuestro (bot o Mateo). Devuelve (texto, aviso a Mateo) o null si no toca nada.
    /// </summary>
    public async Task<(string? Text, string? Notify)?> CloserFollowUpAsync(Lead lead, LeadOnboarding ob, OnboardingConfig cfg,
        DateTimeOffset lastInboundAt, DateTimeOffset lastOutboundAt, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var ar = TimeZoneInfo.ConvertTime(now, BotLearningService.ArTz);
        var s = ReadState(ob);
        if (ob.Step is CloserFin or CloserCuenta || lead.Status is LeadStatus.Lost) return null;

        // Llamada agendada: recordatorio al lead 1 h antes y aviso a Mateo 10 min antes.
        if (s.CallAt is { } call && call > now)
        {
            if (!s.CallReminded && now >= call.AddHours(-1))
            {
                s.CallReminded = true; WriteState(ob, s); await _db.SaveChangesAsync(ct);
                return ($"buenas! a las {SayHour(call)} te llamo por aca", null);
            }
            if (!s.CallAlerted && now >= call.AddMinutes(-10))
            {
                s.CallAlerted = true; WriteState(ob, s); await _db.SaveChangesAsync(ct);
                return (null, $"en 10 minutos tenes que llamarlo (a las {SayHour(call)})");
            }
            return null;
        }

        // Fecha de apertura: 5 días antes.
        if (s.AbreFecha is { } abre && !s.AbreAvisado && now >= abre.AddDays(-5) && ar.Hour is >= 9 and < 21)
        {
            s.AbreAvisado = true; WriteState(ob, s); await _db.SaveChangesAsync(ct);
            return ($"{Hi(ob, "buenas")}! como va? ya estas por abrir? avisame si tenes 10 min y te llamo asi te lo dejamos armado", null);
        }

        if (ob.Step is CloserLlamando or CloserAgendada) return null; // ya se acordó la llamada: la sigue Mateo
        if (lastInboundAt > lastOutboundAt) return null; // el último es del lead: lo contesta el flujo normal
        if (ar.Hour is < 9 or >= 22) return null;
        var anchor = new[] { lastInboundAt, s.CallAt ?? DateTimeOffset.MinValue }.Max();
        var i = ob.FollowUpCount;
        var arAnchor = TimeZoneInfo.ConvertTime(anchor, BotLearningService.ArTz);
        DateTimeOffset At(int days, int hour)
        {
            var d = arAnchor.Date.AddDays(days).AddHours(hour);
            return new DateTimeOffset(d, BotLearningService.ArTz.GetUtcOffset(d));
        }
        string? text = null;
        switch (i)
        {
            case 0:
                if (now < lastOutboundAt.AddMinutes(20)) return null;
                if (now > lastOutboundAt.AddHours(2) || ob.Step is CloserLlamando or CloserAgendada or CloserHora) { ob.FollowUpCount = 1; await _db.SaveChangesAsync(ct); return null; }
                text = ob.Step switch
                {
                    CloserCobro => TCobroRe,
                    CloserAlumnos or CloserLista => TAlumnosRe,
                    CloserSedes => "y sedes tenes una sola o mas?",
                    CloserLlamada => "te llamo ahora o mas tarde?",
                    CloserComplica => "que te complica mas, saber quien te debe o andar cobrando?",
                    _ => null,
                };
                if (text is null) { ob.FollowUpCount = 1; await _db.SaveChangesAsync(ct); return null; }
                break;
            case 1:
                if (now < lastOutboundAt.AddHours(3)) return null;
                text = "buenas! avisame si tenes 10 min y te llamo asi te muestro";
                break;
            case 2:
                if (now < At(1, 10) || now < lastOutboundAt.AddHours(12)) return null;
                text = $"buen dia{Name(ob, " ")}! hoy tengo un rato para cargarte los alumnos, avisame y lo armamos";
                break;
            case 3:
                if (now < anchor.AddDays(3)) return null;
                text = $"{Hi(ob, "buenas")}, como va? cuando tengas 10 min avisame y te llamo";
                break;
            case 4:
                if (now < At(5, 9) || ar.Hour >= 11) return null; // "hoy tengo un hueco a la mañana": solo de 9 a 11
                text = "buenas! hoy tengo un hueco a la mañana, si tenes 10 min avisame y te muestro como te quedaria";
                break;
            case 5:
                if (now < anchor.AddDays(8)) return null;
                text = $"{Hi(ob, "buenas")}! cuando puedas avisame y lo vemos en 10 min asi lo dejas andando";
                break;
            default:
                return null;
        }
        ob.FollowUpCount = i + 1; ob.LastFollowUpAt = now; ob.UpdatedAt = now;
        await _db.SaveChangesAsync(ct);
        return (text, null);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────────────────────────
    private static readonly Regex PrefillRx = R(@"^hola!? (tengo|quiero|me gustaria|vengo)|quiero conocer gymhero|me gustaria activar|quiero contratar gymhero|quiero ver como gymhero|quiero manejar todo");
    private static bool LooksLikePrefill(string msg) => PrefillRx.IsMatch(CNorm(msg));
    private static string Short(string m) => m.Length > 90 ? m[..90] + "…" : m;

    /// <summary>Nombre de pila del contacto (su nombre de WhatsApp), sin emojis; "" si no es un nombre.</summary>
    private static string ContactFirst(LeadOnboarding ob)
    {
        var f = MessageRenderer.FirstName(ob.ContactName);
        f = new string(f.Where(char.IsLetter).ToArray());
        return f.Length < 2 ? "" : char.ToLower(f[0]) + f[1..].ToLower();
    }
    private static string Name(LeadOnboarding ob, string sep) { var f = ContactFirst(ob); return f.Length == 0 ? "" : sep + f; }
    /// <summary>"{nombre}" al arranque del mensaje, o el saludo si no hay nombre.</summary>
    private static string Hi(LeadOnboarding ob, string fallback) { var f = ContactFirst(ob); return f.Length == 0 ? fallback : f; }

    private async Task<(int Start, int End)> MateoHoursAsync(OnboardingConfig cfg, CancellationToken ct)
    {
        if (cfg.HandoffSellerId is null) return (9, 21);
        var h = await _db.Sellers.AsNoTracking().Where(x => x.Id == cfg.HandoffSellerId)
            .Select(x => new { x.ActiveHoursStart, x.ActiveHoursEnd }).FirstOrDefaultAsync(ct);
        return h is null ? (9, 21) : (h.ActiveHoursStart, h.ActiveHoursEnd);
    }
    private async Task<bool> MateoAvailableAsync(OnboardingConfig cfg, CancellationToken ct)
    {
        var (start, end) = await MateoHoursAsync(cfg, ct);
        var h = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, BotLearningService.ArTz).Hour;
        return h >= start && h < end;
    }

    /// <summary>Dos horarios para la llamada: la próxima hora en punto con al menos 1 h de margen y la siguiente; si no entran hoy, mañana.</summary>
    public static List<DateTimeOffset> NextSlots(DateTimeOffset now, (int Start, int End) hours)
    {
        var tz = BotLearningService.ArTz;
        var ar = TimeZoneInfo.ConvertTime(now.AddMinutes(60), tz);
        var first = ar.Date.AddHours(ar.Minute == 0 ? ar.Hour : ar.Hour + 1);
        if (first.Hour + 1 >= hours.End || first.Hour < hours.Start)
            first = (first.Hour < hours.Start ? first.Date : first.Date.AddDays(1)).AddHours(hours.Start + 1);
        DateTimeOffset T(DateTime d) => new(d, tz.GetUtcOffset(d));
        return new List<DateTimeOffset> { T(first), T(first.AddHours(1)) };
    }

    private static readonly string[] Dias = { "domingo", "lunes", "martes", "miercoles", "jueves", "viernes", "sabado" };

    /// <summary>
    /// El horario que eligió: "la primera"/"la segunda", una hora ("a las 18", "18hs", "tipo 7" = 19), una franja
    /// ("a la tarde" = 17) y el día ("mañana", "el lunes"). null = no dijo cuándo.
    /// </summary>
    public static DateTimeOffset? PickTime(string norm, List<DateTimeOffset> offered, DateTimeOffset now)
    {
        var tz = BotLearningService.ArTz;
        if (offered.Count >= 2)
        {
            if (Regex.IsMatch(norm, @"\b(la )?primera\b|\bel primero\b")) return offered[0];
            if (Regex.IsMatch(norm, @"\b(la )?segunda\b|\bel segundo\b")) return offered[1];
        }
        var ar = TimeZoneInfo.ConvertTime(now, tz);
        var day = ar.Date;
        var dayGiven = false;
        if (Regex.IsMatch(norm, @"\bmanana\b") && !Regex.IsMatch(norm, @"a la manana") || Regex.IsMatch(norm, @"manana a la")) { day = day.AddDays(1); dayGiven = true; }
        for (var i = 0; i < 7; i++)
            if (Regex.IsMatch(norm, $@"\b{Dias[i]}\b"))
            {
                var delta = ((i - (int)ar.DayOfWeek) + 7) % 7; if (delta == 0) delta = 7;
                day = ar.Date.AddDays(delta); dayGiven = true;
            }
        int? hour = null; var minute = 0;
        var hm = Regex.Match(norm, @"(?:a las|alas|tipo|las|desde las|desde|a eso de las|de)\s*(\d{1,2})(?:[:.](\d{2}))?\b|\b(\d{1,2})(?:[:.](\d{2}))?\s*(?:hs|h|horas)\b");
        if (hm.Success)
        {
            hour = int.Parse(hm.Groups[1].Success ? hm.Groups[1].Value : hm.Groups[3].Value);
            var mm = hm.Groups[2].Success ? hm.Groups[2].Value : hm.Groups[4].Value;
            if (!string.IsNullOrEmpty(mm)) minute = int.Parse(mm);
            if (hour < 8) hour += 12;
        }
        else if (offered.Count >= 2)
        {
            foreach (var o in offered)
            {
                var oh = TimeZoneInfo.ConvertTime(o, tz).Hour;
                if (Regex.IsMatch(norm, $@"\b{oh}\b")) return o;
            }
        }
        if (hour is null)
        {
            if (Regex.IsMatch(norm, @"a la manana")) hour = 10;
            else if (Regex.IsMatch(norm, @"al mediodia")) hour = 13;
            else if (Regex.IsMatch(norm, @"a la tarde")) hour = 17;
            else if (Regex.IsMatch(norm, @"a la noche")) hour = 20;
        }
        if (hour is null or > 23) return null;
        var when = day.AddHours(hour.Value).AddMinutes(minute);
        if (!dayGiven && when <= ar.DateTime.AddMinutes(15)) when = when.AddDays(1);
        return new DateTimeOffset(when, tz.GetUtcOffset(when));
    }

    /// <summary>Fecha de apertura ("en diciembre", "el mes que viene", "15/12", "en dos semanas"); null = no dijo.</summary>
    public static DateTimeOffset? ParseFecha(string norm, DateTimeOffset now)
    {
        string[] meses = { "enero", "febrero", "marzo", "abril", "mayo", "junio", "julio", "agosto", "septiembre", "octubre", "noviembre", "diciembre" };
        var ar = TimeZoneInfo.ConvertTime(now, BotLearningService.ArTz).Date;
        var dm = Regex.Match(norm, @"\b(\d{1,2})/(\d{1,2})\b");
        DateTime? d = null;
        if (dm.Success && int.TryParse(dm.Groups[1].Value, out var dd) && int.TryParse(dm.Groups[2].Value, out var mo) && mo is >= 1 and <= 12 && dd is >= 1 and <= 31)
        {
            var y = ar.Year; try { d = new DateTime(y, mo, Math.Min(dd, DateTime.DaysInMonth(y, mo))); if (d < ar) d = d.Value.AddYears(1); } catch { }
        }
        for (var i = 0; d is null && i < 12; i++)
            if (Regex.IsMatch(norm, $@"\b{meses[i]}\b"))
            {
                var y = ar.Month - 1 > i ? ar.Year + 1 : ar.Year;
                d = new DateTime(y, i + 1, 1);
            }
        if (d is null && Regex.IsMatch(norm, @"mes que viene|proximo mes")) d = new DateTime(ar.Year, ar.Month, 1).AddMonths(1);
        var w = Regex.Match(norm, @"en (\d+|una|dos|tres|cuatro) semana");
        if (d is null && w.Success)
        {
            var k = w.Groups[1].Value switch { "una" => 1, "dos" => 2, "tres" => 3, "cuatro" => 4, var x => int.Parse(x) };
            d = ar.AddDays(7 * k);
        }
        if (d is null) return null;
        return new DateTimeOffset(d.Value, BotLearningService.ArTz.GetUtcOffset(d.Value));
    }

    private static string SayHour(DateTimeOffset t)
    {
        var ar = TimeZoneInfo.ConvertTime(t, BotLearningService.ArTz);
        return ar.Minute == 0 ? ar.Hour.ToString() : $"{ar.Hour}:{ar.Minute:00}";
    }
    private static string SayDay(DateTimeOffset t, DateTimeOffset now)
    {
        var tz = BotLearningService.ArTz;
        var a = TimeZoneInfo.ConvertTime(t, tz).Date; var b = TimeZoneInfo.ConvertTime(now, tz).Date;
        if (a == b) return "hoy";
        if (a == b.AddDays(1)) return "mañana";
        return "el " + Dias[(int)a.DayOfWeek];
    }
}
