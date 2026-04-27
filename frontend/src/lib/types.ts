export type LeadStatus =
  | 'New' | 'Assigned' | 'Queued' | 'Sent' | 'Replied'
  | 'Interested' | 'DemoScheduled' | 'Closed' | 'Lost' | 'Blocked';

export type LeadSource =
  | 'GooglePlaces' | 'ApifyGoogleMaps' | 'ApifyMetaAdsLibrary'
  | 'ApifyInstagram' | 'ApifyFacebookPages' | 'Manual'
  | 'ManualMaps' | 'ManualInstagram' | 'ManualWhatsApp' | 'ManualWeb';

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
  checkoutUrl: string;
  priceDisplay: string;
  dailyLimit: number;
  triggerHours: number[];
  requiresAssistedSale: boolean;
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
