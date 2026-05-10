import { useQuery } from '@tanstack/react-query';
import { Link } from 'react-router-dom';
import { api } from '../lib/api';

type UpcomingItem = {
  id: string;
  leadId: string;
  leadName: string;
  productKey: string;
  productName?: string;
  whatsappPhone: string;
  message: string;
  status: 'Scheduled' | 'Sending' | 'Sent' | 'Failed' | 'Cancelled';
  scheduledAt: string;
  searchCategory?: string;
  hasMedia: boolean;
  mediaMimeType?: string;
  city?: string;
};

function fmtRelative(iso: string): string {
  const ms = new Date(iso).getTime() - Date.now();
  const m = Math.round(ms / 60_000);
  if (m < 0) {
    const past = -m;
    if (past < 60) return `hace ${past}m`;
    return `hace ${Math.round(past / 60)}h`;
  }
  if (m < 1) return 'ahora';
  if (m < 60) return `en ${m}m`;
  const h = Math.floor(m / 60);
  const rem = m % 60;
  return rem === 0 ? `en ${h}h` : `en ${h}h ${rem}m`;
}

function fmtAbsolute(iso: string): string {
  const d = new Date(iso);
  return d.toLocaleString('es-AR', {
    day: '2-digit', month: '2-digit', hour: '2-digit', minute: '2-digit'
  });
}

function kindIcon(it: UpcomingItem): string {
  if (!it.hasMedia) return '💬';
  const m = it.mediaMimeType ?? '';
  if (m.startsWith('audio/')) return '🎤';
  if (m.startsWith('image/')) return '🖼️';
  return '📄';
}

export default function UpcomingSends() {
  const q = useQuery({
    queryKey: ['upcoming-sends'],
    queryFn: async () => (await api.get<UpcomingItem[]>('/dashboard/outbox', {
      params: { status: 'Scheduled', order: 'upcoming', limit: 15 }
    })).data,
    refetchInterval: 15_000
  });

  const items = q.data ?? [];

  return (
    <div className="card p-4 space-y-2">
      <div className="flex items-center justify-between">
        <div>
          <h2 className="font-semibold">Próximos envíos</h2>
          <div className="text-xs text-slate-500">Lo que va a salir desde tu WhatsApp en orden de despacho.</div>
        </div>
        <span className="text-xs text-slate-400">
          {q.isFetching && 'Actualizando…'}
        </span>
      </div>

      {q.isLoading ? (
        <div className="text-sm text-slate-500 p-4 text-center">Cargando…</div>
      ) : items.length === 0 ? (
        <div className="text-sm text-slate-500 p-4 text-center bg-slate-50 rounded">
          No hay envíos programados. Asigná leads o tomálos del Pool para que se enculen.
        </div>
      ) : (
        <div className="divide-y divide-slate-100">
          {items.map((it) => (
            <Link
              key={it.id}
              to={`/leads/${it.leadId}`}
              className="flex items-center gap-3 py-2 hover:bg-slate-50 px-1">
              <div className="text-xl shrink-0">{kindIcon(it)}</div>
              <div className="flex-1 min-w-0">
                <div className="flex items-center gap-2 flex-wrap">
                  <span className="font-medium truncate">{it.leadName}</span>
                  {it.searchCategory && (
                    <span className="text-[10px] bg-brand-50 text-brand-700 rounded px-1.5">{it.searchCategory}</span>
                  )}
                </div>
                <div className="text-xs text-slate-500 truncate">
                  {it.productName ?? it.productKey}
                  {it.city && <> · {it.city}</>}
                  {' · '}{it.whatsappPhone}
                </div>
              </div>
              <div className="text-right text-xs whitespace-nowrap shrink-0">
                <div className="font-medium text-slate-700">{fmtRelative(it.scheduledAt)}</div>
                <div className="text-slate-400">{fmtAbsolute(it.scheduledAt)}</div>
              </div>
            </Link>
          ))}
        </div>
      )}
    </div>
  );
}
