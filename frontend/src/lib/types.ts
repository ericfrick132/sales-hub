export type LeadStatus =
  | 'New' | 'Assigned' | 'Queued' | 'Sent' | 'Replied'
  | 'Interested' | 'DemoScheduled' | 'Closed' | 'Lost' | 'Blocked';

// ---------------------------------------------------------------- SEO / GEO

export type SeoIntent = 'Informational' | 'Commercial' | 'Transactional' | 'Navigational';
export type SeoKeywordStatus = 'Idea' | 'Planned' | 'Used' | 'Ignored';
export type SeoContentType = 'Article' | 'Guide' | 'Faq' | 'Comparison' | 'Landing';
export type SeoArticleStatus = 'Draft' | 'NeedsReview' | 'Approved' | 'Published' | 'Archived';

export interface SeoSite {
  id: string;
  productKey?: string | null;
  name: string;
  domain: string;
  language: string;
  sector: string;
  audience: string;
  productSummary: string;
  brandVoice: string;
  targetCountries: string[];
  blogBaseUrl: string;
  autoPublish: boolean;
  publishCadenceHours: number;
  postsPerRun: number;
  repoFullName: string;
  repoBranch: string;
  repoPublicDir: string;
  isActive: boolean;
  keywordCount: number;
  articleCount: number;
  publishedCount: number;
}

export interface SeoKeyword {
  id: string;
  siteId: string;
  term: string;
  cluster: string;
  intent: SeoIntent;
  source: string;
  volume?: number | null;
  difficulty?: number | null;
  priority: number;
  status: SeoKeywordStatus;
  createdAt: string;
}

export interface SeoArticleSummary {
  id: string;
  siteId: string;
  targetKeyword: string;
  title: string;
  slug: string;
  contentType: SeoContentType;
  status: SeoArticleStatus;
  seoScore?: number | null;
  wordCount: number;
  generatedBy: string;
  publishedUrl: string;
  createdAt: string;
  updatedAt: string;
  publishedAt?: string | null;
}

export interface SeoArticleFull extends SeoArticleSummary {
  keywordId?: string | null;
  metaDescription: string;
  bodyMarkdown: string;
  faqJson: string;
  jsonLd: string;
  optimizationNotes: string;
}

export const LEAD_SOURCE_LABEL: Record<LeadSource, string> = {
  GooglePlaces: 'Google Places',
  ApifyGoogleMaps: 'Maps',
  ApifyMetaAdsLibrary: 'Meta Ads',
  ApifyInstagram: 'Instagram',
  ApifyFacebookPages: 'Facebook',
  Manual: 'Manual',
  ManualMaps: 'Maps',
  ManualInstagram: 'Instagram',
  ManualWhatsApp: 'WhatsApp',
  ManualWeb: 'Web',
  BrowserCapture: 'Captura web',
  InstagramScraper: 'Instagram (scraper)',
  DemoSignup: 'Demo / alta producto',
  ProductReengage: 'Re-enganche producto',
  WhatsAppAd: 'WhatsApp Ad (CTWA)',
  ProductOnboarding: 'Onboarding / OTP',
  MetaLeadAd: 'Meta Lead Ads'
};

export const LEAD_STATUS_LABEL: Record<LeadStatus, string> = {
  New: 'Nuevo',
  Assigned: 'Asignado',
  Queued: 'En cola',
  Sent: 'Contactado',
  Replied: 'Respondió',
  Interested: 'Interesado',
  DemoScheduled: 'Demo agendada',
  Closed: 'Cerrado',
  Lost: 'Perdido',
  Blocked: 'Bloqueado'
};

export type LeadSource =
  | 'GooglePlaces' | 'ApifyGoogleMaps' | 'ApifyMetaAdsLibrary'
  | 'ApifyInstagram' | 'ApifyFacebookPages' | 'Manual'
  | 'ManualMaps' | 'ManualInstagram' | 'ManualWhatsApp' | 'ManualWeb'
  | 'BrowserCapture' | 'InstagramScraper' | 'DemoSignup' | 'ProductReengage'
  | 'WhatsAppAd' | 'ProductOnboarding' | 'MetaLeadAd';

export interface Lead {
  id: string;
  productKey: string;
  productName?: string;
  source: LeadSource;
  name: string;
  city?: string;
  province?: string;
  whatsappPhone?: string;
  website?: string;
  instagramHandle?: string;
  facebookUrl?: string;
  rating?: number;
  totalReviews?: number;
  score: number;
  status: LeadStatus;
  sellerId?: string;
  sellerName?: string;
  renderedMessage?: string;
  whatsappLink?: string;
  assignedAt?: string;
  sentAt?: string;
  firstReplyAt?: string;
  notes?: string;
  createdAt: string;
  searchCategory?: string;
  searchQuery?: string;
}

export interface Product {
  id: string;
  productKey: string;
  displayName: string;
  active: boolean;
  country: string;
  countryName: string;
  regionCode: string;
  language: string;
  phonePrefix: string;
  categories: string[];
  messageTemplate: string;
  openerTemplate: string;
  checkoutUrl: string;
  priceDisplay: string;
  dailyLimit: number;
  triggerHours: number[];
  sendHourStart: number;
  sendHourEnd: number;
  requiresAssistedSale: boolean;
  googlePlacesDailyLeadCap: number;
  replyTemplates: string[];
  messageSteps: MessageStep[];
  categoryCadences: CategoryCadence[];
  /** Cadencia para leads que llegan por Meta Lead Ads (paso a paso de alta de
   *  cuenta). Vacía = esos leads usan la cadencia default como cualquier otro. */
  metaAdsMessageSteps: MessageStep[];
  aiSalesPlaybook: string;
  autoPilot: boolean;
  autoReengage: boolean;
}

export interface MessageStep {
  text: string;
  delaySeconds: number;
  mediaAssetId?: string | null;
  /** Si es audio, podés cargar varias variantes y la app rota round-robin entre ellas. */
  mediaAssetIds?: string[];
}

/** Override de cadencia para una categoría puntual del producto. Si está, sus
 *  steps reemplazan al messageSteps default cuando el lead tiene esa categoría. */
export interface CategoryCadence {
  category: string;
  steps: MessageStep[];
}

export interface LeadPreviewStep {
  index: number;
  text: string;
  delaySeconds: number;
  /** "audio" | "image" | "pdf" | "video" | "file" | null si es texto puro. */
  mediaKind?: string | null;
  mediaFileName?: string | null;
  /** Cuántas variantes hay configuradas en este step (rotación). */
  mediaVariantsCount?: number | null;
}

export interface LeadPreview {
  hasSteps: boolean;
  /** "" cuando es la cadencia default del producto. */
  category: string;
  /** Solo cuando hasSteps=false: el MessageTemplate legacy renderizado. */
  legacyMessage?: string | null;
  steps: LeadPreviewStep[];
}

export interface MediaAsset {
  id: string;
  productKey: string;
  fileName: string;
  mimeType: string;
  sizeBytes: number;
  createdAt: string;
}

export type InstanceStatus = 'Disconnected' | 'Connecting' | 'Connected' | 'Banned' | 'Unknown' | null;
export type SendMode = 'Conservative' | 'Balanced' | 'Aggressive' | 'Custom';

export interface Seller {
  id: string;
  sellerKey: string;
  displayName: string;
  email: string;
  role: string;
  isActive: boolean;
  sendingEnabled: boolean;
  whatsappPhone?: string;
  evolutionInstance?: string;
  instanceStatus?: InstanceStatus;
  verticalsWhitelist: string[];
  regionsAssigned: string[];
  keywordRules: string[];
  sendMode: SendMode;
  dailyCap: number;
  dailyVariancePct: number;
  warmupDays: number;
  warmupStartedAt?: string;
  activeHoursStart: number;
  activeHoursEnd: number;
  timezone: string;
  delayMinSeconds: number;
  delayMaxSeconds: number;
  burstSize: number;
  burstPauseMinSeconds: number;
  burstPauseMaxSeconds: number;
  preSendTypingMinSeconds: number;
  preSendTypingMaxSeconds: number;
  readIncomingFirst: boolean;
  skipDayProbabilityPct: number;
  typoProbabilityPct: number;
}

export interface SellerMetricRow {
  sellerId: string;
  displayName: string;
  leadsAssigned: number;
  leadsSent: number;
  leadsReplied: number;
  leadsClosed: number;
  replyRate: number;
  closeRate: number;
  todayCap: number;
  todaySent: number;
  instanceStatus: string;
  sendingEnabled: boolean;
}

export interface GlobalMetrics {
  totalLeads: number;
  leadsToday: number;
  leadsSent7d: number;
  leadsReplied7d: number;
  leadsClosed7d: number;
  leadsByProduct: Record<string, number>;
  leadsBySource: Record<string, number>;
  sellers: SellerMetricRow[];
}

export interface SellerDashboard {
  metrics: SellerMetricRow;
  activeLeads: Lead[];
  queuedCount: number;
  todaySentCount: number;
  todayCap: number;
}

export type SellerGoalMetric = 'Contactos' | 'Respuestas' | 'Demos' | 'Cierres';
export type SellerGoalPeriod = 'Daily' | 'Weekly';

export interface SellerGoal {
  id: string;
  sellerId: string | null;
  sellerName: string | null;
  productKey: string | null;
  metric: SellerGoalMetric;
  period: SellerGoalPeriod;
  target: number;
  isActive: boolean;
}

export interface SellerGoalProgress {
  goalId: string;
  metric: SellerGoalMetric;
  period: SellerGoalPeriod;
  productKey: string | null;
  target: number;
  current: number;
  percent: number;
}

export interface SellerCompliance {
  sellerId: string;
  sellerName: string;
  overallPercent: number;
  goals: SellerGoalProgress[];
}

// Gasto de la API de Claude (la key de IA): costo USD + desglose por feature de hoy.
export interface AiCostFeature {
  feature: string;
  costUsd: number;
  calls: number;
}

export interface AiCost {
  todayUsd: number;
  last7dUsd: number;
  last30dUsd: number;
  byFeatureToday: AiCostFeature[];
}

// Embudo de efectividad por aplicación (leads → contactados → ... → cerrados + tasas).
export interface AppEffectiveness {
  productKey: string;
  leads: number;
  contactados: number;
  respondieron: number;
  demos: number;
  cerrados: number;
  perdidos: number;
  replyRate: number;
  demoRate: number;
  closeRate: number;
}

// Leads de anuncios (Meta Lead Ads + WhatsApp Ads) por app — para las cards del dashboard.
export interface AdLeads {
  productKey: string;
  displayName: string;
  metaLeadAd: number;
  whatsAppAd: number;
  total: number;
  last7d: number;
}

// Regla dura genérica de la IA de ventas (CRUD admin; se inyecta al system prompt).
export interface AiRule {
  id: string;
  text: string;
  productKey: string | null;
  isActive: boolean;
  sortOrder: number;
}

export interface OnboardingAppConfig {
  productKey: string;
  displayName: string;
  enabled: boolean;
  selfServe: boolean;
  intro: string;
  questions: string[];
  emailPrompt: string;
  provisionUrl: string;
  provisionNameField: string;
  successMessage: string;
  closingMessage: string;
  usePitchAudio: boolean;
  replyDelayMinSec: number;
  replyDelayMaxSec: number;
  audioCount: number;
}

export interface OnboardingAudioVariant {
  id: string;
  durationMs: number;
  createdAt: string;
}
