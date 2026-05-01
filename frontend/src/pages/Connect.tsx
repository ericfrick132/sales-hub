import { useQuery, useQueryClient } from '@tanstack/react-query';
import { api } from '../lib/api';
import toast from 'react-hot-toast';
import QrPanel from '../components/QrPanel';
import { useAuthStore } from '../lib/auth';
import type { SellerDashboard } from '../lib/types';

type OutboxStatus = 'Scheduled' | 'Sending' | 'Sent' | 'Failed' | 'Canceled';

interface OutboxItem {
  id: string;
  leadId: string;
  leadName: string;
  productKey: string;
  productName?: string;
  whatsappPhone: string;
  message: string;
  status: OutboxStatus;
  scheduledAt: string;
  sentAt?: string;
  attempts: number;
  error?: string;
}

const OUTBOX_STATUS_LABEL: Record<OutboxStatus, string> = {
  Scheduled: 'Programado',
  Sending: 'Enviando',
  Sent: 'Enviado',
  Failed: 'Fallado',
  Canceled: 'Cancelado'
};

const OUTBOX_STATUS_CLASS: Record<OutboxStatus, string> = {
  Scheduled: 'bg-amber-50 text-amber-700 border-amber-200',
  Sending: 'bg-sky-50 text-sky-700 border-sky-200',
  Sent: 'bg-emerald-50 text-emerald-700 border-emerald-200',
  Failed: 'bg-rose-50 text-rose-700 border-rose-200',
  Canceled: 'bg-slate-50 text-slate-600 border-slate-200'
};

const fmtDateTime = (s?: string) => {
  if (!s) return '—';
  const d = new Date(s);
  const dd = String(d.getDate()).padStart(2, '0');
  const mm = String(d.getMonth() + 1).padStart(2, '0');
  const hh = String(d.getHours()).padStart(2, '0');
  const mi = String(d.getMinutes()).padStart(2, '0');
  return `${dd}/${mm} ${hh}:${mi}`;
};

export default function Connect() {
  const user = useAuthStore((s) => s.user)!;
  const qc = useQueryClient();
  const me = useQuery({
    queryKey: ['dashboard-me'],
    queryFn: async () => (await api.get<SellerDashboard>('/dashboard/me')).data,
    refetchInterval: 10000
  });
  const outbox = useQuery({
    queryKey: ['dashboard-outbox'],
    queryFn: async () => (await api.get<OutboxItem[]>('/dashboard/outbox', { params: { limit: 80 } })).data,
    refetchInterval: 10000
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
      <h1 className="text-xl md:text-2xl font-bold">Conectar WhatsApp</h1>
      <p className="text-sm text-slate-500">
        Escaneá el QR con tu celular (Configuración → Dispositivos vinculados). Una vez conectado,
        activá el envío automático y SalesHub va a ir mandando leads respetando tus gauges humanizados.
      </p>

      <QrPanel sellerId={user.sellerId} currentStatus={m.instanceStatus} />

      <div className="card p-4 md:p-6 flex items-center justify-between gap-3 flex-wrap">
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

      <div className="space-y-2">
        <div className="flex items-center justify-between">
          <h2 className="text-lg font-semibold">Mi cola de envío</h2>
          <button
            className="text-xs text-slate-500 hover:text-slate-700"
            onClick={() => qc.invalidateQueries({ queryKey: ['dashboard-outbox'] })}>
            Refrescar
          </button>
        </div>
        <p className="text-xs text-slate-500">
          Mensajes que SalesHub va a mandar (o ya mandó) por tu WhatsApp. Se actualiza cada 10s.
        </p>
        <div className="card overflow-x-auto">
          <table className="min-w-full text-sm">
            <thead className="bg-slate-50 text-xs uppercase tracking-wide text-slate-500">
              <tr>
                <th className="px-3 py-2 text-left">Estado</th>
                <th className="px-3 py-2 text-left">Lead</th>
                <th className="px-3 py-2 text-left">App</th>
                <th className="px-3 py-2 text-left">WhatsApp</th>
                <th className="px-3 py-2 text-left">Programado</th>
                <th className="px-3 py-2 text-left">Enviado</th>
                <th className="px-3 py-2 text-right">Intentos</th>
                <th className="px-3 py-2 text-left">Error</th>
              </tr>
            </thead>
            <tbody className="divide-y divide-slate-100">
              {outbox.isLoading && (
                <tr><td colSpan={8} className="px-3 py-6 text-center text-slate-500">Cargando…</td></tr>
              )}
              {!outbox.isLoading && (outbox.data ?? []).length === 0 && (
                <tr><td colSpan={8} className="px-3 py-6 text-center text-slate-500">
                  No hay mensajes en la cola. Para que aparezcan, marcá leads como "En cola" desde Mis leads o asignate leads del Pool.
                </td></tr>
              )}
              {(outbox.data ?? []).map((o) => (
                <tr key={o.id} className="hover:bg-slate-50">
                  <td className="px-3 py-2">
                    <span className={`inline-flex items-center rounded border px-2 py-0.5 text-xs ${OUTBOX_STATUS_CLASS[o.status]}`}>
                      {OUTBOX_STATUS_LABEL[o.status]}
                    </span>
                  </td>
                  <td className="px-3 py-2 font-medium">{o.leadName}</td>
                  <td className="px-3 py-2 text-slate-600">{o.productName ?? o.productKey}</td>
                  <td className="px-3 py-2 text-slate-600">{o.whatsappPhone}</td>
                  <td className="px-3 py-2 text-slate-600">{fmtDateTime(o.scheduledAt)}</td>
                  <td className="px-3 py-2 text-slate-600">{fmtDateTime(o.sentAt)}</td>
                  <td className="px-3 py-2 text-right text-slate-600">{o.attempts}</td>
                  <td className="px-3 py-2 text-rose-600 text-xs">{o.error ?? ''}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      </div>
    </div>
  );
}
