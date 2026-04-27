import { useQuery, useQueryClient } from '@tanstack/react-query';
import { api } from '../lib/api';
import toast from 'react-hot-toast';
import QrPanel from '../components/QrPanel';
import { useAuthStore } from '../lib/auth';
import type { SellerDashboard } from '../lib/types';

export default function Connect() {
  const user = useAuthStore((s) => s.user)!;
  const qc = useQueryClient();
  const me = useQuery({
    queryKey: ['dashboard-me'],
    queryFn: async () => (await api.get<SellerDashboard>('/dashboard/me')).data
  });

  async function toggleSending(enabled: boolean) {
    try {
      await api.post(`/sellers/${user.sellerId}/sending`, { enabled });
      toast.success(enabled ? 'Envío activado' : 'Envío pausado');
      qc.invalidateQueries();
    } catch (err: any) {
      toast.error(err.response?.data?.error ?? 'No se pudo cambiar');
    }
  }

  if (me.isLoading || !me.data) return <div>Cargando…</div>;
  const m = me.data.metrics;

  return (
    <div className="space-y-6 max-w-2xl">
      <h1 className="text-2xl font-bold">Conectar WhatsApp</h1>
      <p className="text-sm text-slate-500">
        Escaneá el QR con tu celular (Configuración → Dispositivos vinculados). Una vez conectado,
        activá el envío automático y SalesHub va a ir mandando leads respetando tus gauges humanizados.
      </p>

      <QrPanel sellerId={user.sellerId} currentStatus={m.instanceStatus} />

      <div className="card p-6 flex items-center justify-between">
        <div>
          <div className="font-semibold">Envío automático</div>
          <div className="text-sm text-slate-500">Cap hoy: {m.todaySent}/{m.todayCap} — En cola: {me.data.queuedCount}</div>
        </div>
        <label className="inline-flex items-center cursor-pointer gap-2">
          <span className="text-sm">{m.sendingEnabled ? 'On' : 'Off'}</span>
          <input type="checkbox" className="sr-only peer" checked={m.sendingEnabled}
            onChange={(e) => toggleSending(e.target.checked)} />
          <div className="w-11 h-6 bg-slate-200 rounded-full peer peer-checked:bg-brand-600 relative transition">
            <div className={`absolute top-0.5 ${m.sendingEnabled ? 'left-5' : 'left-0.5'} w-5 h-5 bg-white rounded-full transition-all`} />
          </div>
        </label>
      </div>
    </div>
  );
}
