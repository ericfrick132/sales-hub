import { Link, useParams } from 'react-router-dom';
import { useQuery } from '@tanstack/react-query';
import { api } from '../lib/api';
import type { Lead, SellerDashboard } from '../lib/types';
import LeadTable from '../components/LeadTable';
import ConversationsList from '../components/ConversationsList';

interface DailyActivity {
  date: string;
  total: number;
  byProduct: Record<string, number>;
  byStatus: Record<string, number>;
}

interface SellerActivity {
  sellerId: string;
  displayName: string;
  email: string;
  instanceStatus: string;
  sendingEnabled: boolean;
  total: number;
  total7d: number;
  todayCount: number;
  yesterdayCount: number;
  topProducts: Record<string, number>;
  daily: DailyActivity[];
}

const fmtDay = (iso: string) => {
  const [y, m, d] = iso.split('-');
  return `${d}/${m}`;
};

export default function SellerDetail() {
  const { id } = useParams<{ id: string }>();

  const dash = useQuery({
    queryKey: ['seller-dashboard', id],
    enabled: !!id,
    queryFn: async () => (await api.get<SellerDashboard>(`/dashboard/seller/${id}`)).data
  });

  const activity = useQuery({
    queryKey: ['sellers-activity', 30],
    queryFn: async () => (await api.get<SellerActivity[]>('/dashboard/sellers/activity', { params: { days: 30 } })).data
  });

  const recent = useQuery({
    queryKey: ['seller-recent-leads', id],
    enabled: !!id,
    queryFn: async () => (await api.get<Lead[]>('/leads/mine', { params: { sellerId: id, limit: 100 } })).data
  });

  const myActivity = (activity.data ?? []).find((a) => a.sellerId === id);

  if (dash.isLoading || !dash.data) return <div>Cargando…</div>;
  const m = dash.data.metrics;

  return (
    <div className="space-y-6">
      <div className="flex items-center gap-2 md:gap-3 flex-wrap">
        <Link to="/admin" className="text-sm text-slate-500 hover:text-slate-700">← Volver</Link>
        <h1 className="text-xl md:text-2xl font-bold">{m.displayName}</h1>
        <span className={`text-xs rounded px-2 py-0.5 border ${
          m.instanceStatus === 'Connected'
            ? 'bg-emerald-50 text-emerald-700 border-emerald-200'
            : 'bg-slate-50 text-slate-600 border-slate-200'
        }`}>
          {m.instanceStatus}
        </span>
        <span className={`text-xs rounded px-2 py-0.5 border ${
          m.sendingEnabled
            ? 'bg-emerald-50 text-emerald-700 border-emerald-200'
            : 'bg-slate-50 text-slate-600 border-slate-200'
        }`}>
          Envío: {m.sendingEnabled ? 'on' : 'off'}
        </span>
      </div>

      <div className="grid grid-cols-2 md:grid-cols-5 gap-3">
        <Card label="Hoy" value={myActivity?.todayCount ?? 0} />
        <Card label="Ayer" value={myActivity?.yesterdayCount ?? 0} />
        <Card label="7 días" value={myActivity?.total7d ?? 0} />
        <Card label="Cap hoy" value={`${m.todaySent}/${m.todayCap}`} />
        <Card label="En cola" value={dash.data.queuedCount} />
      </div>

      <div className="grid grid-cols-1 md:grid-cols-3 gap-4">
        <div className="card p-4">
          <div className="text-xs uppercase tracking-wide text-slate-500 mb-2">Funnel total</div>
          <Stat label="Asignados" value={m.leadsAssigned} />
          <Stat label="Contactados" value={m.leadsSent} />
          <Stat label="Respondieron" value={m.leadsReplied} />
          <Stat label="Cerrados" value={m.leadsClosed} />
          <div className="border-t border-slate-100 mt-2 pt-2 text-xs text-slate-500">
            Reply: {(m.replyRate * 100).toFixed(0)}% · Close: {(m.closeRate * 100).toFixed(0)}%
          </div>
        </div>
        <div className="card p-4 md:col-span-2">
          <div className="text-xs uppercase tracking-wide text-slate-500 mb-2">Top aplicaciones (30d)</div>
          {Object.keys(myActivity?.topProducts ?? {}).length === 0 ? (
            <div className="text-sm text-slate-500">Sin actividad en 30 días.</div>
          ) : (
            <ul className="space-y-1">
              {Object.entries(myActivity?.topProducts ?? {}).map(([k, v]) => (
                <li key={k} className="flex justify-between text-sm">
                  <span>{k}</span>
                  <span className="font-medium">{v}</span>
                </li>
              ))}
            </ul>
          )}
        </div>
      </div>

      <div className="space-y-2">
        <h2 className="text-lg font-semibold">Actividad diaria (30 días)</h2>
        {!myActivity ? (
          <div className="text-sm text-slate-500">Sin datos.</div>
        ) : (
          <DailyChart daily={myActivity.daily} />
        )}
      </div>

      <div className="space-y-2">
        <h2 className="text-lg font-semibold">Conversaciones</h2>
        <p className="text-xs text-slate-500">
          Filtrá por producto, período o foco de follow-up. Click una para abrir el chat.
        </p>
        {id && <ConversationsList sellerId={id} initialBucket="all" />}
      </div>

      <div className="space-y-2">
        <h2 className="text-lg font-semibold">Leads recientes</h2>
        {recent.isLoading ? (
          <div>Cargando…</div>
        ) : (
          <LeadTable leads={recent.data ?? []} showSeller={false} />
        )}
      </div>
    </div>
  );
}

function Card({ label, value }: { label: string; value: number | string }) {
  return (
    <div className="card p-4">
      <div className="text-xs text-slate-500">{label}</div>
      <div className="text-2xl font-bold">{value}</div>
    </div>
  );
}

function Stat({ label, value }: { label: string; value: number }) {
  return (
    <div className="flex justify-between text-sm py-1">
      <span className="text-slate-600">{label}</span>
      <span className="font-medium">{value}</span>
    </div>
  );
}

function DailyChart({ daily }: { daily: DailyActivity[] }) {
  const ordered = [...daily].reverse();
  const max = Math.max(1, ...ordered.map((d) => d.total));
  return (
    <div className="card p-4 overflow-x-auto">
      <div className="flex items-end gap-1 h-40 min-w-[480px] md:min-w-[600px]">
        {ordered.map((d) => {
          const h = Math.max(2, Math.round((d.total / max) * 100));
          const tooltip = d.total === 0
            ? `${fmtDay(d.date)}: sin leads`
            : `${fmtDay(d.date)}: ${d.total} · ${Object.entries(d.byProduct).map(([k, v]) => `${k}:${v}`).join(' · ')}`;
          return (
            <div key={d.date} className="flex flex-col items-center flex-1 min-w-[18px]" title={tooltip}>
              <div className={`w-full rounded-t ${d.total > 0 ? 'bg-brand-500' : 'bg-slate-100'}`} style={{ height: `${h}%` }} />
              <div className="text-[10px] text-slate-500 mt-1 whitespace-nowrap">{fmtDay(d.date)}</div>
              <div className="text-[10px] font-medium text-slate-700">{d.total || ''}</div>
            </div>
          );
        })}
      </div>
    </div>
  );
}
