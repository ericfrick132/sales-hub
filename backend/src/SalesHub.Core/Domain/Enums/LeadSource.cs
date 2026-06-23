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
    BrowserCapture = 200,
    InstagramScraper = 300,
    // Tenant/demo nuevo registrado en el propio producto (TurnosPro/GymHero) que entra
    // al follow-up automático vía el endpoint de intake.
    DemoSignup = 400
}
