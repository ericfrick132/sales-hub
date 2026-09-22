namespace SalesHub.Core.Domain.Enums;

public enum LeadSource
{
    GooglePlaces = 0,
    ApifyGoogleMaps = 1,
    ApifyMetaAdsLibrary = 2,
    ApifyInstagram = 3,
    ApifyFacebookPages = 4,
    Manual = 99,
    ManualMaps = 100,
    ManualInstagram = 101,
    ManualWhatsApp = 102,
    ManualWeb = 103,
    // Contacto que ya estaba en el WhatsApp de un teléfono nuestro: lo cargó la importación
    // del historial al escanear el QR (gente que nos escribió o a la que le escribimos hace
    // tiempo). Se laburan LLAMÁNDOLOS desde el CRM, no mandándoles WhatsApp: por eso queda
    // DEBAJO de 400 (el piso de los "calientes", que se saltean el techo de tráfico frío) y
    // OutboxEnqueueHelper no les encola cadencia — son miles de contactos viejos y blastearlos
    // es el camino más corto a un ban (ban del 30/07/2026).
    Remarketing = 104,
    BrowserCapture = 200,
    InstagramScraper = 300,
    // Tenant/demo nuevo registrado en el propio producto (TurnosPro/GymHero) que entra
    // al follow-up automático vía el endpoint de intake.
    DemoSignup = 400,
    // Lead importado/sincronizado desde otro producto propio (TurnosPro/GymHero) para
    // re-engagement centralizado: SalesHub lo trae por el endpoint de export de ese producto.
    ProductReengage = 401,
    // Lead que llegó al WhatsApp por un anuncio (click-to-WhatsApp), pusheado al Hub.
    WhatsAppAd = 402,
    // Lead que arrancó el onboarding/OTP de un producto (alta intención; incluye
    // los que pidieron OTP y NO completaron el setup → recuperación de abandono).
    ProductOnboarding = 403,
    // Lead que dejó sus datos en un formulario instantáneo de Meta (Lead Ad): teléfono +
    // respuestas de calificación, ingerido por el webhook /api/webhooks/meta-leads.
    MetaLeadAd = 404,
    // Número desconocido que escribió a una línea vinculada (vendedor o app) sin texto
    // de anuncio: se crea igual para que TODOS los chats queden centralizados en
    // Conversaciones. El bot arranca muteado (lo maneja un humano salvo que lo prendan).
    WhatsAppInbound = 405
}
