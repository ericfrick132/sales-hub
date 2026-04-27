import { useQuery } from '@tanstack/react-query';
import { Link } from 'react-router-dom';
import { api } from '../lib/api';
import type { SellerDashboard } from '../lib/types';
import MetricCards from '../components/MetricCards';
import LeadTable from '../components/LeadTable';

export default function MyDashboard() {
  const { data, isLoading } = useQuery({
    queryKey: ['dashboard-me'],
    queryFn: async () => (await api.get<SellerDashboard>('/dashboard/me')).data,
    refetchInterval: 30000
  });

  if (isLoading || !data) return <div className="text-slate-500">Cargando…</div>;
  const m = data.metrics;

  return (
    <div className="space-y-6">
      <div className="flex items-end justify-between">
        <div>
          <h1 className="text-2xl font-bold">Mi panel</h1>
          <p className="text-sm text-slate-500">Instance: <span className="font-medium">{m.instanceStatus}</span> — Envío: <span className="font-medium">{m.sendingEnabled ? 'activo' : 'pausado'}</span></p>
        </div>
        <Link to="/connect" className="btn-secondary">Conectar/Ver WhatsApp</Link>
      </div>

      <MetricCards cards={[
        { label: 'Hoy — cap/enviados', value: `${m.todaySent} / ${m.todayCap}` },
        { label: 'Asignados', value: m.leadsAssigned },
        { label: 'Enviados total', value: m.leadsSent },
        { label: 'Respondieron', value: `${m.leadsReplied} (${(m.replyRate*100).toFixed(0)}%)` },
        { label: 'Cerrados', value: `${m.leadsClosed} (${(m.closeRate*100).toFixed(0)}%)` },
        { label: 'En cola', value: data.queuedCount, hint: 'Mensajes programados' }
      ]} />

      <div>
        <h2 className="text-lg font-semibold mb-2">Leads activos ({data.activeLeads.length})</h2>
        <LeadTable leads={data.activeLeads.slice(0, 10)} />
      </div>
    </div>
  );
}
