import { useEffect, useRef, useState } from 'react';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import toast from 'react-hot-toast';
import clsx from 'clsx';
import { api } from '../lib/api';
import { CRM_SOURCES } from '../lib/types';
import { fmtDateTime, fmtPhone, hace, telHref, toLocalInput } from '../lib/crmFormat';

/**
 * Modo llamadas del CRM: recorre una cola de leads y los va llamando uno atrás del otro.
 *
 * La web no puede saber cuándo termina una llamada (el tel: se lo pasa a FaceTime o a
 * Enlace Móvil y ahí pierde el rastro). Por eso el ritmo lo marca el resultado: al
 * elegir "No atendió", "Interesado", etc. se guarda y en ese mismo click se llama al
 * siguiente. Tiene que ser en el click: el navegador sólo abre tel: con un gesto del usuario.
 */

type QueueItem = {
  id: string; name: string; phone: string; productKey: string; productName?: string; city?: string;
  status: string; stageKey: string; sellerName?: string; nextActionAt?: string; nextActionNote?: string;
  lastNote?: string; callCount: number; lastCallAt?: string; createdAt: string;
};
type Outcome = 'NoAnswer' | 'Answered' | 'Interested' | 'NotInterested' | 'Callback' | 'WrongNumber';
type LeadDetail = {
  notes: { id: string; text: string; kind: string; createdAt: string }[];
  messages: { direction: string; text: string; timestamp: string }[];
};

const OUTCOMES: { key: Outcome; label: string; hint: string; cls: string }[] = [
  { key: 'NoAnswer', label: 'No atendió', hint: 'sólo anota', cls: 'bg-slate-100 text-slate-700 hover:bg-slate-200' },
  { key: 'Answered', label: 'Atendió', hint: 'sólo anota', cls: 'bg-sky-50 text-sky-800 hover:bg-sky-100' },
  { key: 'Interested', label: 'Interesado', hint: 'pasa a Interesados', cls: 'bg-emerald-50 text-emerald-800 hover:bg-emerald-100' },
  { key: 'Callback', label: 'Volver a llamar', hint: 'deja recordatorio', cls: 'bg-amber-50 text-amber-800 hover:bg-amber-100' },
  { key: 'NotInterested', label: 'No le interesa', hint: 'pasa a Perdidos', cls: 'bg-rose-50 text-rose-800 hover:bg-rose-100' },
  { key: 'WrongNumber', label: 'Número equivocado', hint: 'pasa a Perdidos', cls: 'bg-rose-50 text-rose-800 hover:bg-rose-100' },
];

const SKIP_CALLED = [
  { value: 'ever', label: 'Nunca llamados' },
  { value: 'week', label: 'Sin llamar en 7 días' },
  { value: 'today', label: 'Sin llamar hoy' },
  { value: 'no', label: 'Todos, aunque ya los haya llamado' },
];

const DEFAULT_STAGES = ['nuevo', 'contactado', 'respondio', 'interesado', 'demo'];

/** Hoy o dentro de N días a una hora local fija. */
const atHour = (daysAhead: number, hour: number) => {
  const d = new Date();
  d.setDate(d.getDate() + daysAhead);
  d.setHours(hour, 0, 0, 0);
  return d;
};

const mmss = (ms: number) => {
  const s = Math.max(0, Math.floor(ms / 1000));
  return `${String(Math.floor(s / 60)).padStart(2, '0')}:${String(s % 60).padStart(2, '0')}`;
};

export default function CrmCallMode({ filters, stages, onClose }: {
  /** Los mismos filtros que tiene aplicados el tablero. */
  filters: Record<string, unknown>;
  stages: { key: string; label: string }[];
  onClose: () => void;
}) {
  const qc = useQueryClient();
  const [phase, setPhase] = useState<'setup' | 'session' | 'done'>('setup');
  const [chosenStages, setChosenStages] = useState<string[]>(DEFAULT_STAGES);
  const [skipCalled, setSkipCalled] = useState('ever');
  // Arranca con el origen que tenga puesto el tablero, pero se cambia acá sin salir de la ronda.
  const [source, setSource] = useState<string>((filters.source as string) ?? '');

  const [queue, setQueue] = useState<QueueItem[]>([]);
  const [idx, setIdx] = useState(0);
  const [dialedAt, setDialedAt] = useState<number | null>(null);
  /** Cuándo volviste al navegador después de marcar: ahí se da la llamada por terminada. */
  const [returnedAt, setReturnedAt] = useState<number | null>(null);
  const [now, setNow] = useState(Date.now());
  const [note, setNote] = useState('');
  const [callbackOpen, setCallbackOpen] = useState(false);
  const [callbackCustom, setCallbackCustom] = useState('');
  const [autoNext, setAutoNext] = useState(true);
  const [stats, setStats] = useState<Partial<Record<Outcome, number>>>({});
  const noteRef = useRef<HTMLTextAreaElement>(null);

  const params = { ...filters, source: source || undefined, stages: chosenStages.join(','), skipCalled, limit: 300 };
  const preview = useQuery({
    queryKey: ['crm-call-queue', params],
    enabled: phase === 'setup' && chosenStages.length > 0,
    queryFn: async () =>
      (await api.get<{ total: number; items: QueueItem[] }>('/crm/call-queue', { params })).data,
  });

  const current = phase === 'session' ? queue[idx] : undefined;
  const detail = useQuery({
    queryKey: ['crm-lead', current?.id],
    enabled: !!current,
    queryFn: async () => (await api.get<LeadDetail>(`/crm/leads/${current!.id}`)).data,
  });

  // Reloj de la llamada en curso (se para cuando volvés).
  useEffect(() => {
    if (dialedAt === null || returnedAt !== null) return;
    const t = setInterval(() => setNow(Date.now()), 1000);
    return () => clearInterval(t);
  }, [dialedAt, returnedAt]);

  /**
   * Marcar un tel: se lleva la pantalla al teléfono (o a FaceTime/Enlace Móvil). La web no puede
   * saber cuándo termina la llamada, pero sí cuándo VOLVISTE: la pestaña pasa de oculta a
   * visible. Ese es el momento de preguntar cómo salió, con la duración ya contada.
   * El margen de 2 s evita el falso positivo de cuando el navegador no llega a ocultarse.
   */
  useEffect(() => {
    if (dialedAt === null || returnedAt !== null) return;
    const backFromCall = () => {
      if (document.visibilityState !== 'visible') return;
      if (Date.now() - dialedAt < 2000) return;
      setReturnedAt(Date.now());
      setNow(Date.now());
      navigator.vibrate?.(60);
      setTimeout(() => noteRef.current?.focus(), 50);
    };
    document.addEventListener('visibilitychange', backFromCall);
    window.addEventListener('pageshow', backFromCall);
    window.addEventListener('focus', backFromCall);
    return () => {
      document.removeEventListener('visibilitychange', backFromCall);
      window.removeEventListener('pageshow', backFromCall);
      window.removeEventListener('focus', backFromCall);
    };
  }, [dialedAt, returnedAt]);

  function dial(item: QueueItem) {
    window.location.href = telHref(item.phone);
    setDialedAt(Date.now());
    setReturnedAt(null);
    setNow(Date.now());
  }

  function start() {
    const items = preview.data?.items ?? [];
    if (items.length === 0) return;
    setQueue(items);
    setIdx(0);
    setStats({});
    setDialedAt(null);
    setReturnedAt(null);
    setPhase('session');
  }

  /** Pasa al siguiente lead y, si está prendido, lo llama en este mismo click. */
  function advance() {
    const next = idx + 1;
    setNote('');
    setCallbackOpen(false);
    setCallbackCustom('');
    setIdx(next);
    setReturnedAt(null);
    if (next >= queue.length) {
      setDialedAt(null);
      setPhase('done');
      qc.invalidateQueries({ queryKey: ['crm-board'] });
    } else if (autoNext) {
      dial(queue[next]);
    } else {
      setDialedAt(null);
    }
  }

  function record(outcome: Outcome, callbackAt?: Date) {
    if (!current) return;
    const item = current;
    const body = { outcome, note: note.trim() || null, callbackAt: callbackAt?.toISOString() ?? null };
    setStats((s) => ({ ...s, [outcome]: (s[outcome] ?? 0) + 1 }));
    // Primero se llama al siguiente (necesita el gesto del click) y después se guarda.
    advance();
    api.post(`/crm/leads/${item.id}/calls`, body)
      .then(() => qc.invalidateQueries({ queryKey: ['crm-lead', item.id] }))
      .catch((e) => toast.error(
        `No se guardó la llamada a ${item.name}: ${e.response?.data?.error ?? 'error'}` +
        (body.note ? ` (nota: ${body.note})` : ''),
        { duration: 15000 }));
  }

  function skip() {
    advance();
  }

  // Atajos: L llama, S salta, 1-6 eligen el resultado. No se disparan escribiendo la nota.
  useEffect(() => {
    if (phase !== 'session') return;
    const onKey = (e: KeyboardEvent) => {
      const tag = (e.target as HTMLElement)?.tagName;
      if (tag === 'TEXTAREA' || tag === 'INPUT' || tag === 'SELECT' || e.metaKey || e.ctrlKey || e.altKey) return;
      if (!current) return;
      if (dialedAt === null) {
        if (e.key.toLowerCase() === 'l') { e.preventDefault(); dial(current); }
        if (e.key.toLowerCase() === 's') { e.preventDefault(); skip(); }
        return;
      }
      const n = Number(e.key);
      if (n >= 1 && n <= OUTCOMES.length) {
        e.preventDefault();
        const o = OUTCOMES[n - 1].key;
        if (o === 'Callback') setCallbackOpen(true);
        else record(o);
      }
    };
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  });

  function close() {
    qc.invalidateQueries({ queryKey: ['crm-board'] });
    onClose();
  }

  const stageLabel = (key: string) => stages.find((s) => s.key === key)?.label ?? key;
  const totalCalls = Object.values(stats).reduce((a, b) => a + (b ?? 0), 0);

  return (
    // En plena ronda un click afuera no cierra: perdería la cola.
    <div className="fixed inset-0 z-50 bg-black/50 flex items-start md:items-center justify-center p-3"
         onClick={phase === 'session' ? undefined : close}>
      <div className="bg-white w-full max-w-xl rounded-xl shadow-xl max-h-[95vh] overflow-y-auto" onClick={(e) => e.stopPropagation()}>
        <div className="px-4 py-3 border-b border-slate-100 flex items-center justify-between gap-2">
          <div className="font-bold">Modo llamadas</div>
          {phase === 'session' && (
            <div className="text-sm text-slate-500 tabular-nums">{Math.min(idx + 1, queue.length)} de {queue.length}</div>
          )}
          <button onClick={close} className="text-slate-400 hover:text-slate-600 text-xl leading-none">×</button>
        </div>

        {/* ══ Armar la cola ══ */}
        {phase === 'setup' && (
          <div className="p-4 space-y-4">
            <p className="text-sm text-slate-600">
              Llama uno atrás del otro a los leads con teléfono del tablero. Cuando volvés de la llamada te
              pregunta cómo salió, y al elegirlo marca al siguiente en el mismo toque.
            </p>
            <div className="space-y-1.5">
              <div className="text-sm font-semibold">Origen</div>
              <select className="input text-sm w-full" value={source} onChange={(e) => setSource(e.target.value)}>
                <option value="">Todos los orígenes</option>
                {CRM_SOURCES.map((o) => (
                  <option key={o.value} value={o.value}>{o.label}</option>
                ))}
              </select>
            </div>
            <div className="space-y-1.5">
              <div className="text-sm font-semibold">Etapas</div>
              <div className="flex flex-wrap gap-1.5">
                {stages.map((s) => {
                  const on = chosenStages.includes(s.key);
                  return (
                    <button key={s.key}
                      onClick={() => setChosenStages((c) => (on ? c.filter((k) => k !== s.key) : [...c, s.key]))}
                      className={clsx('text-xs px-2.5 py-1 rounded-full border',
                        on ? 'bg-brand-600 text-white border-brand-600' : 'bg-white text-slate-600 border-slate-200')}>
                      {s.label}
                    </button>
                  );
                })}
              </div>
            </div>
            <label className="block space-y-1">
              <div className="text-sm font-semibold">Ya llamados</div>
              <select className="input text-sm w-full" value={skipCalled} onChange={(e) => setSkipCalled(e.target.value)}>
                {SKIP_CALLED.map((o) => <option key={o.value} value={o.value}>{o.label}</option>)}
              </select>
            </label>
            <div className="flex items-center justify-between gap-2 pt-1">
              <div className="text-sm text-slate-500">
                {chosenStages.length === 0 ? 'Elegí al menos una etapa'
                  : preview.isLoading ? 'Contando…'
                  : preview.data ? `${preview.data.total} leads para llamar` + (preview.data.total > preview.data.items.length ? ` (van los primeros ${preview.data.items.length})` : '')
                  : ''}
              </div>
              <button className="btn-primary" disabled={!preview.data?.items.length} onClick={start}>Empezar</button>
            </div>
            <p className="text-[11px] text-slate-400">
              Orden: primero los que tienen un recordatorio vencido, después los más nuevos.
            </p>
          </div>
        )}

        {/* ══ Llamando ══ */}
        {phase === 'session' && current && (
          <div className="p-4 space-y-4">
            <div className="space-y-1">
              <div className="text-xl font-bold leading-tight">{current.name}</div>
              <a href={telHref(current.phone)} className="text-2xl tabular-nums text-brand-700 hover:underline">
                {fmtPhone(current.phone)}
              </a>
              <div className="text-xs text-slate-500">
                {current.productName ?? current.productKey}
                {current.city ? ` · ${current.city}` : ''} · {stageLabel(current.stageKey)}
                {current.sellerName ? ` · ${current.sellerName}` : ''}
              </div>
              <div className="text-xs text-slate-500">
                Entró {hace(current.createdAt)}
                {current.callCount > 0 && ` · llamado ${current.callCount} ${current.callCount === 1 ? 'vez' : 'veces'}, la última ${hace(current.lastCallAt)}`}
              </div>
              {current.nextActionNote && (
                <div className="text-xs bg-amber-50 text-amber-800 rounded px-2 py-1">
                  {fmtDateTime(current.nextActionAt)} · {current.nextActionNote}
                </div>
              )}
            </div>

            {/* Contexto antes de hablar: últimas notas y mensajes. */}
            {detail.data && (detail.data.notes.length > 0 || detail.data.messages.length > 0) && (
              <div className="text-xs space-y-1 max-h-40 overflow-y-auto border border-slate-100 rounded p-2">
                {detail.data.notes.filter((n) => n.kind === 'Note' || n.kind === 'Call').slice(0, 3).map((n) => (
                  <div key={n.id} className="text-slate-600">
                    <span className="text-slate-400">{fmtDateTime(n.createdAt)}</span> {n.text}
                  </div>
                ))}
                {detail.data.messages.slice(-3).map((m, i) => (
                  <div key={i} className={m.direction === 'Inbound' ? 'text-slate-700' : 'text-brand-800'}>
                    <span className="text-slate-400">{m.direction === 'Inbound' ? 'Lead' : 'Nosotros'}:</span> {m.text.slice(0, 160)}
                  </div>
                ))}
              </div>
            )}

            {dialedAt === null ? (
              <div className="flex gap-2">
                <button className="btn-primary flex-1 py-3 text-base" onClick={() => dial(current)}>Llamar <kbd className="text-[10px] opacity-70 ml-1">L</kbd></button>
                <button className="btn-secondary" onClick={skip}>Saltar <kbd className="text-[10px] opacity-60 ml-1">S</kbd></button>
              </div>
            ) : (
              <div className="space-y-3">
                <div className="flex items-center justify-between text-sm">
                  {returnedAt === null ? (
                    <span className="text-emerald-700 font-medium">
                      Llamando · <span className="tabular-nums">{mmss(now - dialedAt)}</span>
                    </span>
                  ) : (
                    <span className="font-medium">
                      ¿Cómo salió? · <span className="tabular-nums text-slate-500">{mmss(returnedAt - dialedAt)}</span>
                    </span>
                  )}
                  <button className="text-xs text-slate-500 underline" onClick={() => dial(current)}>volver a marcar</button>
                </div>
                <textarea
                  ref={noteRef}
                  className="input text-sm w-full min-h-[60px]"
                  placeholder="Nota de la llamada (opcional)…"
                  value={note}
                  onChange={(e) => setNote(e.target.value)}
                />
                <div className={clsx('grid grid-cols-2 gap-2 rounded-lg transition',
                  returnedAt !== null && 'ring-2 ring-emerald-300 ring-offset-2')}>
                  {OUTCOMES.map((o, i) => (
                    <button key={o.key}
                      onClick={() => (o.key === 'Callback' ? setCallbackOpen((v) => !v) : record(o.key))}
                      className={clsx('rounded-lg px-3 py-2 text-left transition', o.cls,
                        o.key === 'Callback' && callbackOpen && 'ring-2 ring-amber-300')}>
                      <div className="text-sm font-semibold">{o.label} <kbd className="text-[10px] opacity-50 ml-1">{i + 1}</kbd></div>
                      <div className="text-[10px] opacity-70">{o.hint}</div>
                    </button>
                  ))}
                </div>
                {callbackOpen && (
                  <div className="rounded-lg border border-amber-200 bg-amber-50/50 p-2 space-y-2">
                    <div className="text-xs text-amber-900 font-medium">¿Cuándo lo volvés a llamar?</div>
                    <div className="flex flex-wrap gap-1.5">
                      <button className="btn-secondary text-xs" onClick={() => record('Callback', new Date(Date.now() + 2 * 3600_000))}>En 2 horas</button>
                      <button className="btn-secondary text-xs" onClick={() => record('Callback', atHour(1, 10))}>Mañana 10:00</button>
                      <button className="btn-secondary text-xs" onClick={() => record('Callback', atHour(1, 17))}>Mañana 17:00</button>
                      <button className="btn-secondary text-xs" onClick={() => record('Callback', atHour(2, 10))}>Pasado mañana 10:00</button>
                    </div>
                    <div className="flex gap-1.5">
                      <input type="datetime-local" className="input text-xs flex-1"
                        min={toLocalInput(new Date().toISOString())}
                        value={callbackCustom} onChange={(e) => setCallbackCustom(e.target.value)} />
                      <button className="btn-primary text-xs" disabled={!callbackCustom}
                        onClick={() => record('Callback', new Date(callbackCustom))}>Guardar</button>
                    </div>
                  </div>
                )}
                <div className="flex items-center justify-between gap-2 text-xs text-slate-500">
                  <label className="inline-flex items-center gap-1.5">
                    <input type="checkbox" checked={autoNext} onChange={(e) => setAutoNext(e.target.checked)} />
                    Al elegir el resultado, llamar al siguiente
                  </label>
                  <button className="underline" onClick={skip}>saltar sin anotar</button>
                </div>
              </div>
            )}

            {queue[idx + 1] && (
              <div className="text-[11px] text-slate-400 border-t border-slate-100 pt-2">
                Sigue: {queue[idx + 1].name} · {fmtPhone(queue[idx + 1].phone)}
              </div>
            )}
          </div>
        )}

        {/* ══ Fin de la ronda ══ */}
        {phase === 'done' && (
          <div className="p-4 space-y-3">
            <div className="text-lg font-semibold">Ronda terminada</div>
            <div className="text-sm text-slate-600">{totalCalls} llamadas anotadas.</div>
            <div className="grid grid-cols-2 gap-1.5 text-sm">
              {OUTCOMES.filter((o) => stats[o.key]).map((o) => (
                <div key={o.key} className={clsx('rounded px-2 py-1', o.cls)}>
                  {o.label}: <span className="font-semibold tabular-nums">{stats[o.key]}</span>
                </div>
              ))}
            </div>
            <div className="flex gap-2 pt-1">
              <button className="btn-primary" onClick={() => setPhase('setup')}>Otra ronda</button>
              <button className="btn-secondary" onClick={close}>Cerrar</button>
            </div>
          </div>
        )}
      </div>
    </div>
  );
}
