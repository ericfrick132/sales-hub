import { useQuery } from '@tanstack/react-query';
import { Link } from 'react-router-dom';
import { api } from '../lib/api';
import { useAuthStore } from '../lib/auth';
import type { SellerDashboard } from '../lib/types';
import MetricCards from '../components/MetricCards';
import LeadTable from '../components/LeadTable';
import SendingControl from '../components/SendingControl';
import ConversationsList from '../components/ConversationsList';

export default function MyDashboard() {
  const user = useAuthStore((s) => s.user);
  const { data, isLoading } = useQuery({
    queryKey: ['dashboard-me'],
    queryFn: async () => (await api.get<SellerDashboard>('/dashboard/me')).data,
    // Faster refresh while sending so the counter ticks up live without manual reloads.
    refetchInterval: (q) => q.state.data?.metrics.sendingEnabled ? 5000 : 30000
  });

  if (isLoading || !data) return <div className="text-slate-500">Cargando…</div>;
  const m = data.metrics;

  return (
    <div className="space-y-6">
      <div className="flex items-end justify-between gap-2 flex-wrap">
        <div>
          <h1 className="text-xl md:text-2xl font-bold">Mi panel</h1>
        </div>
        <Link to="/connect" className="btn-secondary text-xs md:text-sm">Conectar/Ver WhatsApp</Link>
      </div>

      {user && (
        <SendingControl
          sellerId={user.sellerId}
          sendingEnabled={m.sendingEnabled}
          instanceStatus={m.instanceStatus}
          invalidate={[['dashboard-me']]}
        />
      )}

      <MetricCards cards={[
        { label: 'Hoy — cap/enviados', value: `${m.todaySent} / ${m.todayCap}` },
        { label: 'Asignados', value: m.leadsAssigned },
        { label: 'Enviados total', value: m.leadsSent },
        { label: 'Respondieron', value: `${m.leadsReplied} (${(m.replyRate*100).toFixed(0)}%)` },
        { label: 'Cerrados', value: `${m.leadsClosed} (${(m.closeRate*100).toFixed(0)}%)` },
        { label: 'En cola', value: data.queuedCount, hint: 'Mensajes programados' }
      ]} />

      <div>
        <h2 className="text-lg font-semibold mb-2">Mis conversaciones</h2>
        <ConversationsList initialBucket="all" maxHeight={360} />
      </div>

      <div>
        <h2 className="text-lg font-semibold mb-2">Leads activos ({data.activeLeads.length})</h2>
        <LeadTable leads={data.activeLeads.slice(0, 10)} />
      </div>
    </div>
  );
}
