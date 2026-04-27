import { useEffect, useRef, useState } from 'react';
import { useQuery, useQueryClient, useMutation } from '@tanstack/react-query';
import { useSearchParams } from 'react-router-dom';
import { api } from '../lib/api';
import toast from 'react-hot-toast';
import clsx from 'clsx';

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

  return (
    <div className="grid grid-cols-12 gap-4 h-[calc(100vh-8rem)]">
      <div className="col-span-4 card overflow-y-auto">
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

      <div className="col-span-8 card flex flex-col overflow-hidden">
        {!selected ? (
          <div className="flex-1 grid place-items-center text-slate-500">Seleccioná una conversación</div>
        ) : thread.isLoading ? (
          <div className="flex-1 grid place-items-center text-slate-500">Cargando…</div>
        ) : thread.data ? (
          <>
            <div className="p-3 border-b border-slate-100">
              <div className="font-semibold">{thread.data.leadName}</div>
              <div className="text-xs text-slate-500">
                {thread.data.productKey} · {thread.data.status} · {thread.data.whatsappPhone ?? '—'}
              </div>
            </div>
            <div className="flex-1 overflow-y-auto p-4 space-y-2 bg-slate-50">
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
                    'max-w-md rounded-lg px-3 py-2 text-sm whitespace-pre-wrap',
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
                {sendMut.isPending ? 'Enviando…' : 'Enviar'}
              </button>
            </form>
          </>
        ) : null}
      </div>
    </div>
  );
}
