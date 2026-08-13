import { useState } from 'react';
import { Link } from 'react-router-dom';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import toast from 'react-hot-toast';
import clsx from 'clsx';
import { api } from '../lib/api';
import type { Product } from '../lib/types';

/**
 * CRM: los mismos leads, vistos como pipeline. Las etapas son el LeadStatus de siempre
 * agrupado, así que mover una tarjeta acá mueve el estado real que miran las métricas,
 * el follow-up y los workers — no hay un pipeline paralelo que se desincronice.
 */

type Card = {
  id: string; name: string; city?: string; productKey: string; productName?: string;
  phone?: string; status: string; stageKey: string; source: string;
  sellerId?: string; sellerName?: string; deviceId?: string; deviceName?: string;
  lastActivityAt?: string; nextActionAt?: string; nextActionNote?: string;
  noteCount: number; lastNote?: string; unreadCount: number; score: number; createdAt: string;
};
type Column = { key: string; label: string; total: number; cards: Card[] };
type Board = { stages: Column[]; total: number; overdue: number; perStage: number };
type Note = { id: string; text: string; kind: string; createdAt: string; sellerId?: string; sellerName?: string };
type Detail = {
  id: string; name: string; phone?: string; city?: string; province?: string; website?: string;
  instagram?: string; productKey: string; productName?: string; status: string; source: string;
  score: number; sellerName?: string; createdAt: string; sentAt?: string; firstReplyAt?: string;
  demoScheduledAt?: string; closedAt?: string; nextActionAt?: string; nextActionNote?: string;
  legacyNotes?: string; notes: Note[];
  messages: {
    direction: string; text: string; timestamp: string;
    /** Instancia o celular por el que viajó el mensaje. */
    line?: string;
    /** Número real de esa línea, cuando lo tenemos guardado. */
    linePhone?: string;
    isDevice: boolean;
  }[];
};
type Device = { id: string; name: string };

const SOURCES = [
  { value: 'MetaLeadAd', label: 'Meta Lead Ads' },
  { value: 'WhatsAppAd', label: 'Click to WhatsApp' },
  { value: 'WhatsAppInbound', label: 'Escribió al WhatsApp' },
  { value: 'ProductOnboarding', label: 'Onboarding del producto' },
  { value: 'ProductReengage', label: 'Re-enganche' },
  { value: 'GooglePlaces', label: 'Google Maps' },
  { value: 'ApifyGoogleMaps', label: 'Maps (Apify)' },
  { value: 'InstagramScraper', label: 'Instagram' },
];

/** Mismo agrupamiento que usa el backend (CrmController.Stages). */
const STATUS_TO_STAGE: Record<string, string> = {
  New: 'nuevo', Assigned: 'nuevo', Queued: 'nuevo',
  Sent: 'contactado',
  Replied: 'respondio',
  Interested: 'interesado',
  DemoScheduled: 'demo',
  Closed: 'ganado',
  Lost: 'perdido', Blocked: 'perdido', NoWhatsApp: 'perdido',
};

/**
 * Por dónde viajó un mensaje: el número real de la línea si lo tenemos, si no el
 * nombre del celular o de la instancia. Los mensajes viejos no traen línea.
 */
const lineLabel = (m: { line?: string; linePhone?: string; isDevice: boolean; direction: string }) => {
  const via = m.direction === 'Inbound' ? 'entró por' : 'salió por';
  if (m.linePhone) return `${via} +${m.linePhone}`;
  if (m.line) return `${via} ${m.isDevice ? `celu ${m.line}` : m.line}`;
  return '';
};

const hace = (iso?: string) => {
  if (!iso) return 'sin actividad';
  const min = Math.floor((Date.now() - new Date(iso).getTime()) / 60000);
  if (min < 1) return 'recién';
  if (min < 60) return `hace ${min} min`;
  if (min < 1440) return `hace ${Math.floor(min / 60)} h`;
  return `hace ${Math.floor(min / 1440)} d`;
};
const fmtDate = (iso?: string) =>
  iso ? new Date(iso).toLocaleDateString('es-AR', { day: '2-digit', month: '2-digit' }) : '—';
const fmtDateTime = (iso?: string) =>
  iso ? new Date(iso).toLocaleString('es-AR', { day: '2-digit', month: '2-digit', hour: '2-digit', minute: '2-digit' }) : '—';
/** ISO → valor para <input type="datetime-local"> en hora local. */
const toLocalInput = (iso?: string) => {
  if (!iso) return '';
  const d = new Date(iso);
  const off = d.getTimezoneOffset();
  return new Date(d.getTime() - off * 60000).toISOString().slice(0, 16);
};

export default function Crm() {
  const qc = useQueryClient();
  const [q, setQ] = useState('');
  const [productKey, setProductKey] = useState('');
  const [deviceId, setDeviceId] = useState('');
  const [source, setSource] = useState('');
  const [quick, setQuick] = useState<'' | 'mine' | 'overdue' | 'today' | 'stalled'>('');
  const [openLead, setOpenLead] = useState<string | null>(null);
  const [dragging, setDragging] = useState<string | null>(null);
  const [overStage, setOverStage] = useState<string | null>(null);

  const filters = {
    q: q.trim() || undefined,
    productKey: productKey || undefined,
    deviceId: deviceId || undefined,
    source: source || undefined,
    onlyMine: quick === 'mine' || undefined,
    due: quick === 'overdue' ? 'overdue' : quick === 'today' ? 'today' : undefined,
    stalledDays: quick === 'stalled' ? 7 : undefined,
  };

  const board = useQuery({
    queryKey: ['crm-board', filters],
    queryFn: async () => (await api.get<Board>('/crm/board', { params: filters })).data,
    refetchInterval: 60000,
  });

  const products = useQuery({
    queryKey: ['products-min'],
    queryFn: async () => (await api.get<Product[]>('/products')).data,
    staleTime: 5 * 60_000,
    // Sin la fila de producto vacío, que no es una app real (ver Devices.tsx).
    select: (rows) => rows.filter((p) => p.productKey?.trim()),
  });
  const devices = useQuery({
    queryKey: ['devices-for-conv-filter'],
    queryFn: async () => (await api.get<Device[]>('/devices')).data,
    staleTime: 60_000,
  });

  async function move(leadId: string, stage: string) {
    try {
      await api.patch(`/crm/leads/${leadId}/stage`, { stage });
      qc.invalidateQueries({ queryKey: ['crm-board'] });
      qc.invalidateQueries({ queryKey: ['crm-lead', leadId] });
    } catch (e: any) {
      toast.error(e.response?.data?.error ?? 'No se pudo mover');
    }
  }

  const cols = board.data?.stages ?? [];

  return (
    <div className="space-y-4">
      <div className="flex items-baseline justify-between flex-wrap gap-2">
        <div>
          <h1 className="text-xl md:text-2xl font-bold">CRM</h1>
          <p className="text-sm text-slate-500">
            Arrastrá una tarjeta para moverla de etapa. Cada lead guarda sus notas y su próxima acción.
          </p>
        </div>
        <div className="text-sm text-slate-500">
          {board.data ? `${board.data.total} leads` : '…'}
          {board.data && board.data.overdue > 0 && (
            <span className="ml-2 text-red-600 font-medium">{board.data.overdue} vencidos</span>
          )}
        </div>
      </div>

      {/* ══ Filtros rápidos ══ */}
      <div className="card p-3 space-y-3">
        <div className="flex flex-wrap gap-2">
          <input
            className="input text-sm flex-1 min-w-[200px]"
            placeholder="Buscar por nombre, teléfono o ciudad…"
            value={q}
            onChange={(e) => setQ(e.target.value)}
          />
          <select className="input text-sm w-40" value={productKey} onChange={(e) => setProductKey(e.target.value)}>
            <option value="">Todas las apps</option>
            {(products.data ?? []).map((p) => (
              <option key={p.productKey} value={p.productKey}>{p.displayName}</option>
            ))}
          </select>
          <select className="input text-sm w-36" value={deviceId} onChange={(e) => setDeviceId(e.target.value)}>
            <option value="">Todos los celus</option>
            {(devices.data ?? []).map((d) => (
              <option key={d.id} value={d.id}>{d.name}</option>
            ))}
          </select>
          <select className="input text-sm w-44" value={source} onChange={(e) => setSource(e.target.value)}>
            <option value="">Todos los orígenes</option>
            {SOURCES.map((s) => (
              <option key={s.value} value={s.value}>{s.label}</option>
            ))}
          </select>
        </div>
        <div className="flex flex-wrap gap-1.5">
          <Chip active={quick === ''} onClick={() => setQuick('')}>Todos</Chip>
          <Chip active={quick === 'mine'} onClick={() => setQuick('mine')}>Míos</Chip>
          <Chip active={quick === 'overdue'} onClick={() => setQuick('overdue')} tone="red">Vencidos</Chip>
          <Chip active={quick === 'today'} onClick={() => setQuick('today')}>Para hoy</Chip>
          <Chip active={quick === 'stalled'} onClick={() => setQuick('stalled')}>Sin tocar +7 días</Chip>
          {(q || productKey || deviceId || source || quick) && (
            <button
              className="text-xs text-slate-500 hover:text-slate-700 underline ml-1"
              onClick={() => { setQ(''); setProductKey(''); setDeviceId(''); setSource(''); setQuick(''); }}>
              limpiar
            </button>
          )}
        </div>
      </div>

      {/* ══ Tablero ══ */}
      {board.isLoading ? (
        <div className="text-slate-500">Cargando…</div>
      ) : (
        <div className="flex gap-3 overflow-x-auto pb-3">
          {cols.map((col) => (
            <div
              key={col.key}
              onDragOver={(e) => { e.preventDefault(); setOverStage(col.key); }}
              onDragLeave={() => setOverStage((s) => (s === col.key ? null : s))}
              onDrop={(e) => {
                e.preventDefault();
                setOverStage(null);
                const id = dragging ?? e.dataTransfer.getData('text/plain');
                setDragging(null);
                if (id) move(id, col.key);
              }}
              className={clsx(
                'w-[280px] shrink-0 rounded-lg border bg-slate-50/60 flex flex-col max-h-[calc(100vh-260px)]',
                overStage === col.key ? 'border-brand-400 bg-brand-50/60' : 'border-slate-200'
              )}>
              <div className="px-3 py-2 border-b border-slate-200 flex items-center justify-between sticky top-0 bg-inherit rounded-t-lg">
                <span className="font-semibold text-sm">{col.label}</span>
                <span className="text-xs text-slate-500 tabular-nums">{col.total}</span>
              </div>
              <div className="p-2 space-y-2 overflow-y-auto">
                {col.cards.map((c) => (
                  <CrmCard
                    key={c.id}
                    card={c}
                    onOpen={() => setOpenLead(c.id)}
                    onDragStart={() => setDragging(c.id)}
                    onDragEnd={() => setDragging(null)}
                  />
                ))}
                {col.cards.length === 0 && (
                  <div className="text-xs text-slate-400 text-center py-6">vacío</div>
                )}
                {col.total > col.cards.length && (
                  <div className="text-[11px] text-slate-400 text-center py-1">
                    +{col.total - col.cards.length} más
                  </div>
                )}
              </div>
            </div>
          ))}
        </div>
      )}

      {openLead && (
        <LeadDrawer
          leadId={openLead}
          stages={cols.map((c) => ({ key: c.key, label: c.label }))}
          onClose={() => setOpenLead(null)}
          onMove={move}
        />
      )}
    </div>
  );
}

function Chip({ children, active, onClick, tone }: {
  children: React.ReactNode; active: boolean; onClick: () => void; tone?: 'red';
}) {
  return (
    <button
      onClick={onClick}
      className={clsx(
        'text-xs px-2.5 py-1 rounded-full border transition',
        active
          ? tone === 'red' ? 'bg-red-600 text-white border-red-600' : 'bg-brand-600 text-white border-brand-600'
          : 'bg-white text-slate-600 border-slate-200 hover:border-slate-300'
      )}>
      {children}
    </button>
  );
}

function CrmCard({ card, onOpen, onDragStart, onDragEnd }: {
  card: Card; onOpen: () => void; onDragStart: () => void; onDragEnd: () => void;
}) {
  const overdue = card.nextActionAt && new Date(card.nextActionAt) < new Date();
  return (
    <div
      draggable
      onDragStart={(e) => { e.dataTransfer.setData('text/plain', card.id); onDragStart(); }}
      onDragEnd={onDragEnd}
      onClick={onOpen}
      className="bg-white border border-slate-200 rounded-md p-2.5 cursor-pointer hover:border-brand-300 hover:shadow-sm transition active:cursor-grabbing">
      <div className="flex items-start justify-between gap-1.5">
        <div className="font-medium text-sm leading-tight truncate flex-1">{card.name}</div>
        {card.unreadCount > 0 && (
          <span className="badge bg-rose-500 text-white text-[10px] shrink-0">{card.unreadCount}</span>
        )}
      </div>
      <div className="text-[11px] text-slate-500 mt-0.5 truncate">
        {card.productKey}{card.city ? ` · ${card.city}` : ''}
      </div>
      {card.lastNote && (
        <div className="text-[11px] text-slate-600 mt-1.5 line-clamp-2 bg-slate-50 rounded px-1.5 py-1">
          {card.lastNote}
        </div>
      )}
      <div className="flex items-center gap-1 flex-wrap mt-1.5">
        {card.nextActionAt && (
          <span className={clsx(
            'text-[10px] px-1.5 py-0.5 rounded font-medium',
            overdue ? 'bg-red-100 text-red-700' : 'bg-amber-100 text-amber-700'
          )}>
            {overdue ? '⚑ ' : ''}{fmtDate(card.nextActionAt)}
          </span>
        )}
        {card.deviceName && (
          <span className="text-[10px] px-1.5 py-0.5 rounded bg-brand-50 text-brand-700">{card.deviceName}</span>
        )}
        {card.noteCount > 0 && <span className="text-[10px] text-slate-400">{card.noteCount} notas</span>}
        <span className="text-[10px] text-slate-400 ml-auto">{hace(card.lastActivityAt)}</span>
      </div>
    </div>
  );
}

/** Ficha lateral: datos, etapa, próxima acción y la bitácora de notas. */
function LeadDrawer({ leadId, stages, onClose, onMove }: {
  leadId: string;
  stages: { key: string; label: string }[];
  onClose: () => void;
  onMove: (leadId: string, stage: string) => Promise<void>;
}) {
  const qc = useQueryClient();
  const [noteText, setNoteText] = useState('');
  const [saving, setSaving] = useState(false);

  const detail = useQuery({
    queryKey: ['crm-lead', leadId],
    queryFn: async () => (await api.get<Detail>(`/crm/leads/${leadId}`)).data,
  });

  const d = detail.data;
  /** Etapa actual derivada del status, para preseleccionar el combo. */
  const currentStage = STATUS_TO_STAGE[d?.status ?? ''] ?? '';

  async function addNote() {
    const text = noteText.trim();
    if (!text) return;
    setSaving(true);
    try {
      await api.post(`/crm/leads/${leadId}/notes`, { text });
      setNoteText('');
      qc.invalidateQueries({ queryKey: ['crm-lead', leadId] });
      qc.invalidateQueries({ queryKey: ['crm-board'] });
    } catch {
      toast.error('No se pudo guardar la nota');
    } finally {
      setSaving(false);
    }
  }

  async function setNextAction(at: string | null, note?: string) {
    try {
      await api.patch(`/crm/leads/${leadId}/next-action`, {
        at: at ? new Date(at).toISOString() : null,
        note: note ?? d?.nextActionNote ?? null,
      });
      qc.invalidateQueries({ queryKey: ['crm-lead', leadId] });
      qc.invalidateQueries({ queryKey: ['crm-board'] });
      toast.success(at ? 'Recordatorio guardado' : 'Recordatorio quitado');
    } catch {
      toast.error('No se pudo guardar');
    }
  }

  return (
    <div className="fixed inset-0 z-50 bg-black/40 flex justify-end" onClick={onClose}>
      <div
        className="bg-white w-full max-w-md h-full overflow-y-auto shadow-xl"
        onClick={(e) => e.stopPropagation()}>
        {!d ? (
          <div className="p-6 text-slate-500">Cargando…</div>
        ) : (
          <div className="p-4 space-y-4">
            <div className="flex items-start justify-between gap-2">
              <div className="min-w-0">
                <h2 className="text-lg font-bold truncate">{d.name}</h2>
                <p className="text-xs text-slate-500">
                  {d.productName ?? d.productKey} · {d.status}
                  {d.city ? ` · ${d.city}` : ''}
                </p>
              </div>
              <button onClick={onClose} className="text-slate-400 hover:text-slate-600 text-xl leading-none">×</button>
            </div>

            <div className="flex gap-2 flex-wrap">
              {d.phone && (
                <a href={`https://wa.me/${d.phone.replace(/\D/g, '')}`} target="_blank" rel="noreferrer"
                   className="btn-primary text-xs">WhatsApp</a>
              )}
              <Link to={`/conversations?lead=${d.id}`} className="btn-secondary text-xs">Ver chat</Link>
              <Link to={`/leads/${d.id}`} className="btn-secondary text-xs">Ficha completa</Link>
            </div>

            {/* ── Etapa (alternativa al arrastre: en el celular no se puede arrastrar) ── */}
            <div className="card p-3 space-y-1">
              <div className="text-sm font-semibold">Etapa</div>
              <select
                className="input text-sm w-full"
                value={currentStage}
                onChange={async (e) => {
                  await onMove(leadId, e.target.value);
                  qc.invalidateQueries({ queryKey: ['crm-lead', leadId] });
                }}>
                {stages.map((s) => (
                  <option key={s.key} value={s.key}>{s.label}</option>
                ))}
              </select>
            </div>

            {/* ── Próxima acción ── */}
            <div className="card p-3 space-y-2">
              <div className="text-sm font-semibold">Próxima acción</div>
              <input
                type="datetime-local"
                className="input text-sm w-full"
                defaultValue={toLocalInput(d.nextActionAt)}
                onChange={(e) => setNextAction(e.target.value || null)}
              />
              <input
                className="input text-sm w-full"
                placeholder="Qué hay que hacer (ej: llamarlo para cerrar)"
                defaultValue={d.nextActionNote ?? ''}
                onBlur={(e) => {
                  if ((e.target.value || '') !== (d.nextActionNote ?? '')) {
                    setNextAction(d.nextActionAt ? toLocalInput(d.nextActionAt) : null, e.target.value);
                  }
                }}
              />
              {d.nextActionAt && (
                <button className="text-xs text-slate-500 hover:text-red-600 underline"
                        onClick={() => setNextAction(null)}>
                  quitar recordatorio
                </button>
              )}
            </div>

            {/* ── Notas ── */}
            <div className="space-y-2">
              <div className="text-sm font-semibold">Notas</div>
              <textarea
                className="input text-sm w-full min-h-[70px]"
                placeholder="Escribí lo que pasó en la charla…"
                value={noteText}
                onChange={(e) => setNoteText(e.target.value)}
                onKeyDown={(e) => { if (e.key === 'Enter' && (e.metaKey || e.ctrlKey)) addNote(); }}
              />
              <div className="flex items-center gap-2">
                <button className="btn-primary text-xs" onClick={addNote} disabled={saving || !noteText.trim()}>
                  Guardar nota
                </button>
                <span className="text-[11px] text-slate-400">Ctrl/Cmd + Enter</span>
              </div>

              {d.legacyNotes && (
                <div className="text-xs text-slate-500 bg-amber-50 border border-amber-100 rounded p-2">
                  <span className="font-medium">Nota vieja del sistema:</span> {d.legacyNotes}
                </div>
              )}

              <div className="divide-y divide-slate-100">
                {d.notes.map((n) => (
                  <div key={n.id} className="py-2">
                    <div className="text-xs text-slate-400 flex gap-2">
                      <span>{fmtDateTime(n.createdAt)}</span>
                      {n.sellerName && <span>· {n.sellerName}</span>}
                      {n.kind !== 'Note' && <span className="text-slate-300">· automático</span>}
                    </div>
                    <div className={clsx('text-sm', n.kind !== 'Note' && 'text-slate-500 italic')}>{n.text}</div>
                  </div>
                ))}
                {d.notes.length === 0 && <div className="text-xs text-slate-400 py-2">Todavía no hay notas.</div>}
              </div>
            </div>

            {/* ── Últimos mensajes ── */}
            {d.messages.length > 0 && (
              <div className="space-y-1">
                <div className="flex items-baseline justify-between gap-2">
                  <div className="text-sm font-semibold">Últimos mensajes</div>
                  {d.phone && <div className="text-[11px] text-slate-500">chat con +{d.phone.replace(/\D/g, '')}</div>}
                </div>
                {d.messages.map((m, i) => (
                  <div key={i} className={clsx(
                    'text-xs rounded p-1.5',
                    m.direction === 'Inbound' ? 'bg-slate-100' : 'bg-brand-50 text-brand-900'
                  )}>
                    <div className="text-[10px] text-slate-400 flex justify-between gap-2">
                      <span>{fmtDateTime(m.timestamp)}</span>
                      <span className="truncate" title={m.line ?? ''}>{lineLabel(m)}</span>
                    </div>
                    {m.text.slice(0, 200)}
                  </div>
                ))}
                <p className="text-[10px] text-slate-400">
                  A la derecha, el número (o el celular) por el que viajó cada mensaje.
                </p>
              </div>
            )}

            {/* ── Hitos ── */}
            <div className="text-xs text-slate-500 space-y-0.5 border-t border-slate-100 pt-3">
              <div>Entró: {fmtDateTime(d.createdAt)} · origen {d.source}</div>
              {d.sentAt && <div>Primer contacto: {fmtDateTime(d.sentAt)}</div>}
              {d.firstReplyAt && <div>Respondió: {fmtDateTime(d.firstReplyAt)}</div>}
              {d.demoScheduledAt && <div>Demo: {fmtDateTime(d.demoScheduledAt)}</div>}
              {d.closedAt && <div>Cerrado: {fmtDateTime(d.closedAt)}</div>}
              {d.sellerName && <div>Línea: {d.sellerName}</div>}
            </div>
          </div>
        )}
      </div>
    </div>
  );
}
