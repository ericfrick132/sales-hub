import { useEffect, useMemo, useRef, useState } from 'react';
import { useQuery, useQueryClient, useMutation } from '@tanstack/react-query';
import { useSearchParams } from 'react-router-dom';
import { api } from '../lib/api';
import toast from 'react-hot-toast';
import clsx from 'clsx';
import type { Product, Seller } from '../lib/types';
import { isAdmin, useAuthStore } from '../lib/auth';

type ListItem = {
  leadId: string;
  leadName: string;
  city?: string;
  productKey: string;
  status: string;
  sellerId?: string;
  sellerName?: string;
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
  sellerId?: string;
  sellerName?: string;
  aiSuggestedReply?: string;
  messages: Message[];
};

export default function Conversations() {
  const qc = useQueryClient();
  const user = useAuthStore((s) => s.user);
  const admin = isAdmin(user);
  const [params, setParams] = useSearchParams();
  const selected = params.get('lead');
  const [reply, setReply] = useState('');
  const [hideSuggestionFor, setHideSuggestionFor] = useState<string | null>(null);
  const endRef = useRef<HTMLDivElement>(null);

  const [productFilter, setProductFilter] = useState('');
  const [sellerFilter, setSellerFilter] = useState('');
  const [statusFilter, setStatusFilter] = useState('');
  const [days, setDays] = useState(30);
  const fromTs = useMemo(() => {
    if (!days) return undefined;
    return new Date(Date.now() - days * 86400_000).toISOString();
  }, [days]);

  const list = useQuery({
    queryKey: ['conversations', productFilter, sellerFilter, statusFilter, days],
    queryFn: async () => (await api.get<ListItem[]>('/conversations', {
      params: {
        productKey: productFilter || undefined,
        sellerId: sellerFilter || undefined,
        status: statusFilter || undefined,
        from: fromTs
      }
    })).data,
    refetchInterval: 15000
  });

  const productsQ = useQuery({
    queryKey: ['products-min'],
    queryFn: async () => (await api.get<Product[]>('/products')).data,
    staleTime: 5 * 60_000
  });

  const sellersQ = useQuery({
    queryKey: ['sellers-for-conv-filter'],
    enabled: admin,
    queryFn: async () => (await api.get<Seller[]>('/sellers')).data,
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
        <div className="p-3 border-b border-slate-100 space-y-2">
          <div>
            <h2 className="font-semibold text-lg">Conversaciones</h2>
            <p className="text-xs text-slate-500">Leads que te respondieron o a los que ya escribiste.</p>
          </div>
          <div className="flex flex-wrap gap-2">
            <div className="flex-1 min-w-[120px]">
              <label className="text-[11px] text-slate-500 block">Producto</label>
              <select
                className="input text-sm w-full"
                value={productFilter}
                onChange={(e) => setProductFilter(e.target.value)}>
                <option value="">Todos</option>
                {(productsQ.data ?? []).map((p) => (
                  <option key={p.productKey} value={p.productKey}>{p.displayName}</option>
                ))}
              </select>
            </div>
            {admin && (
              <div className="flex-1 min-w-[120px]">
                <label className="text-[11px] text-slate-500 block">Vendedor</label>
                <select
                  className="input text-sm w-full"
                  value={sellerFilter}
                  onChange={(e) => setSellerFilter(e.target.value)}>
                  <option value="">Todos</option>
                  {(sellersQ.data ?? []).filter((s) => s.isActive).map((s) => (
                    <option key={s.id} value={s.id}>{s.displayName}</option>
                  ))}
                </select>
              </div>
            )}
            <div className="flex-1 min-w-[120px]">
              <label className="text-[11px] text-slate-500 block">Estado</label>
              <select
                className="input text-sm w-full"
                value={statusFilter}
                onChange={(e) => setStatusFilter(e.target.value)}>
                <option value="">Todos</option>
                <option value="Interested">Interesado</option>
                <option value="Replied">Respondió</option>
                <option value="DemoScheduled">Demo agendada</option>
                <option value="Closed">Ganado</option>
                <option value="Lost">Perdido</option>
                <option value="Sent">Enviado</option>
                <option value="Blocked">Bloqueado</option>
              </select>
            </div>
            <div className="flex-1 min-w-[120px]">
              <label className="text-[11px] text-slate-500 block">Período</label>
              <select
                className="input text-sm w-full"
                value={days}
                onChange={(e) => setDays(Number(e.target.value))}>
                <option value={0}>Sin límite</option>
                <option value={7}>7 días</option>
                <option value={30}>30 días</option>
                <option value={90}>90 días</option>
                <option value={365}>1 año</option>
              </select>
            </div>
          </div>
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
              <div className="font-medium truncate flex-1 min-w-0">{c.leadName}</div>
              <StatusPill status={c.status} />
              {admin && c.sellerName && (
                <span className="text-[11px] bg-brand-100 text-brand-700 rounded px-1.5 py-0.5 font-medium shrink-0">
                  {c.sellerName}
                </span>
              )}
              {c.unreadCount > 0 && (
                <span className="badge bg-rose-500 text-white text-xs shrink-0">{c.unreadCount}</span>
              )}
            </div>
            <div className="text-xs text-slate-500 truncate">
              {c.lastDirection === 'Outbound'
                ? (admin && c.sellerName ? `${c.sellerName}: ` : 'Vos: ')
                : ''}
              {c.lastMessageText?.slice(0, 80) ?? '(sin mensajes)'}
            </div>
            <div className="text-xs text-slate-400 mt-0.5 flex gap-2 flex-wrap">
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
                <div className="font-semibold truncate flex items-center gap-2">
                  <span className="truncate">{thread.data.leadName}</span>
                  <StatusPill status={thread.data.status} />
                </div>
                <div className="text-xs text-slate-500 truncate">
                  {thread.data.productKey} · {thread.data.whatsappPhone ?? '—'}
                  {admin && thread.data.sellerName && <> · <span className="font-medium">{thread.data.sellerName}</span></>}
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
            {(() => {
              const replies = parseQuickReplies(
                productsQ.data?.find((p) => p.productKey === thread.data?.productKey)?.replyTemplates ?? []
              );
              const suggestion = thread.data?.aiSuggestedReply;
              return (
                <>
                  {suggestion && hideSuggestionFor !== selected && (
                    <div className="px-3 pt-2 border-t border-slate-100">
                      <div className="bg-amber-50 border border-amber-200 rounded-lg p-2.5 text-sm">
                        <div className="flex items-center justify-between gap-2 mb-1">
                          <span className="text-[11px] uppercase tracking-wide text-amber-700 font-medium">
                            💡 Sugerencia IA
                          </span>
                          <div className="flex gap-1.5 shrink-0">
                            <button
                              type="button"
                              onClick={() => { setReply(suggestion); setHideSuggestionFor(selected); }}
                              className="text-xs px-2 py-0.5 rounded bg-amber-600 text-white hover:bg-amber-700">
                              Usar
                            </button>
                            <button
                              type="button"
                              onClick={() => setHideSuggestionFor(selected)}
                              className="text-xs px-2 py-0.5 rounded border border-amber-300 text-amber-700 hover:bg-amber-100">
                              Descartar
                            </button>
                          </div>
                        </div>
                        <div className="text-slate-700 whitespace-pre-wrap">{suggestion}</div>
                      </div>
                    </div>
                  )}
                  <QuickReplyBar replies={replies} onPick={(t) => setReply(t)} />
                  <form
                    className="p-3 border-t border-slate-100 flex gap-2"
                    onSubmit={(e) => { e.preventDefault(); if (reply.trim()) sendMut.mutate(); }}>
                    <input
                      className="input flex-1"
                      placeholder={replies.some(r => r.trigger)
                        ? `Escribí tu respuesta… (probá /${replies.find(r => r.trigger)!.trigger})`
                        : 'Escribí tu respuesta…'}
                      value={reply}
                      onChange={(e) => setReply(expandSlash(e.target.value, replies))}
                      onKeyDown={(e) => {
                        if (e.key === 'Tab') {
                          const expanded = expandSlash(reply + ' ', replies);
                          if (expanded !== reply + ' ') {
                            e.preventDefault();
                            setReply(expanded);
                          }
                        }
                      }}
                      disabled={sendMut.isPending}
                    />
                    <button className="btn-primary" disabled={sendMut.isPending || !reply.trim()}>
                      {sendMut.isPending ? '…' : 'Enviar'}
                    </button>
                  </form>
                </>
              );
            })()}
          </>
        ) : null}
      </div>
    </div>
  );
}

const STATUS_META: Record<string, { label: string; cls: string }> = {
  New:           { label: 'nuevo',         cls: 'bg-slate-100 text-slate-600' },
  Assigned:      { label: 'asignado',      cls: 'bg-slate-100 text-slate-600' },
  Queued:        { label: 'en cola',       cls: 'bg-slate-100 text-slate-600' },
  Sent:          { label: 'enviado',       cls: 'bg-sky-100 text-sky-700' },
  Replied:       { label: 'respondió',     cls: 'bg-indigo-100 text-indigo-700' },
  Interested:    { label: 'interesado',    cls: 'bg-emerald-100 text-emerald-700' },
  DemoScheduled: { label: 'demo agendada', cls: 'bg-violet-100 text-violet-700' },
  Closed:        { label: 'ganado',        cls: 'bg-emerald-600 text-white' },
  Lost:          { label: 'perdido',       cls: 'bg-rose-100 text-rose-700' },
  Blocked:       { label: 'bloqueado',     cls: 'bg-rose-100 text-rose-700' }
};

function StatusPill({ status }: { status?: string }) {
  const m = STATUS_META[status ?? ''] ?? { label: (status ?? '').toLowerCase(), cls: 'bg-slate-100 text-slate-600' };
  return (
    <span className={clsx('text-[10px] px-1.5 py-0.5 rounded font-medium shrink-0 whitespace-nowrap', m.cls)}>
      {m.label}
    </span>
  );
}

type QuickReply = { trigger?: string; content: string; raw: string };

/// Parsea cada línea de replyTemplates. Si arranca con "/<comando> = ..." se
/// considera slash command (tipeable como atajo). Si no, es solo botón.
/// Soporta "\n" literal en el contenido → newline real.
function parseQuickReplies(lines: string[]): QuickReply[] {
  return (lines ?? [])
    .map((line) => line.trim())
    .filter(Boolean)
    .map((line): QuickReply => {
      const m = /^\/([a-zA-Z0-9_-]+)\s*[:=]\s*(.+)$/s.exec(line);
      if (m) {
        return {
          trigger: m[1].toLowerCase(),
          content: m[2].replace(/\\n/g, '\n'),
          raw: line
        };
      }
      return { content: line.replace(/\\n/g, '\n'), raw: line };
    });
}

/// Si `value` termina en `/<trigger> ` (espacio), reemplaza por el contenido
/// del comando match. Si no, devuelve el value sin cambios.
function expandSlash(value: string, replies: QuickReply[]): string {
  const m = /(^|\s)\/([a-zA-Z0-9_-]+)(\s)$/.exec(value);
  if (!m) return value;
  const trigger = m[2].toLowerCase();
  const r = replies.find((x) => x.trigger === trigger);
  if (!r) return value;
  // Reemplaza desde donde empieza el "/", preservando lo de antes.
  const start = m.index + (m[1] === '' ? 0 : m[1].length);
  return value.slice(0, start) + r.content;
}

function QuickReplyBar({ replies, onPick }: { replies: QuickReply[]; onPick: (t: string) => void }) {
  if (!replies || replies.length === 0) return null;
  return (
    <div className="px-3 pt-2 border-t border-slate-100 flex flex-wrap gap-1">
      <span className="text-[11px] uppercase tracking-wide text-slate-400 self-center mr-1">
        Respuestas rápidas:
      </span>
      {replies.map((r, i) => (
        <button
          key={i}
          type="button"
          onClick={() => onPick(r.content)}
          title={r.trigger ? `/${r.trigger}\n\n${r.content}` : r.content}
          className={clsx(
            'text-xs px-2 py-1 rounded border max-w-[260px] truncate',
            r.trigger
              ? 'border-brand-300 bg-brand-50 text-brand-700 hover:bg-brand-100 font-mono'
              : 'border-slate-200 bg-white text-slate-700 hover:bg-slate-50'
          )}>
          {r.trigger
            ? `/${r.trigger}`
            : (r.content.length > 40 ? r.content.slice(0, 40) + '…' : r.content)}
        </button>
      ))}
    </div>
  );
}
