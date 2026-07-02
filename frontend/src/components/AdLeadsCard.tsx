import { Link } from 'react-router-dom';
import { useQuery } from '@tanstack/react-query';
import { api } from '../lib/api';
import type { AdLeads } from '../lib/types';

/**
 * Leads de ANUNCIOS por app, en un pantallazo: Meta Lead Ads (formularios) + WhatsApp Ads
 * (click-to-WhatsApp). Cada número es un link a /leads ya filtrado por app + fuente (drill-down).
 */
export default function AdLeadsCard() {
  const { data, isLoading } = useQuery({
    queryKey: ['ad-leads'],
    queryFn: async () => (await api.get<AdLeads[]>('/dashboard/ad-leads')).data,
    refetchInterval: 30000,
  });

  const rows = data ?? [];

  return (
    <div className="card p-5">
      <div className="flex items-center justify-between mb-3 gap-2 flex-wrap">
        <h2 className="text-lg font-semibold">Leads de anuncios por app</h2>
        <span className="text-xs text-slate-400">Meta forms + WhatsApp ads · clic para ver el detalle</span>
      </div>

      {isLoading ? (
        <p className="text-slate-400 text-sm">Cargando…</p>
      ) : rows.length === 0 ? (
        <p className="text-slate-400 text-sm">Todavía no entraron leads de anuncios.</p>
      ) : (
        <div className="grid grid-cols-2 md:grid-cols-3 lg:grid-cols-4 gap-3">
          {rows.map((r) => (
            <div
              key={r.productKey}
              className="rounded-lg border border-slate-200 p-3 hover:border-slate-300 hover:shadow-sm transition"
            >
              <div className="flex items-baseline justify-between gap-2">
                <span className="font-medium text-slate-700 truncate" title={r.displayName}>{r.displayName}</span>
                {r.last7d > 0 && <span className="text-[11px] text-emerald-600 font-medium whitespace-nowrap">+{r.last7d} · 7d</span>}
              </div>

              <Link
                to={`/leads?product=${encodeURIComponent(r.productKey)}&source=MetaLeadAd&source=WhatsAppAd`}
                className="block mt-1 text-3xl font-bold tabular-nums text-slate-900 hover:text-brand-600"
                title="Ver todos los leads de anuncios de esta app"
              >
                {r.total}
              </Link>

              <div className="mt-2 flex gap-3 text-xs">
                <Link
                  to={`/leads?product=${encodeURIComponent(r.productKey)}&source=MetaLeadAd`}
                  className="text-slate-500 hover:text-brand-600"
                  title="Meta Lead Ads (formularios)"
                >
                  Meta <span className="font-semibold tabular-nums text-slate-700">{r.metaLeadAd}</span>
                </Link>
                <Link
                  to={`/leads?product=${encodeURIComponent(r.productKey)}&source=WhatsAppAd`}
                  className="text-slate-500 hover:text-brand-600"
                  title="WhatsApp Ads (click-to-WhatsApp)"
                >
                  WhatsApp <span className="font-semibold tabular-nums text-slate-700">{r.whatsAppAd}</span>
                </Link>
              </div>
            </div>
          ))}
        </div>
      )}
    </div>
  );
}
