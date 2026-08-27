import { useEffect, useMemo, useRef, useState } from 'react';
import { useQuery, useQueryClient, useMutation } from '@tanstack/react-query';
import { Link, useSearchParams } from 'react-router-dom';
import { api } from '../lib/api';
import toast from 'react-hot-toast';
import clsx from 'clsx';
import type { Product } from '../lib/types';
import { isAdmin, useAuthStore } from '../lib/auth';

type ListItem = {
  leadId: string;
  leadName: string;
  city?: string;
  productKey: string;
  status: string;
  sellerId?: string;
  sellerName?: string;
  deviceId?: string;
  deviceName?: string;
  lastMessageText?: string;
  lastDirection?: 'Outbound' | 'Inbound';
  lastTimestamp?: string;
  unreadCount: number;
  source: string;
  tags: string[];
  adTitle?: string | null;
  lastInboundAt?: string | null;
  windowExpiresAt?: string | null;
  closedAt?: string | null;
  botMutedAt?: string | null;
  pitchName?: string | null;
  pitchStep?: number | null;
  pitchSteps?: number | null;
  pitchActive: boolean;
};

type Device = { id: string; name: string; sellerId?: string; sellerName?: string; status: string };

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
  botMutedAt?: string | null;
  messages: Message[];
  source: string;
  tags: string[];
  adId?: string | null;
  adTitle?: string | null;
  adSourceUrl?: string | null;
  lastInboundAt?: string | null;
  windowExpiresAt?: string | null;
  closedAt?: string | null;
  createdAt: string;
  firstMessageAt?: string | null;
  messagesCount: number;
  lastActiveAt?: string | null;
  pitch?: { pitchId: string; name: string; stepIndex: number; stepsTotal: number; followupsSent: number; completed: boolean; gaveUp: boolean; nextStepDueAt?: string | null } | null;
  feedback: { id: string; rating: number; note?: string | null; ratedMessage?: string | null; sellerName?: string | null; createdAt: string }[];
  city?: string | null;
  score: number;
};

type WindowFilter = '' | '12h+' | '6-12h' | '<6h' | 'expired' | 'new';
const WINDOW_CHIPS: { key: WindowFilter; label: string; dot?: string }[] = [
  { key: '', label: 'Todas' },
  { key: '12h+', label: '12h+', dot: 'bg-emerald-500' },
  { key: '6-12h', label: '6-12h', dot: 'bg-amber-400' },
  { key: '<6h', label: '<6h', dot: 'bg-rose-500' },
  { key: 'expired', label: 'Vencidas', dot: 'bg-slate-300' },
  { key: 'new', label: 'Nuevas' }
];

const SOURCE_LABEL: Record<string, string> = {
  WhatsAppAd: 'Anuncio WhatsApp', WhatsAppInbound: 'Escribió solo', MetaLeadAd: 'Form de Meta', DemoSignup: 'Se registró en la app',
  ProductReengage: 'Re-enganche', ProductOnboarding: 'Onboarding app', GooglePlaces: 'Google Maps', ApifyGoogleMaps: 'Google Maps',
  InstagramScraper: 'Instagram', ManualWhatsApp: 'Manual'
};

/** Cuánto queda de la ventana de 24 h. */
function windowInfo(lastInboundAt?: string | null): { label: string; dot: string; cls: string } {
  if (!lastInboundAt) return { label: 'sin mensajes del lead', dot: 'bg-slate-300', cls: 'text-slate-400' };
  const left = new Date(lastInboundAt).getTime() + 24 * 3600_000 - Date.now();
  if (left <= 0) return { label: 'ventana vencida', dot: 'bg-slate-300', cls: 'text-slate-400' };
  const h = Math.floor(left / 3600_000); const m = Math.floor((left % 3600_000) / 60_000);
  const label = `${h}h ${m.toString().padStart(2, '0')}m`;
  if (h >= 12) return { label, dot: 'bg-emerald-500', cls: 'text-emerald-700' };
  if (h >= 6) return { label, dot: 'bg-amber-400', cls: 'text-amber-700' };
  return { label, dot: 'bg-rose-500', cls: 'text-rose-700' };
}

export default function Conversations() {
  const qc = useQueryClient();
  const user = useAuthStore((s) => s.user);
  const admin = isAdmin(user);
  const [params, setParams] = useSearchParams();
  const selected = params.get('lead');
  const [reply, setReply] = useState('');
  const [hideSuggestionFor, setHideSuggestionFor] = useState<string | null>(null);
  // Prellenado del mini-form "Promover a FAQ" (pregunta = último inbound, respuesta = sugerencia IA).
  const [promoteDraft, setPromoteDraft] = useState<{ productKey: string; question: string; answer: string } | null>(null);
  const endRef = useRef<HTMLDivElement>(null);

  const [productFilter, setProductFilter] = useState('');
  const [deviceFilter, setDeviceFilter] = useState('');
  const [statusFilter, setStatusFilter] = useState('');
  const [days, setDays] = useState(30);
  const [windowFilter, setWindowFilter] = useState<WindowFilter>('');
  const [includeClosed, setIncludeClosed] = useState(false);
  const [tagFilter, setTagFilter] = useState('');
  const [showInfo, setShowInfo] = useState(true);
  const fromTs = useMemo(() => {
    if (!days) return undefined;
    return new Date(Date.now() - days * 86400_000).toISOString();
  }, [days]);

  const list = useQuery({
    queryKey: ['conversations', productFilter, deviceFilter, statusFilter, days, windowFilter, includeClosed, tagFilter],
    queryFn: async () => (await api.get<ListItem[]>('/conversations', {
      params: {
        productKey: productFilter || undefined,
        deviceId: deviceFilter || undefined,
        status: statusFilter || undefined,
        from: fromTs,
        window: windowFilter || undefined,
        includeClosed: includeClosed || undefined,
        tag: tagFilter || undefined
      }
    })).data,
    refetchInterval: 15000
  });

  const productsQ = useQuery({
    queryKey: ['products-min'],
    queryFn: async () => (await api.get<Product[]>('/products')).data,
    staleTime: 5 * 60_000
  });

  const devicesQ = useQuery({
    queryKey: ['devices-for-conv-filter'],
    queryFn: async () => (await api.get<Device[]>('/devices')).data,
    staleTime: 60_000
  });

  const thread = useQuery({
    queryKey: ['conv-thread', selected],
    enabled: !!selected,
    queryFn: async () => (await api.get<Thread>(`/conversations/${selected}`)).data,
    refetchInterval: 10000
  });

  /** Celular que atiende el chat abierto (por la línea a la que está asignado). */
  const threadDevice = useMemo(() => {
    const sid = thread.data?.sellerId;
    if (!sid) return null;
    const dev = (devicesQ.data ?? []).find((d) => d.sellerId === sid);
    return dev?.name ?? (thread.data?.sellerName ? `${thread.data.sellerName} (sin celu)` : null);
  }, [thread.data?.sellerId, thread.data?.sellerName, devicesQ.data]);

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
          selected && showInfo ? 'lg:col-span-3' : '',
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
            <div className="flex-1 min-w-[120px]">
              <label className="text-[11px] text-slate-500 block">Celular</label>
              <select
                className="input text-sm w-full"
                value={deviceFilter}
                onChange={(e) => setDeviceFilter(e.target.value)}>
                <option value="">Todos</option>
                {(devicesQ.data ?? []).map((d) => (
                  <option key={d.id} value={d.id}>{d.name}</option>
                ))}
              </select>
            </div>
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
          <div className="flex flex-wrap items-center gap-1">
            {WINDOW_CHIPS.map((c) => (
              <button key={c.key} type="button"
                onClick={() => setWindowFilter(c.key)}
                title={c.key === '' ? 'Todas' : c.key === 'new' ? 'Leads creados en las últimas 24 h' : 'Tiempo que queda de la ventana de 24 h para responder'}
                className={clsx('text-[11px] px-2 py-0.5 rounded-full border flex items-center gap-1',
                  windowFilter === c.key ? 'bg-slate-800 text-white border-slate-800' : 'bg-white text-slate-600 border-slate-200 hover:bg-slate-50')}>
                {c.dot && <span className={clsx('inline-block w-1.5 h-1.5 rounded-full', c.dot)} />}{c.label}
              </button>
            ))}
            <label className="ml-auto text-[11px] text-slate-500 flex items-center gap-1">
              <input type="checkbox" checked={includeClosed} onChange={(e) => setIncludeClosed(e.target.checked)} /> ver cerradas
            </label>
          </div>
          {tagFilter && (
            <div className="text-[11px] text-slate-500">Filtrando por tag <b>#{tagFilter}</b> <button className="underline" onClick={() => setTagFilter('')}>quitar</button></div>
          )}
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
              <span className={clsx('inline-block w-2 h-2 rounded-full mt-1.5 shrink-0', windowInfo(c.lastInboundAt).dot)} title={`Ventana: ${windowInfo(c.lastInboundAt).label}`} />
              <div className="font-medium truncate flex-1 min-w-0">{c.leadName}</div>
              <StatusPill status={c.status} />
              {(c.deviceName || c.sellerName) && (
                <span
                  className="text-[11px] bg-brand-100 text-brand-700 rounded px-1.5 py-0.5 font-medium shrink-0"
                  title={c.deviceName ? `Celular: ${c.deviceName}` : `Sin celular asignado — línea de ${c.sellerName}`}>
                  {c.deviceName ?? `${c.sellerName} (sin celu)`}
                </span>
              )}
              {c.unreadCount > 0 && (
                <span className="badge bg-rose-500 text-white text-xs shrink-0">{c.unreadCount}</span>
              )}
            </div>
            <div className="text-xs text-slate-500 truncate">
              {c.lastDirection === 'Outbound' ? (c.deviceName ? `${c.deviceName}: ` : 'Nosotros: ') : ''}
              {c.lastMessageText?.slice(0, 80) ?? '(sin mensajes)'}
            </div>
            <div className="text-xs text-slate-400 mt-0.5 flex gap-2 flex-wrap items-center">
              <span>{c.productKey}</span>
              {c.city && <span>· {c.city}</span>}
              {c.source === 'WhatsAppAd' && <span title={c.adTitle ?? 'Lead de anuncio'} className="text-[10px] bg-sky-50 text-sky-700 border border-sky-200 rounded px-1">📣 ad</span>}
              {c.pitchName && (
                <span title={c.pitchName} className={clsx('text-[10px] rounded px-1 border', c.pitchActive ? 'bg-violet-50 text-violet-700 border-violet-200' : 'bg-slate-50 text-slate-500 border-slate-200')}>
                  Pitch {c.pitchStep}/{c.pitchSteps}
                </span>
              )}
              {c.closedAt && <span className="text-[10px] bg-slate-100 text-slate-500 rounded px-1">cerrada</span>}
              {c.tags?.slice(0, 3).map((t) => <span key={t} className="text-[10px] bg-amber-50 text-amber-700 border border-amber-200 rounded px-1">#{t}</span>)}
              <span className="ml-auto">{c.lastTimestamp ? new Date(c.lastTimestamp).toLocaleString('es-AR', { hour: '2-digit', minute: '2-digit', day: '2-digit', month: '2-digit' }) : ''}</span>
            </div>
          </button>
        ))}
      </div>

      <div
        className={clsx(
          'md:col-span-8 card flex flex-col overflow-hidden min-h-0',
          selected && showInfo ? 'lg:col-span-6' : '',
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
                  {threadDevice && <> · <span className="font-medium">{threadDevice}</span></>}
                </div>
              </div>
              <button
                type="button"
                title={thread.data.botMutedAt
                  ? 'Bot pausado (takeover humano). Click para reactivar — o mandá "+" desde el celu.'
                  : 'Bot respondiendo. Click para tomar control — o mandá "-" desde el celu.'}
                onClick={async () => {
                  await api.post(`/conversations/${thread.data!.leadId}/bot`, { enabled: !!thread.data!.botMutedAt });
                  qc.invalidateQueries({ queryKey: ['conv-thread', selected] });
                }}
                className={`text-[11px] px-2 py-1 rounded border shrink-0 ${thread.data.botMutedAt
                  ? 'border-amber-300 bg-amber-50 text-amber-700'
                  : 'border-emerald-300 bg-emerald-50 text-emerald-700'}`}>
                {thread.data.botMutedAt ? '🤖 pausado' : '🤖 activo'}
              </button>
              <button
                type="button"
                title={thread.data.closedAt ? 'Reabrir conversación' : 'Cerrar conversación (se oculta del inbox hasta que el lead vuelva a escribir)'}
                onClick={async () => {
                  await api.post(`/conversations/${thread.data!.leadId}/${thread.data!.closedAt ? 'reopen' : 'close'}`);
                  qc.invalidateQueries({ queryKey: ['conv-thread', selected] });
                  qc.invalidateQueries({ queryKey: ['conversations'] });
                  toast.success(thread.data!.closedAt ? 'Conversación reabierta' : 'Conversación cerrada');
                }}
                className="text-[11px] px-2 py-1 rounded border border-slate-200 text-slate-600 hover:bg-slate-50 shrink-0">
                {thread.data.closedAt ? '↺ Reabrir' : '✕ Cerrar'}
              </button>
              <button type="button" onClick={() => setShowInfo((v) => !v)}
                className="hidden lg:block text-[11px] px-2 py-1 rounded border border-slate-200 text-slate-600 hover:bg-slate-50 shrink-0">
                {showInfo ? 'Ocultar info' : 'Info del lead'}
              </button>
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
                            {admin && (
                              <button
                                type="button"
                                title="Guardar pregunta + respuesta como FAQ canónica del producto: el bot de soporte la responde directo la próxima vez"
                                onClick={() => {
                                  const msgs = thread.data?.messages ?? [];
                                  const lastInbound = [...msgs].reverse().find((m) => m.direction === 'Inbound');
                                  setPromoteDraft({
                                    productKey: thread.data!.productKey,
                                    question: lastInbound?.text ?? '',
                                    answer: suggestion
                                  });
                                }}
                                className="text-xs px-2 py-0.5 rounded border border-amber-300 text-amber-700 hover:bg-amber-100">
                                Promover a FAQ
                              </button>
                            )}
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

      {selected && showInfo && thread.data && (
        <div className="hidden lg:block lg:col-span-3 card overflow-y-auto min-h-0">
          <LeadInfoPanel thread={thread.data} onTagClick={(t) => setTagFilter(t)} onChanged={() => {
            qc.invalidateQueries({ queryKey: ['conv-thread', selected] });
            qc.invalidateQueries({ queryKey: ['conversations'] });
          }} />
        </div>
      )}

      {promoteDraft && (
        <PromoteFaqModal draft={promoteDraft} onClose={() => setPromoteDraft(null)} />
      )}
    </div>
  );
}

/// Panel derecho estilo "Info del Lead": ventana de 24 h, tags, origen/anuncio, pitch,
/// actividad, rating 👍👎 + feedback (que entrena al agente) y accesos al CRM.
function LeadInfoPanel({ thread, onTagClick, onChanged }: { thread: Thread; onTagClick: (t: string) => void; onChanged: () => void }) {
  const [tagInput, setTagInput] = useState('');
  const [note, setNote] = useState('');
  const [rating, setRating] = useState<number>(0);
  const [saving, setSaving] = useState(false);
  const [, setTick] = useState(0);
  useEffect(() => { const id = setInterval(() => setTick((t) => t + 1), 30_000); return () => clearInterval(id); }, []);
  const win = windowInfo(thread.lastInboundAt);
  const fmt = (d?: string | null) => (d ? new Date(d).toLocaleString('es-AR', { day: '2-digit', month: '2-digit', hour: '2-digit', minute: '2-digit' }) : '—');

  async function saveTags(tags: string[]) {
    try { await api.post(`/conversations/${thread.leadId}/tags`, { tags }); onChanged(); }
    catch (e: any) { toast.error(e.response?.data?.error ?? 'No se pudo guardar el tag'); }
  }
  async function sendFeedback(r: number) {
    if (r === 0 && !note.trim()) { toast.error('Poné un comentario o un pulgar'); return; }
    setSaving(true);
    try {
      await api.post(`/conversations/${thread.leadId}/feedback`, { rating: r, note: note.trim() || null });
      toast.success(note.trim() ? 'Feedback guardado — el agente lo va a tener en cuenta' : 'Calificación guardada');
      setNote(''); setRating(0); onChanged();
    } catch (e: any) { toast.error(e.response?.data?.error ?? 'No se pudo guardar'); }
    finally { setSaving(false); }
  }
  async function stopPitch() {
    try { await api.post(`/pitches/enrollments/${thread.leadId}/stop`); toast.success('Lead sacado del pitch'); onChanged(); }
    catch (e: any) { toast.error(e.response?.data?.error ?? 'No se pudo'); }
  }

  return (
    <div className="p-3 space-y-4 text-sm">
      <div>
        <div className="text-[11px] uppercase tracking-wide text-slate-400">Info del lead</div>
        <div className="font-semibold text-base truncate">{thread.leadName}</div>
        <div className="text-xs text-slate-500">{thread.whatsappPhone ?? '—'}{thread.city ? ` · ${thread.city}` : ''}</div>
        <Link to={`/leads/${thread.leadId}`} className="text-xs text-brand-700 hover:underline">Ver en CRM →</Link>
      </div>

      <div>
        <div className="text-[11px] uppercase tracking-wide text-slate-400">Ventana de respuesta</div>
        <div className={clsx('flex items-center gap-2 font-medium', win.cls)}>
          <span className={clsx('inline-block w-2 h-2 rounded-full', win.dot)} />{win.label}
        </div>
        <div className="text-[11px] text-slate-400">24 h desde el último mensaje del lead ({fmt(thread.lastInboundAt)})</div>
      </div>

      <div>
        <div className="text-[11px] uppercase tracking-wide text-slate-400">Tags</div>
        <div className="flex flex-wrap gap-1 mt-1">
          {thread.tags.length === 0 && <span className="text-xs text-slate-400">Sin tags</span>}
          {thread.tags.map((t) => (
            <span key={t} className="text-xs bg-amber-50 text-amber-700 border border-amber-200 rounded px-1.5 py-0.5 flex items-center gap-1">
              <button type="button" onClick={() => onTagClick(t)} title="Filtrar por este tag">#{t}</button>
              <button type="button" onClick={() => saveTags(thread.tags.filter((x) => x !== t))} title="Quitar">×</button>
            </span>
          ))}
        </div>
        <form className="flex gap-1 mt-1" onSubmit={(e) => { e.preventDefault(); if (tagInput.trim()) { saveTags([...thread.tags, tagInput.trim()]); setTagInput(''); } }}>
          <input className="input text-xs py-1 flex-1" placeholder="Agregar tag…" value={tagInput} onChange={(e) => setTagInput(e.target.value)} />
          <button className="btn-secondary text-xs" disabled={!tagInput.trim()}>+</button>
        </form>
      </div>

      <div className="space-y-1">
        <div className="text-[11px] uppercase tracking-wide text-slate-400">Origen</div>
        <div>{SOURCE_LABEL[thread.source] ?? thread.source}</div>
        {thread.adTitle && (
          <div className="text-xs text-slate-600">📣 {thread.adSourceUrl ? <a className="hover:underline" href={thread.adSourceUrl} target="_blank" rel="noreferrer">{thread.adTitle}</a> : thread.adTitle}</div>
        )}
        {thread.adId && !thread.adTitle && <div className="text-xs text-slate-500 font-mono">ad {thread.adId}</div>}
      </div>

      {thread.pitch && (
        <div className="space-y-1 bg-violet-50 border border-violet-200 rounded p-2">
          <div className="text-[11px] uppercase tracking-wide text-violet-600">Pitch</div>
          <div className="font-medium text-violet-800 truncate" title={thread.pitch.name}>{thread.pitch.name}</div>
          <div className="text-xs text-violet-700">
            Paso {thread.pitch.stepIndex}/{thread.pitch.stepsTotal} · {thread.pitch.followupsSent} follow-ups ·{' '}
            {thread.pitch.completed ? 'terminado' : thread.pitch.gaveUp ? 'sin respuesta' : thread.pitch.nextStepDueAt ? `próximo paso ${fmt(thread.pitch.nextStepDueAt)}` : 'esperando respuesta'}
          </div>
          {!thread.pitch.completed && !thread.pitch.gaveUp && (
            <button type="button" className="text-xs text-rose-600 hover:underline" onClick={stopPitch}>Sacar del pitch</button>
          )}
        </div>
      )}

      <div className="grid grid-cols-2 gap-2 text-xs">
        <div><div className="text-[11px] uppercase tracking-wide text-slate-400">Primer mensaje</div>{fmt(thread.firstMessageAt)}</div>
        <div><div className="text-[11px] uppercase tracking-wide text-slate-400">Mensajes</div>{thread.messagesCount}</div>
        <div><div className="text-[11px] uppercase tracking-wide text-slate-400">Última actividad</div>{fmt(thread.lastActiveAt)}</div>
        <div><div className="text-[11px] uppercase tracking-wide text-slate-400">Lead creado</div>{fmt(thread.createdAt)}</div>
      </div>

      <div className="space-y-2 border-t border-slate-100 pt-3">
        <div className="text-[11px] uppercase tracking-wide text-slate-400">Calificar conversación</div>
        <div className="flex gap-2">
          <button type="button" onClick={() => setRating(rating === 1 ? 0 : 1)}
            className={clsx('px-3 py-1 rounded border text-lg', rating === 1 ? 'bg-emerald-50 border-emerald-300' : 'border-slate-200 hover:bg-slate-50')}>👍</button>
          <button type="button" onClick={() => setRating(rating === -1 ? 0 : -1)}
            className={clsx('px-3 py-1 rounded border text-lg', rating === -1 ? 'bg-rose-50 border-rose-300' : 'border-slate-200 hover:bg-slate-50')}>👎</button>
        </div>
        <textarea className="input text-xs min-h-[64px]" placeholder="Feedback… (ej: 'no mandes el precio antes de preguntar cuántos clientes tiene'). El agente lo usa en las próximas respuestas de este producto."
          value={note} onChange={(e) => setNote(e.target.value)} />
        <button type="button" className="btn-primary text-xs w-full" disabled={saving || (rating === 0 && !note.trim())} onClick={() => sendFeedback(rating)}>
          {saving ? 'Guardando…' : '💾 Guardar feedback'}
        </button>
        {thread.feedback.length > 0 && (
          <div className="space-y-1">
            {thread.feedback.map((f) => (
              <div key={f.id} className="text-xs bg-slate-50 border border-slate-100 rounded p-1.5">
                <span>{f.rating > 0 ? '👍' : f.rating < 0 ? '👎' : '📝'}</span> {f.note ?? <span className="text-slate-400">sin nota</span>}
                <div className="text-[10px] text-slate-400">{f.sellerName ?? ''} · {fmt(f.createdAt)}</div>
              </div>
            ))}
          </div>
        )}
      </div>
    </div>
  );
}

/// Mini-form para promover una respuesta aprobada a FAQ canónica del producto.
/// POST /support/faqs/promote — después se administra desde /soporte (tab FAQs).
function PromoteFaqModal({
  draft,
  onClose
}: {
  draft: { productKey: string; question: string; answer: string };
  onClose: () => void;
}) {
  const [question, setQuestion] = useState(draft.question);
  const [answer, setAnswer] = useState(draft.answer);
  const [keywords, setKeywords] = useState('');
  const [saving, setSaving] = useState(false);

  async function promote() {
    setSaving(true);
    try {
      await api.post('/support/faqs/promote', {
        productKey: draft.productKey,
        question: question.trim(),
        answer: answer.trim(),
        keywords: keywords.split(',').map((k) => k.trim()).filter(Boolean)
      });
      toast.success('FAQ promovida — el bot de soporte ya la puede usar');
      onClose();
    } catch (err: any) {
      toast.error(err.response?.data?.error ?? 'No se pudo promover');
    } finally {
      setSaving(false);
    }
  }

  return (
    <div className="fixed inset-0 bg-black/40 grid place-items-center z-50 p-4 overflow-y-auto">
      <div className="card p-5 w-full max-w-lg space-y-3 my-8">
        <h3 className="text-lg font-semibold">
          Promover a FAQ <span className="text-slate-400 font-normal">· {draft.productKey}</span>
        </h3>
        <p className="text-xs text-slate-500">
          La pregunta y la respuesta quedan como FAQ canónica del producto: la próxima vez que un cliente
          pregunte algo parecido, el bot responde esto directo. Se edita después desde Soporte → FAQs.
        </p>
        <label className="text-sm block">
          <div className="text-slate-500 mb-1">Pregunta (como la hizo el cliente)</div>
          <textarea className="input min-h-[56px]" value={question} onChange={(e) => setQuestion(e.target.value)} />
        </label>
        <label className="text-sm block">
          <div className="text-slate-500 mb-1">Respuesta</div>
          <textarea className="input min-h-[96px]" value={answer} onChange={(e) => setAnswer(e.target.value)} />
        </label>
        <label className="text-sm block">
          <div className="text-slate-500 mb-1">Keywords separadas por coma (opcional, ayudan al match)</div>
          <input className="input" placeholder="ej. factura, pago, mercadopago"
            value={keywords} onChange={(e) => setKeywords(e.target.value)} />
        </label>
        <div className="flex justify-end gap-2">
          <button className="btn-secondary" onClick={onClose} disabled={saving}>Cancelar</button>
          <button className="btn-primary" onClick={promote}
            disabled={saving || !question.trim() || !answer.trim()}>
            {saving ? 'Guardando…' : 'Promover'}
          </button>
        </div>
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
