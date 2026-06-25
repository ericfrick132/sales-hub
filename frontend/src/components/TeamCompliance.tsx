import { useQuery } from '@tanstack/react-query';
import { Link } from 'react-router-dom';
import { api } from '../lib/api';
import type { SellerCompliance } from '../lib/types';

/**
 * Vista de pájaro del admin: cumplimiento de HOY por vendedor (% general + barritas
 * por métrica). No renderiza nada si no hay metas diarias cargadas.
 */
export default function TeamCompliance() {
  const { data, isLoading } = useQuery({
    queryKey: ['goals-compliance', 'Daily'],
    queryFn: async () =>
      (await api.get<SellerCompliance[]>('/seller-goals/compliance', { params: { period: 'Daily' } })).data,
    refetchInterval: 30000,
  });

  if (isLoading) return null;
  const rows = data ?? [];
  if (rows.length === 0) return null;

  return (
    <div className="card p-5">
      <div className="flex items-center justify-between mb-3">
        <h2 className="text-lg font-semibold">Cumplimiento hoy</h2>
        <Link to="/objetivos" className="text-xs text-brand-700 hover:underline">Editar metas</Link>
      </div>
      <div className="divide-y divide-slate-100">
        {rows.map((r) => (
          <div key={r.sellerId} className="py-3 flex flex-col md:flex-row md:items-center gap-2 md:gap-4">
            <div className="flex items-center gap-2 md:w-48 shrink-0">
              <PctBadge pct={r.overallPercent} />
              <span className="font-medium truncate">{r.sellerName}</span>
            </div>
            <div className="flex flex-wrap gap-x-5 gap-y-2 flex-1">
              {r.goals.map((g) => (
                <MiniBar key={g.goalId} label={g.metric} current={g.current} target={g.target} percent={g.percent} />
              ))}
            </div>
          </div>
        ))}
      </div>
    </div>
  );
}

function PctBadge({ pct }: { pct: number }) {
  const tone =
    pct >= 100 ? 'bg-emerald-100 text-emerald-700'
    : pct >= 60 ? 'bg-amber-100 text-amber-700'
    : 'bg-red-100 text-red-700';
  return <span className={`badge ${tone} w-12 justify-center tabular-nums`}>{pct}%</span>;
}

function MiniBar({ label, current, target, percent }: { label: string; current: number; target: number; percent: number }) {
  const done = percent >= 100;
  return (
    <div className="min-w-[130px]">
      <div className="flex items-center justify-between text-xs mb-0.5">
        <span className="text-slate-500">{label}</span>
        <span className={`tabular-nums ${done ? 'text-emerald-600 font-semibold' : 'text-slate-600'}`}>
          {current}/{target}
        </span>
      </div>
      <div className="w-full h-1.5 bg-slate-200 rounded-full overflow-hidden">
        <div
          className={`h-full rounded-full transition-all ${done ? 'bg-emerald-500' : 'bg-brand-500'}`}
          style={{ width: `${Math.min(100, percent)}%` }}
        />
      </div>
    </div>
  );
}
