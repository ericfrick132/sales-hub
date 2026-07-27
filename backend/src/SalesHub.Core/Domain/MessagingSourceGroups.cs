using SalesHub.Core.Domain.Enums;

namespace SalesHub.Core.Domain;

/// <summary>
/// Agrupa los <see cref="LeadSource"/> en "orígenes" con los que razona la política de
/// mensajería (<c>MessagingPolicy</c>). La UI y el bot maestro hablan de estos grupos,
/// no del enum crudo: "Meta Lead Ads", "prospección fría", etc.
///
/// Sumar un LeadSource nuevo al enum sin tocar <see cref="For"/> lo deja en
/// <see cref="Cold"/> — conservador: cae en el grupo que se apaga primero.
/// </summary>
public static class MessagingSourceGroups
{
    public const string Cold = "frios";
    public const string MetaAds = "meta-ads";
    public const string WhatsAppAds = "wa-ads";
    public const string Apps = "apps";
    public const string Reengage = "reengage";
    public const string Inbound = "inbound";

    public record GroupDef(string Key, string Label, string Hint);

    /// <summary>Orden en que se muestran en la UI y en el menú del bot maestro.</summary>
    public static readonly IReadOnlyList<GroupDef> All = new List<GroupDef>
    {
        new(MetaAds, "Meta Lead Ads",
            "Dejaron teléfono y respuestas en un formulario instantáneo de Facebook/Instagram."),
        new(WhatsAppAds, "Click-to-WhatsApp",
            "Escribieron al WhatsApp desde un anuncio."),
        new(Apps, "Altas y onboarding de apps",
            "Se registraron o arrancaron el alta (OTP) en TurnosPro, GymHero, etc."),
        new(Cold, "Prospección fría",
            "Leads scrapeados o cargados a mano: Maps, Apify, Instagram, importaciones."),
        new(Reengage, "Re-enganche B2B",
            "Importados de TurnosPro/GymHero para volver a contactarlos desde el Hub."),
        new(Inbound, "Inbound desconocido",
            "Números que escribieron a una línea sin venir de un anuncio."),
    };

    public static string For(LeadSource source) => source switch
    {
        LeadSource.MetaLeadAd => MetaAds,
        LeadSource.WhatsAppAd => WhatsAppAds,
        LeadSource.DemoSignup or LeadSource.ProductOnboarding => Apps,
        LeadSource.ProductReengage => Reengage,
        LeadSource.WhatsAppInbound => Inbound,
        _ => Cold,
    };

    /// <summary>Todos los LeadSource que caen en un grupo (para filtrar en las queries).</summary>
    public static List<LeadSource> SourcesOf(string group) =>
        Enum.GetValues<LeadSource>().Where(s => For(s) == group).ToList();

    public static bool IsKnown(string group) => All.Any(g => g.Key == group);
}
