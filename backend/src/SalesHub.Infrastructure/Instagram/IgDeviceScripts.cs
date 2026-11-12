namespace SalesHub.Infrastructure.Instagram;

/// <summary>
/// Las indicaciones que SalesHub le manda al celular para Instagram. El APK no sabe nada de
/// Instagram: ejecuta una lista de pasos genéricos (abrir un link, esperar que aparezca algo,
/// tocar, tipear, juntar textos de una lista) y reporta cómo terminó. Todo lo que depende de
/// cómo se ve la app de Instagram vive ACÁ, así que cuando cambia un botón se corrige con un
/// deploy del backend y no hay que publicar otra versión de la app del celular.
///
/// Pasos (campo "op"):
///   wake                                  prende la pantalla y saca el bloqueo
///   open    url, pkg                      abre un link https de Instagram dentro de la app
///   focus   rx, tries, error              espera que la ventana activa sea la app
///   wait    any[], fail[{sel,error}], timeoutMs, error
///   tap     sel[], optional, error        toca el primer selector que aparezca
///   type    text                          tipea con ritmo humano
///   sleep   min, max                      pausa al azar (ms)
///   swipe   dir (up|down), frac           desliza una fracción de la pantalla
///   expect  any[], none[], fail[], timeoutMs, error
///   collect rx[], swipes, max, error      junta el grupo 1 de la regex bajando por una lista
///   if      any[], then[], else[]
///   home                                  vuelve al inicio del celular
///
/// Un selector es una o más regex unidas con " && " que tienen que aparecer TODAS en el mismo
/// nodo de la pantalla (el volcado de uiautomator: text="…", resource-id="…", content-desc="…").
/// Los textos van en español y en inglés porque dependen del idioma del teléfono.
/// </summary>
public static class IgDeviceScripts
{
    public const string Package = "com.instagram.android";
    private const string Pkg = @"package=""com\.instagram\.android""";

    // ── Selectores ─────────────────────────────────────────────────────────────────────────

    private static readonly string[] MessageButton =
    {
        $@"text=""(Enviar mensaje|Mensaje|Message)"" && {Pkg}",
        $@"content-desc=""(Enviar mensaje|Mensaje|Message)"" && {Pkg}",
    };

    /// <summary>Botones que solo aparecen en un perfil ajeno.</summary>
    private static readonly string[] FollowButton =
    {
        $@"text=""(Seguir|Follow|Seguir tambi.n|Follow back|Follow Back|Siguiendo|Following|Solicitado|Requested)"" && {Pkg}",
    };

    /// <summary>Solo aparece en el perfil de la cuenta que está logueada en el celular.</summary>
    private static readonly string[] EditProfile =
    {
        $@"text=""(Editar perfil|Edit profile|Edit Profile)"" && {Pkg}",
    };

    private static readonly string[] Composer =
    {
        @"resource-id=""com\.instagram\.android:id/row_thread_composer_edittext""",
        $@"class=""android\.widget\.EditText"" && {Pkg}",
    };

    private static readonly string[] SendButton =
    {
        @"resource-id=""com\.instagram\.android:id/row_thread_composer_(button_send|send_button[a-z_]*)""",
        $@"content-desc=""(Enviar|Send)"" && {Pkg}",
        $@"text=""(Enviar|Send)"" && {Pkg}",
    };

    /// <summary>Carteles de Instagram cuando frena la cuenta. El celular reporta "blocked" y la app se pausa.</summary>
    private static readonly string[] Blocked =
    {
        @"text=""[^""]*(Int.ntalo de nuevo m.s tarde|Try again later|Try Again Later|Acci.n bloqueada|Action Blocked|restringimos|We restrict|limitamos la frecuencia|We limit how often)[^""]*""",
    };

    private static readonly string[] NotFound =
    {
        @"text=""[^""]*(no est. disponible|isn.t available|Usuario no encontrado|User not found|No se encontr. el usuario)[^""]*""",
    };

    private static readonly string[] CantMessage =
    {
        @"text=""[^""]*(o puedes enviar mensajes|o se pueden enviar mensajes|can.t message|no acepta mensajes|not accepting messages)[^""]*""",
    };

    private static readonly string[] LoggedOut =
    {
        $@"text=""(Iniciar sesi.n|Log in|Log In|Crear cuenta nueva|Create new account)"" && {Pkg}",
    };

    /// <summary>Carteles que Instagram pone encima al abrir (notificaciones, guardar el login).</summary>
    private static readonly string[] Dismiss =
    {
        $@"text=""(Ahora no|Not now|Not Now|Omitir|Skip)"" && {Pkg}",
    };

    private static readonly string[] FollowersCounter =
    {
        @"resource-id=""com\.instagram\.android:id/row_profile_header_followers_container""",
        @"resource-id=""com\.instagram\.android:id/profile_header_followers_stacked_familiar""",
        $@"text=""(seguidores|followers|Seguidores|Followers)"" && {Pkg}",
        $@"content-desc=""[^""]*(seguidores|followers)[^""]*"" && {Pkg}",
    };

    /// <summary>Señales de que la lista de seguidores ya abrió.</summary>
    private static readonly string[] FollowerRow =
    {
        @"resource-id=""com\.instagram\.android:id/follow_list_username""",
        @"resource-id=""com\.instagram\.android:id/follow_list_container""",
        $@"text=""(Eliminar|Remove)"" && {Pkg}",
    };

    /// <summary>El @usuario de cada fila de la lista (grupo 1). Se usa la primera regex que encuentre algo.</summary>
    private static readonly string[] FollowerUsername =
    {
        @"text=""([a-z0-9._]{1,30})"" resource-id=""com\.instagram\.android:id/follow_list_username""",
        @"text=""([a-z0-9._]{1,30})"" resource-id=""com\.instagram\.android:id/[a-z_]*username[a-z_]*""",
    };

    // ── Guiones ────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Mandar un DM: entra al perfil, lo mira un momento, abre el chat, tipea y envía, y confirma
    /// que salió (el botón de enviar desaparece). Sin confirmación reporta send_not_confirmed y
    /// no se reintenta: mejor perder uno que escribirle dos veces a la misma persona.
    /// </summary>
    public static List<Dictionary<string, object?>> Dm(string handle, string text) => new()
    {
        Step("wake"),
        Step("open", ("url", ProfileUrl(handle)), ("pkg", Package)),
        Step("focus", ("rx", @"com\.instagram\.android"), ("tries", 8), ("error", "instagram_no_abrio")),
        Step("tap", ("sel", Dismiss), ("optional", true)),
        Step("wait",
            ("any", Any(MessageButton, FollowButton)),
            ("fail", Fails((NotFound, "perfil_no_existe"), (Blocked, "blocked"), (LoggedOut, "sin_sesion"), (EditProfile, "es_la_cuenta_propia"))),
            ("timeoutMs", 15000), ("error", "perfil_no_cargo")),
        // Mirar el perfil como lo haría una persona antes de escribir.
        Step("sleep", ("min", 1500), ("max", 4000)),
        Step("swipe", ("dir", "up"), ("frac", 0.3)),
        Step("sleep", ("min", 900), ("max", 2400)),
        Step("swipe", ("dir", "down"), ("frac", 0.3)),
        Step("sleep", ("min", 600), ("max", 1500)),
        // Algunos perfiles esconden "Mensaje": ahí se abre el chat con el link directo.
        Step("if", ("any", MessageButton),
            ("then", new[] { Step("tap", ("sel", MessageButton), ("error", "boton_mensaje_no_encontrado")) }),
            ("else", new[] { Step("open", ("url", $"https://ig.me/m/{handle}"), ("pkg", Package)) })),
        Step("wait",
            ("any", Composer),
            ("fail", Fails((Blocked, "blocked"), (CantMessage, "no_acepta_mensajes"))),
            ("timeoutMs", 15000), ("error", "chat_no_abrio")),
        Step("sleep", ("min", 1000), ("max", 3000)),
        Step("tap", ("sel", Composer), ("error", "cajon_no_encontrado")),
        Step("type", ("text", text)),
        Step("sleep", ("min", 700), ("max", 2000)),
        Step("tap", ("sel", SendButton), ("error", "boton_enviar_no_encontrado")),
        Step("sleep", ("min", 1200), ("max", 2500)),
        Step("expect", ("none", SendButton), ("fail", Fails((Blocked, "blocked"))), ("timeoutMs", 6000), ("error", "send_not_confirmed")),
        Step("sleep", ("min", 1500), ("max", 3500)),
        Step("home"),
    };

    /// <summary>
    /// Confirmar que el Instagram del celular está en la cuenta de la app: en el perfil propio
    /// aparece "Editar perfil"; en cualquier otro, "Seguir" o "Mensaje".
    /// </summary>
    public static List<Dictionary<string, object?>> Verify(string account)
    {
        var steps = OpenOwnProfile(account);
        steps.Add(Step("sleep", ("min", 1000), ("max", 2500)));
        steps.Add(Step("home"));
        return steps;
    }

    /// <summary>
    /// Leer la lista de seguidores de la cuenta: entra al perfil propio, abre "seguidores" y va
    /// bajando <paramref name="swipes"/> pantallas juntando los @usuarios. Los de arriba son los
    /// más nuevos.
    /// </summary>
    public static List<Dictionary<string, object?>> Followers(string account, int swipes, int max)
    {
        var steps = OpenOwnProfile(account);
        steps.Add(Step("sleep", ("min", 1200), ("max", 3000)));
        steps.Add(Step("tap", ("sel", FollowersCounter), ("error", "contador_de_seguidores_no_encontrado")));
        steps.Add(Step("wait", ("any", FollowerRow), ("timeoutMs", 15000), ("error", "lista_de_seguidores_no_abrio")));
        steps.Add(Step("sleep", ("min", 1000), ("max", 2500)));
        steps.Add(Step("collect", ("rx", FollowerUsername), ("swipes", swipes), ("max", max), ("error", "no_pude_leer_la_lista")));
        steps.Add(Step("sleep", ("min", 800), ("max", 2000)));
        steps.Add(Step("home"));
        return steps;
    }

    private static List<Dictionary<string, object?>> OpenOwnProfile(string account) => new()
    {
        Step("wake"),
        Step("open", ("url", ProfileUrl(account)), ("pkg", Package)),
        Step("focus", ("rx", @"com\.instagram\.android"), ("tries", 8), ("error", "instagram_no_abrio")),
        Step("tap", ("sel", Dismiss), ("optional", true)),
        Step("wait",
            ("any", EditProfile),
            ("fail", Fails((LoggedOut, "sin_sesion"), (MessageButton, "cuenta_equivocada"), (FollowButton, "cuenta_equivocada"), (NotFound, "cuenta_equivocada"))),
            ("timeoutMs", 15000), ("error", "perfil_no_cargo")),
    };

    // ── Armado ─────────────────────────────────────────────────────────────────────────────

    private static string ProfileUrl(string handle) => $"https://www.instagram.com/{handle}/";

    private static Dictionary<string, object?> Step(string op, params (string Key, object? Value)[] args)
    {
        var d = new Dictionary<string, object?> { ["op"] = op };
        foreach (var (k, v) in args) d[k] = v;
        return d;
    }

    private static string[] Any(params string[][] groups) => groups.SelectMany(g => g).ToArray();

    private static List<Dictionary<string, object?>> Fails(params (string[] Sels, string Error)[] groups) =>
        groups.SelectMany(g => g.Sels.Select(sel => new Dictionary<string, object?> { ["sel"] = sel, ["error"] = g.Error })).ToList();
}
