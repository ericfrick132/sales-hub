/** Orígenes "calientes" que pueden tener cadencia propia. La key es el nombre
 *  exacto del enum LeadSource del backend. Todo lo que no está acá (scraping,
 *  Places, manual) es cold outreach y cae al default / override por categoría. */
export const SOURCE_ORIGINS: { key: string; label: string }[] = [
  { key: 'MetaLeadAd', label: 'Form de Meta (dejó sus datos)' },
  { key: 'WhatsAppAd', label: 'Anuncio CTWA (nos escribió)' },
  { key: 'ProductOnboarding', label: 'Onboarding abandonado' },
  { key: 'DemoSignup', label: 'Registro/demo en la app' },
  { key: 'ProductReengage', label: 'Re-enganche desde la app' }
];

export const originLabel = (key: string) =>
  SOURCE_ORIGINS.find((o) => o.key === key)?.label ?? key;

/** Mismo prefijo que usa el backend para identificar cadencias por origen
 *  en CadenceCategory / preview / rotación (ej. "origen:MetaLeadAd"). */
export const SRC_PREFIX = 'origen:';

/** "origen:MetaLeadAd" → "⚡ Form de Meta (dejó sus datos)"; el resto pasa igual. */
export const cadenceLabel = (cadenceCategory: string) =>
  cadenceCategory.startsWith(SRC_PREFIX)
    ? `⚡ ${originLabel(cadenceCategory.slice(SRC_PREFIX.length))}`
    : cadenceCategory;
