import { useEffect, useRef, useState } from 'react';
import { useQuery, useQueryClient, useMutation } from '@tanstack/react-query';
import { useSearchParams } from 'react-router-dom';
import { api } from '../lib/api';
import toast from 'react-hot-toast';
import clsx from 'clsx';
import type { Product } from '../lib/types';

type ListItem = {
  leadId: string;
  leadName: string;
  city?: string;
  productKey: string;
  status: string;
  lastMessageText?: string;
  lastDirection?: 'Outbound' | 'Inbound';
  lastTimestamp?: string;
  unreadCount: number;
};

type Message = {
  id: string;
  direction: 'Outbound' | 'Inbound';
  text: string;
  timestamp: string;
  status: string;
  isRead: boolean;
};

type Thread = {
  leadId: string;
  leadName: string;
  whatsappPhone?: string;
  renderedInitialMessage?: string;
  productKey: string;
  status: string;
  messages: Message[];
};

export default function Conversations() {
  const qc = useQueryClient();
  const [params, setParams] = useSearchParams();
  const selected = params.get('lead');
  const [reply, setReply] = useState('');
  const endRef = useRef<HTMLDivElement>(null);

  const list = useQuery({
    queryKey: ['conversations'],
    queryFn: async () => (await api.get<ListItem[]>('/conversations')).data,
    refetchInterval: 15000
  });

  const productsQ = useQuery({
    queryKey: ['products-min'],
    queryFn: async () => (await api.get<Product[]>('/products')).data,
    staleTime: 5 * 60_000
  });

  const thread = useQuery({
    queryKey: ['conv-thread', selected],
    enabled: !!selected,
    queryFn: async () => (await api.get<Thread>(`/conversations/${selected}`)).data,
    refetchInterval: 10000
  });

  useEffect(() => {
    endRef.current?.scrollIntoView({ behavior: 'smooth' });
  }, [thread.data?.messages.length, selected]);

  const sendMut = useMutation({
    mutationFn: async () => (await api.post(`/conversations/${selected}/reply`, { text: reply })).data,
    onSuccess: () => {
      setReply('');
      qc.invalidateQueries({ queryKey: ['conv-thread', selected] });
      qc.invalidateQueries({ queryKey: ['conversations'] });
      qc.invalidateQueries({ queryKey: ['unread-count'] });
    },
    onError: (err: any) => toast.error(err.response?.data?.error ?? 'No se pudo enviar')
  });

  const clearSelected = () => setParams({});

  return (
    <div className="flex flex-col md:grid md:grid-cols-12 gap-4 h-[calc(100vh-9rem)] md:h-[calc(100vh-8rem)]">
      <div
        className={clsx(
          'md:col-span-4 card overflow-y-auto min-h-0',
          selected ? 'hidden md:block' : 'flex-1 md:flex-none'
        )}>
        <div className="p-3 border-b border-slate-100">
          <h2 className="font-semibold text-lg">Conversaciones</h2>
          <p className="text-xs text-slate-500">Leads que te respondieron o a los que ya escribiste.</p>
        </div>
        {list.isLoading && <div className="p-4 text-sm text-slate-500">Cargando…</div>}
        {list.data?.length === 0 && (
          <div className="p-4 text-sm text-slate-500">
            Ninguna conversación todavía. Van a aparecer acá cuando los leads respondan a los mensajes que mandaste.
          </div>
        )}
        {(list.data ?? []).map((c) => (
          <button
            key={c.leadId}
            onClick={() => setParams({ lead: c.leadId })}
            className={clsx(
              'w-full text-left p-3 border-b border-slate-100 hover:bg-slate-50',
              selected === c.leadId && 'bg-brand-50'
            )}>
            <div className="flex justify-between items-start gap-2">
              <div className="font-medium truncate">{c.leadName}</div>
              {c.unreadCount > 0 && (
                <span className="badge bg-rose-500 text-white text-xs">{c.unreadCount}</span>
              )}
            </div>
            <div className="text-xs text-slate-500 truncate">
              {c.lastDirection === 'Outbound' ? 'Vos: ' : ''}
              {c.lastMessageText?.slice(0, 80) ?? '(sin mensajes)'}
            </div>
            <div className="text-xs text-slate-400 mt-0.5 flex gap-2">
              <span>{c.productKey}</span>
              {c.city && <span>· {c.city}</span>}
              <span className="ml-auto">{c.lastTimestamp ? new Date(c.lastTimestamp).toLocaleString('es-AR', { hour: '2-digit', minute: '2-digit', day: '2-digit', month: '2-digit' }) : ''}</span>
            </div>
          </button>
        ))}
      </div>

      <div
        className={clsx(
          'md:col-span-8 card flex flex-col overflow-hidden min-h-0',
          selected ? 'flex-1 md:flex-none' : 'hidden md:flex'
        )}>
        {!selected ? (
          <div className="flex-1 grid place-items-center text-slate-500">Seleccioná una conversación</div>
        ) : thread.isLoading ? (
          <div className="flex-1 grid place-items-center text-slate-500">Cargando…</div>
        ) : thread.data ? (
          <>
            <div className="p-3 border-b border-slate-100 flex items-start gap-2">
              <button
                type="button"
                onClick={clearSelected}
                className="md:hidden text-sm text-slate-500 hover:text-slate-700 mt-0.5">
                ←
              </button>
              <div className="min-w-0 flex-1">
                <div className="font-semibold truncate">{thread.data.leadName}</div>
                <div className="text-xs text-slate-500 truncate">
                  {thread.data.productKey} · {thread.data.status} · {thread.data.whatsappPhone ?? '—'}
                </div>
              </div>
            </div>
            <div className="flex-1 overflow-y-auto p-3 md:p-4 space-y-2 bg-slate-50">
              {thread.data.renderedInitialMessage && thread.data.messages.length === 0 && (
                <div className="text-center text-xs text-slate-400 mb-4">
                  Mensaje inicial sugerido (aún no enviado):
                  <div className="mt-2 bg-white border border-slate-200 rounded-lg p-3 text-left text-sm whitespace-pre-wrap max-w-sm mx-auto">
                    {thread.data.renderedInitialMessage}
                  </div>
                </div>
              )}
              {thread.data.messages.map((m) => (
                <div key={m.id}
                  className={clsx('flex', m.direction === 'Outbound' ? 'justify-end' : 'justify-start')}>
                  <div className={clsx(
                    'max-w-[85%] md:max-w-md rounded-lg px-3 py-2 text-sm whitespace-pre-wrap',
                    m.direction === 'Outbound' ? 'bg-brand-600 text-white' : 'bg-white border border-slate-200'
                  )}>
                    <div>{m.text}</div>
                    <div className={clsx('text-xs mt-1', m.direction === 'Outbound' ? 'text-brand-100' : 'text-slate-400')}>
                      {new Date(m.timestamp).toLocaleString('es-AR', { hour: '2-digit', minute: '2-digit', day: '2-digit', month: '2-digit' })}
                      {m.direction === 'Outbound' && ` · ${m.status}`}
                    </div>
                  </div>
                </div>
              ))}
              <div ref={endRef} />
            </div>
            <QuickReplyBar
              templates={
                productsQ.data?.find((p) => p.productKey === thread.data?.productKey)?.replyTemplates ?? []
              }
              onPick={(t) => setReply(t)}
            />
            <form
              className="p-3 border-t border-slate-100 flex gap-2"
              onSubmit={(e) => { e.preventDefault(); if (reply.trim()) sendMut.mutate(); }}>
              <input
                className="input flex-1"
                placeholder="Escribí tu respuesta…"
                value={reply}
                onChange={(e) => setReply(e.target.value)}
                disabled={sendMut.isPending}
              />
              <button className="btn-primary" disabled={sendMut.isPending || !reply.trim()}>
                {sendMut.isPending ? '…' : 'Enviar'}
              </button>
            </form>
          </>
        ) : null}
      </div>
    </div>
  );
}

function QuickReplyBar({ templates, onPick }: { templates: string[]; onPick: (t: string) => void }) {
  if (!templates || templates.length === 0) return null;
  return (
    <div className="px-3 pt-2 border-t border-slate-100 flex flex-wrap gap-1">
      <span className="text-[11px] uppercase tracking-wide text-slate-400 self-center mr-1">
        Respuestas rápidas:
      </span>
      {templates.map((t, i) => (
        <button
          key={i}
          type="button"
          onClick={() => onPick(t)}
          title={t}
          className="text-xs px-2 py-1 rounded border border-slate-200 bg-white hover:bg-slate-50 text-slate-700 max-w-[260px] truncate">
          {t.length > 40 ? t.slice(0, 40) + '…' : t}
        </button>
      ))}
    </div>
  );
}
