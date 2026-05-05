import { useMemo, useState } from 'react';
import { useQuery } from '@tanstack/react-query';
import { Link } from 'react-router-dom';
import clsx from 'clsx';
import { api } from '../lib/api';
import type { Product } from '../lib/types';

export type ConvListItem = {
  leadId: string;
  leadName: string;
  city?: string;
  productKey: string;
  productName?: string;
  sellerId?: string;
  sellerName?: string;
  status: string;
  lastMessageText?: string;
  lastDirection?: 'Outbound' | 'Inbound';
  lastTimestamp?: string;
  unreadCount: number;
  firstReplyAt?: string;
  sentAt?: string;
};

type Bucket = 'all' | 'replied' | 'waiting' | 'cold';

type Props = {
  // Si está, filtra por seller (vista admin de un seller). Si no, backend usa el caller.
  sellerId?: string;
  // Mostrar columna de seller en la tabla (admin vista global).
  showSeller?: boolean;
  // Altura máxima de la lista (default 460).
  maxHeight?: number;
  // Bucket inicial.
  initialBucket?: Bucket;
  // Días para considerar una conversación "cold" (sin respuesta).
  coldDays?: number;
};

const BUCKET_LABELS: Record<Bucket, string> = {
  all: 'Todas',
  replied: 'Respondieron',
  waiting: 'Esperando',
  cold: 'Sin contestar'
};

export default function ConversationsList({
  sellerId,
  showSeller = false,
  maxHeight = 460,
  initialBucket = 'all',
  coldDays = 3
}: Props) {
  const [productKey, setProductKey] = useState('');
  const [bucket, setBucket] = useState<Bucket>(initialBucket);
  const [days, setDays] = useState(30);

  const productsQ = useQuery({
    queryKey: ['products-min'],
    queryFn: async () => (await api.get<Product[]>('/products')).data
  });

  const fromTs = useMemo(() => {
    if (!days) return undefined;
    return new Date(Date.now() - days * 86400_000).toISOString();
  }, [days]);

  const list = useQuery({
    queryKey: ['conversations', sellerId ?? 'me', productKey, bucket, days, coldDays],
    queryFn: async () => (await api.get<ConvListItem[]>('/conversations', {
      params: {
        sellerId: sellerId || undefined,
        productKey: productKey || undefined,
        bucket: bucket === 'all' ? undefined : bucket,
        from: fromTs,
        coldDays
      }
    })).data,
    refetchInterval: 20_000
  });

  const counts = useMemo(() => {
    const data = list.data ?? [];
    if (bucket !== 'all') return null;
    const now = Date.now();
    return {
      replied: data.filter((d) => d.firstReplyAt).length,
      waiting: data.filter((d) => !d.firstReplyAt && d.lastDirection === 'Outbound').length,
      cold: data.filter((d) =>
        !d.firstReplyAt && d.lastTimestamp &&
        (now - new Date(d.lastTimestamp).getTime()) / 86400_000 >= coldDays
      ).length
    };
  }, [list.data, bucket, coldDays]);

  return (
    <div className="card p-3 space-y-3">
      <div className="flex flex-wrap items-end gap-2 justify-between">
        <div className="flex flex-wrap gap-1">
          {(Object.keys(BUCKET_LABELS) as Bucket[]).map((b) => (
            <button
              key={b}
              onClick={() => setBucket(b)}
              className={clsx(
                'text-xs px-2 py-1 rounded border',
                bucket === b
                  ? 'bg-brand-600 text-white border-brand-600'
                  : 'bg-white text-slate-600 border-slate-200 hover:bg-slate-50'
              )}>
              {BUCKET_LABELS[b]}
              {b === 'cold' && counts && counts.cold > 0 && (
                <span className="ml-1 text-rose-600 font-semibold">{counts.cold}</span>
              )}
            </button>
          ))}
        </div>
        <div className="flex flex-wrap items-end gap-2">
          <div>
            <label className="text-[11px] text-slate-500 block">Producto</label>
            <select
              className="input text-sm"
              value={productKey}
              onChange={(e) => setProductKey(e.target.value)}>
              <option value="">Todos</option>
              {(productsQ.data ?? []).map((p) => (
                <option key={p.productKey} value={p.productKey}>{p.displayName}</option>
              ))}
            </select>
          </div>
          <div>
            <label className="text-[11px] text-slate-500 block">Período</label>
            <select className="input text-sm" value={days} onChange={(e) => setDays(Number(e.target.value))}>
              <option value={7}>7 días</option>
              <option value={30}>30 días</option>
              <option value={90}>90 días</option>
              <option value={365}>1 año</option>
            </select>
          </div>
        </div>
      </div>

      <div style={{ maxHeight }} className="overflow-y-auto -mx-3">
        {list.isLoading && <div className="p-4 text-sm text-slate-500">Cargando…</div>}
        {!list.isLoading && (list.data ?? []).length === 0 && (
          <div className="p-4 text-sm text-slate-500">
            {bucket === 'cold' && 'Nada para hacer follow-up — buenas noticias.'}
            {bucket === 'waiting' && 'Sin conversaciones esperando respuesta.'}
            {bucket === 'replied' && 'Todavía no respondieron leads en este período.'}
            {bucket === 'all' && 'Sin conversaciones.'}
          </div>
        )}
        {(list.data ?? []).map((c) => {
          const isCold = !c.firstReplyAt && c.lastTimestamp &&
            (Date.now() - new Date(c.lastTimestamp).getTime()) / 86400_000 >= coldDays;
          return (
            <Link
              key={c.leadId}
              to={`/conversations?lead=${c.leadId}`}
              className="block px-3 py-2 border-b border-slate-100 hover:bg-slate-50">
              <div className="flex items-start justify-between gap-2">
                <div className="min-w-0 flex-1">
                  <div className="flex items-center gap-2">
                    <span className="font-medium truncate">{c.leadName}</span>
                    {c.unreadCount > 0 && (
                      <span className="text-[10px] bg-rose-500 text-white rounded-full px-1.5 py-0.5">
                        {c.unreadCount}
                      </span>
                    )}
                    {c.firstReplyAt && (
                      <span className="text-[10px] bg-emerald-100 text-emerald-700 rounded px-1.5 py-0.5">
                        respondió
                      </span>
                    )}
                    {isCold && (
                      <span className="text-[10px] bg-amber-100 text-amber-700 rounded px-1.5 py-0.5">
                        follow-up
                      </span>
                    )}
                  </div>
                  <div className="text-xs text-slate-500 truncate">
                    {c.lastDirection === 'Outbound' ? 'Vos: ' : ''}
                    {c.lastMessageText?.slice(0, 110) ?? '(sin mensajes)'}
                  </div>
                  <div className="text-[11px] text-slate-400 flex flex-wrap gap-x-2 mt-0.5">
                    <span>{c.productName ?? c.productKey}</span>
                    {c.city && <span>· {c.city}</span>}
                    {showSeller && c.sellerName && <span>· {c.sellerName}</span>}
                  </div>
                </div>
                <div className="text-[11px] text-slate-400 whitespace-nowrap">
                  {c.lastTimestamp
                    ? new Date(c.lastTimestamp).toLocaleString('es-AR', {
                        hour: '2-digit', minute: '2-digit', day: '2-digit', month: '2-digit'
                      })
                    : ''}
                </div>
              </div>
            </Link>
          );
        })}
      </div>
    </div>
  );
}
