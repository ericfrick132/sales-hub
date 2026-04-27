import { useQuery } from '@tanstack/react-query';
import { api } from '../lib/api';
import type { GlobalMetrics } from '../lib/types';
import MetricCards from '../components/MetricCards';

export default function AdminDashboard() {
  const { data, isLoading } = useQuery({
    queryKey: ['admin-metrics'],
    queryFn: async () => (await api.get<GlobalMetrics>('/dashboard/admin')).data,
    refetchInterval: 15000
  });

  if (isLoading || !data) return <div>Cargando…</div>;

  return (
    <div className="space-y-6">
      <h1 className="text-2xl font-bold">Panel admin</h1>

      <MetricCards cards={[
        { label: 'Leads totales', value: data.totalLeads },
        { label: 'Leads hoy', value: data.leadsToday },
        { label: 'Enviados 7d', value: data.leadsSent7d },
        { label: 'Respondieron 7d', value: data.leadsReplied7d },
        { label: 'Cerrados 7d', value: data.leadsClosed7d }
      ]} />

      <div className="grid md:grid-cols-2 gap-6">
        <div className="card p-5">
          <h3 className="font-semibold mb-3">Por producto</h3>
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
          <h3 className="font-semibold mb-3">Por fuente</h3>
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
        <h2 className="text-lg font-semibold mb-2">Vendedores</h2>
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
                  <td className="px-3 py-2 font-medium">{s.displayName}</td>
                  <td className="px-3 py-2">{s.instanceStatus}</td>
                  <td className="px-3 py-2">{s.sendingEnabled ? 'On' : 'Off'}</td>
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
