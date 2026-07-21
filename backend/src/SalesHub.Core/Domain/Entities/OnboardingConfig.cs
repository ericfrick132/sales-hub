namespace SalesHub.Core.Domain.Entities;

/// <summary>
/// Config del onboarding de ads POR APP (multi-app). El motor de onboarding es genérico y lee
/// esto: cada producto define su intro, sus preguntas, su endpoint de provisión y su mensaje de
/// éxito. Si <see cref="Enabled"/> es false, los ad leads de esa app NO arrancan el bot.
/// </summary>
public class OnboardingConfig
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>ProductKey de la app (gymhero, turnospro, …).</summary>
    public string ProductKey { get; set; } = string.Empty;

    public bool Enabled { get; set; }

    /// <summary>
    /// true = autoservicio: al final pide el mail y crea la cuenta (gymhero, bunker, unistock).
    /// false = venta asistida: termina con <see cref="ClosingMessage"/> y deriva a demo (turnospro,
    /// playcrew, archicloud) — NO pide mail ni provisiona, marca el lead Interested.
    /// </summary>
    public bool SelfServe { get; set; } = true;

    /// <summary>Si hay audios cargados para esta app, mandar una nota de voz (variante al azar) en el pitch.</summary>
    public bool UsePitchAudio { get; set; }

    /// <summary>
    /// Espera (humana) antes de cada respuesta del bot: random entre Min y Max segundos desde el
    /// mensaje del lead. 0/0 = instantáneo. Ej: 120/3600 = entre 2 min y 1 hora (evita el "bot que
    /// contesta al toque" y baja la detección de Meta).
    /// </summary>
    public int ReplyDelayMinSec { get; set; }
    public int ReplyDelayMaxSec { get; set; }

    /// <summary>Saludo inicial (puede tener [NUEVO_MENSAJE] para splittear). Ej: "buenas! soy Eric de GymHero."</summary>
    public string Intro { get; set; } = string.Empty;

    /// <summary>
    /// Si la app tiene varios perfiles de cliente que entran por el mismo anuncio (ver
    /// <see cref="OnboardingPersona"/>), esta es la pregunta que el bot hace DESPUÉS del intro para
    /// saber con quién habla (ej. "¿tenés canchas que querés administrar, o sos jugador?"). Si está
    /// vacío → la app es de una sola persona y se usan las <see cref="Questions"/> de abajo (legacy).
    /// </summary>
    public string PersonaQuestion { get; set; } = string.Empty;

    /// <summary>Preguntas del alta, en orden. La PRIMERA es el nombre del negocio.</summary>
    public List<string> Questions { get; set; } = new();

    /// <summary>Mensaje que pide el mail antes de crear la cuenta.</summary>
    public string EmailPrompt { get; set; } = string.Empty;

    /// <summary>
    /// Reemplazo del <see cref="Intro"/> para leads de reenganche (Source=ProductReengage): ya
    /// recibieron el opener que SE PRESENTA y promete "te paso los precios" — acá el bot NO se
    /// re-presenta: reacciona y cumple la promesa (precios/link; soporta placeholders {price},
    /// {checkout_url}, {seller}…). Vacío = comportamiento anterior (Intro completo).
    /// </summary>
    public string ReengageIntro { get; set; } = string.Empty;

    /// <summary>
    /// Preguntas del alta para reenganchados. El opener ya hizo la pregunta calificadora, así que
    /// esta lista suele ser más corta (nombre del negocio → situación actual). Vacío = Questions.
    /// </summary>
    public List<string> ReengageQuestions { get; set; } = new();

    /// <summary>
    /// Adjuntos a mandar junto con el ReengageIntro (video demo, imagen de precios) — el "te paso
    /// un video" de la conversación real. Vacío = solo texto.
    /// </summary>
    public List<Guid> ReengageMediaAssetIds { get; set; } = new();

    /// <summary>
    /// Caption de cada adjunto, alineado por índice con <see cref="ReengageMediaAssetIds"/>
    /// ("" = sin caption). Patrón: la imagen de precios lleva la URL de la página como caption.
    /// </summary>
    public List<string> ReengageMediaCaptions { get; set; } = new();

    /// <summary>Endpoint de provisión (bot-register) de la app.</summary>
    public string ProvisionUrl { get; set; } = string.Empty;

    /// <summary>Nombre del campo del body al que mapea el nombre del negocio (ej. "gymName", "businessName").</summary>
    public string ProvisionNameField { get; set; } = "name";

    /// <summary>Mensaje de éxito autoservicio (puede tener [NUEVO_MENSAJE] y el placeholder {accessUrl}).</summary>
    public string SuccessMessage { get; set; } = string.Empty;

    /// <summary>Cierre para venta asistida (SelfServe=false): pitch + handoff a demo. Sin mail ni provisión.</summary>
    public string ClosingMessage { get; set; } = string.Empty;

    /// <summary>
    /// Check-in post-alta ("y pudiste entrar? como te fue?"). Vacío = usa el default del motor.
    /// Soporta spins y placeholders. Se manda UNA vez, ~20h después de provisionar, solo si el
    /// lead no escribió nada desde el alta.
    /// </summary>
    public string PostSignupCheckin { get; set; } = string.Empty;

    /// <summary>
    /// Oferta de cierre anticipado de prueba ("activá hoy y te llevás X% off"). VACÍO = NO se
    /// manda (no inventamos descuentos). Una vez, ~96h post-alta, mismo criterio de silencio.
    /// </summary>
    public string TrialDiscountNudge { get; set; } = string.Empty;

    /// <summary>
    /// "Primeros pasos" de la app (bullet list corta). El SuccessMessage cierra con "avisame
    /// cuando estas adentro y te paso los primeros pasos" — cuando el lead responde CUALQUIER
    /// cosa despues del alta, el bot manda esto UNA vez (marca LeadOnboarding.FirstStepsSentAt).
    /// Vacio = no se manda (la IA sigue sola). Soporta [NUEVO_MENSAJE] y placeholders.
    /// </summary>
    public string FirstStepsMessage { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
