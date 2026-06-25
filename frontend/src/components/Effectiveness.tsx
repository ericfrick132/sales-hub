import { useState } from 'react';
import { useQuery } from '@tanstack/react-query';
import { api } from '../lib/api';
import type { AppEffectiveness } from '../lib/types';

const PERIODS = [
  { key: 'today', label: 'Hoy' },
  { key: '7d', label: '7 días' },
  { key: '30d', label: '30 días' },
  { key: 'all', label: 'Todo' },
] as const;

const ORIGINS = [
  { key: 'all', label: 'Todos' },
  { key: 'inbound', label: 'Entrante' },
  { key: 'outbound', label: 'Saliente' },
] as const;

const pct = (n: number) => `${Math.round(n * 100)}%`;

function Rate({ value }: { value: number }) {
  const p = value * 100;
  const tone = p >= 50 ? 'text-emerald-600' : p >= 20 ? 'text-amber-600' : 'text-slate-500';
  return <span className={`tabular-nums font-medium ${tone}`}>{pct(value)}</span>;
}

/**
 * Embudo de efectividad por aplicación: leads → contactados → respondieron → demos →
 * cerrados, con las tasas (% resp / % demo / % cierre) y filtro de ventana. Con esto
 * se ve qué app convierte y dónde se cae el embudo (y si el opener engancha).
 */
export default function Effectiveness() {
  const [period, setPeriod] = useState<string>('30d');
  const [origin, setOrigin] = useState<string>('all');
  const { data, isLoading } = useQuery({
    queryKey: ['effectiveness', period, origin],
    queryFn: async () =>
      (await api.get<AppEffectiveness[]>('/dashboard/effectiveness', { params: { period, origin } })).data,
    refetchInterval: 60000,
  });

  const rows = data ?? [];

  return (
    <div className="card p-5">
      <div className="flex items-center justify-between mb-3 gap-2 flex-wrap">
        <h2 className="text-lg font-semibold">Efectividad por aplicación</h2>
        <div className="flex gap-3 flex-wrap">
          <div className="flex gap-1">
            {ORIGINS.map((o) => (
              <button
                key={o.key}
                onClick={() => setOrigin(o.key)}
                className={`text-xs px-2 py-1 rounded ${
                  origin === o.key ? 'bg-slate-700 text-white' : 'bg-slate-100 text-slate-600 hover:bg-slate-200'
                }`}
                title="Entrante = nos escriben (ads/producto) · Saliente = los contactamos nosotros"
              >
                {o.label}
              </button>
            ))}
          </div>
          <div className="flex gap-1">
            {PERIODS.map((p) => (
              <button
                key={p.key}
                onClick={() => setPeriod(p.key)}
                className={`text-xs px-2 py-1 rounded ${
                  period === p.key ? 'bg-brand-600 text-white' : 'bg-slate-100 text-slate-600 hover:bg-slate-200'
                }`}
              >
                {p.label}
              </button>
            ))}
          </div>
        </div>
      </div>

      {isLoading ? (
        <p className="text-slate-400 text-sm">Cargando…</p>
      ) : rows.length === 0 ? (
        <p className="text-slate-400 text-sm">Sin leads en esta ventana.</p>
      ) : (
        <div className="overflow-x-auto -mx-1">
          <table className="min-w-full text-sm">
            <thead className="text-xs uppercase tracking-wide text-slate-500">
              <tr>
                <th className="px-2 py-1 text-left">App</th>
                <th className="px-2 py-1 text-right">Leads</th>
                <th className="px-2 py-1 text-right">Contact.</th>
                <th className="px-2 py-1 text-right">Respond.</th>
                <th className="px-2 py-1 text-right">Demos</th>
                <th className="px-2 py-1 text-right">Cerrados</th>
                <th className="px-2 py-1 text-right">% resp</th>
                <th className="px-2 py-1 text-right">% demo</th>
                <th className="px-2 py-1 text-right">% cierre</th>
              </tr>
            </thead>
            <tbody className="divide-y divide-slate-100">
              {rows.map((r) => (
                <tr key={r.productKey} className="hover:bg-slate-50">
                  <td className="px-2 py-1.5 font-medium">{r.productKey}</td>
                  <td className="px-2 py-1.5 text-right tabular-nums">{r.leads}</td>
                  <td className="px-2 py-1.5 text-right tabular-nums text-slate-600">{r.contactados}</td>
                  <td className="px-2 py-1.5 text-right tabular-nums text-slate-600">{r.respondieron}</td>
                  <td className="px-2 py-1.5 text-right tabular-nums text-slate-600">{r.demos}</td>
                  <td className="px-2 py-1.5 text-right tabular-nums font-semibold">{r.cerrados}</td>
                  <td className="px-2 py-1.5 text-right"><Rate value={r.replyRate} /></td>
                  <td className="px-2 py-1.5 text-right"><Rate value={r.demoRate} /></td>
                  <td className="px-2 py-1.5 text-right"><Rate value={r.closeRate} /></td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
      <p className="text-[11px] text-slate-400 mt-3">
        % resp = respondieron/contactados · % demo = demos/respondieron · % cierre = cerrados/contactados.
        Ventana = leads que entraron en ese período.
      </p>
    </div>
  );
}
