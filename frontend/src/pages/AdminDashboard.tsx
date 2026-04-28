import { Link } from 'react-router-dom';
import { useQuery } from '@tanstack/react-query';
import { api } from '../lib/api';
import type { GlobalMetrics } from '../lib/types';
import MetricCards from '../components/MetricCards';
import SendingControl from '../components/SendingControl';

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
  const [, m, d] = iso.split('-');
  return `${d}/${m}`;
};

export default function AdminDashboard() {
  const { data, isLoading } = useQuery({
    queryKey: ['admin-metrics'],
    queryFn: async () => (await api.get<GlobalMetrics>('/dashboard/admin')).data,
    refetchInterval: (q) => q.state.data?.sellers.some((s) => s.sendingEnabled) ? 5000 : 15000
  });

  const activity = useQuery({
    queryKey: ['sellers-activity', 14],
    queryFn: async () => (await api.get<SellerActivity[]>('/dashboard/sellers/activity', { params: { days: 14 } })).data,
    refetchInterval: 30000
  });

  if (isLoading || !data) return <div>Cargando…</div>;

  // Build the date column header from any seller's daily array (all share the same dates).
  const dateHeaders: string[] = activity.data?.[0]?.daily.slice(0, 14).map((d) => d.date) ?? [];
  const sellerActivityById = new Map((activity.data ?? []).map((a) => [a.sellerId, a]));

  return (
    <div className="space-y-6">
      <h1 className="text-2xl font-bold">Hoy</h1>

      <MetricCards cards={[
        { label: 'Leads totales', value: data.totalLeads },
        { label: 'Leads hoy', value: data.leadsToday },
        { label: 'Enviados 7d', value: data.leadsSent7d },
        { label: 'Respondieron 7d', value: data.leadsReplied7d },
        { label: 'Cerrados 7d', value: data.leadsClosed7d }
      ]} />

      <div>
        <h2 className="text-lg font-semibold mb-2">Actividad por vendedor (14 días)</h2>
        <p className="text-xs text-slate-500 mb-2">
          Click en el nombre para ver el detalle. Cada celda es la cantidad de leads cargados/asignados ese día.
          Hover para ver el desglose por aplicación.
        </p>
        <div className="card overflow-x-auto">
          <table className="min-w-full text-sm">
            <thead className="bg-slate-50 text-xs uppercase tracking-wide text-slate-500">
              <tr>
                <th className="px-3 py-2 text-left sticky left-0 bg-slate-50">Vendedor</th>
                <th className="px-3 py-2 text-right">Hoy</th>
                <th className="px-3 py-2 text-right">Ayer</th>
                <th className="px-3 py-2 text-right">7d</th>
                <th className="px-3 py-2 text-left">Top apps</th>
                {dateHeaders.map((d) => (
                  <th key={d} className="px-2 py-2 text-right text-[10px] font-medium">{fmtDay(d)}</th>
                ))}
              </tr>
            </thead>
            <tbody className="divide-y divide-slate-100">
              {data.sellers.map((s) => {
                const a = sellerActivityById.get(s.sellerId);
                return (
                  <tr key={s.sellerId} className="hover:bg-slate-50">
                    <td className="px-3 py-2 sticky left-0 bg-white">
                      <Link to={`/admin/sellers/${s.sellerId}`} className="font-medium text-brand-700 hover:underline">
                        {s.displayName}
                      </Link>
                      <div className="text-[10px] text-slate-400 mt-0.5">
                        {s.instanceStatus} · {s.sendingEnabled ? 'enviando' : 'pausado'}
                      </div>
                    </td>
                    <td className="px-3 py-2 text-right font-bold">{a?.todayCount ?? 0}</td>
                    <td className="px-3 py-2 text-right text-slate-600">{a?.yesterdayCount ?? 0}</td>
                    <td className="px-3 py-2 text-right text-slate-600">{a?.total7d ?? 0}</td>
                    <td className="px-3 py-2 text-slate-600 text-xs">
                      {a && Object.keys(a.topProducts).length > 0
                        ? Object.entries(a.topProducts).map(([k, v]) => `${k} (${v})`).join(', ')
                        : '—'}
                    </td>
                    {dateHeaders.map((d) => {
                      const day = a?.daily.find((x) => x.date === d);
                      const tooltip = day && day.total > 0
                        ? Object.entries(day.byProduct).map(([k, v]) => `${k}: ${v}`).join('\n')
                        : '';
                      const v = day?.total ?? 0;
                      return (
                        <td key={d} className="px-2 py-2 text-right" title={tooltip}>
                          {v === 0 ? (
                            <span className="text-slate-300">·</span>
                          ) : (
                            <span className={`inline-block px-1.5 rounded text-xs font-medium ${
                              v >= 15 ? 'bg-emerald-100 text-emerald-700'
                              : v >= 5 ? 'bg-sky-100 text-sky-700'
                              : 'bg-slate-100 text-slate-600'
                            }`}>{v}</span>
                          )}
                        </td>
                      );
                    })}
                  </tr>
                );
              })}
            </tbody>
          </table>
        </div>
      </div>

      <div className="grid md:grid-cols-2 gap-6">
        <div className="card p-5">
          <h3 className="font-semibold mb-3">Por aplicación</h3>
          <table className="w-full text-sm">
            <tbody>
              {Object.entries(data.leadsByProduct).sort((a, b) => b[1] - a[1]).map(([k, v]) => (
                <tr key={k} className="border-b border-slate-100">
                  <td className="py-1">{k}</td>
                  <td className="py-1 text-right font-medium">{v}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
        <div className="card p-5">
          <h3 className="font-semibold mb-3">Por origen</h3>
          <table className="w-full text-sm">
            <tbody>
              {Object.entries(data.leadsBySource).sort((a, b) => b[1] - a[1]).map(([k, v]) => (
                <tr key={k} className="border-b border-slate-100">
                  <td className="py-1">{k}</td>
                  <td className="py-1 text-right font-medium">{v}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      </div>

      <div>
        <h2 className="text-lg font-semibold mb-2">Estado de envíos</h2>
        <div className="card overflow-hidden">
          <table className="min-w-full text-sm">
            <thead className="bg-slate-50 text-xs uppercase tracking-wide text-slate-500">
              <tr>
                <th className="px-3 py-2 text-left">Nombre</th>
                <th className="px-3 py-2 text-left">Instance</th>
                <th className="px-3 py-2 text-left">Envío</th>
                <th className="px-3 py-2 text-right">Cap hoy</th>
                <th className="px-3 py-2 text-right">Enviados hoy</th>
                <th className="px-3 py-2 text-right">Asignados</th>
                <th className="px-3 py-2 text-right">Reply %</th>
                <th className="px-3 py-2 text-right">Close %</th>
              </tr>
            </thead>
            <tbody className="divide-y divide-slate-100">
              {data.sellers.map((s) => (
                <tr key={s.sellerId}>
                  <td className="px-3 py-2 font-medium">
                    <Link to={`/admin/sellers/${s.sellerId}`} className="text-brand-700 hover:underline">
                      {s.displayName}
                    </Link>
                  </td>
                  <td className="px-3 py-2">{s.instanceStatus}</td>
                  <td className="px-3 py-2">
                    <SendingControl
                      sellerId={s.sellerId}
                      sendingEnabled={s.sendingEnabled}
                      instanceStatus={s.instanceStatus}
                      compact
                      invalidate={[['admin-metrics']]}
                    />
                  </td>
                  <td className="px-3 py-2 text-right">{s.todayCap}</td>
                  <td className="px-3 py-2 text-right">{s.todaySent}</td>
                  <td className="px-3 py-2 text-right">{s.leadsAssigned}</td>
                  <td className="px-3 py-2 text-right">{(s.replyRate*100).toFixed(0)}%</td>
                  <td className="px-3 py-2 text-right">{(s.closeRate*100).toFixed(0)}%</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      </div>
    </div>
  );
}
