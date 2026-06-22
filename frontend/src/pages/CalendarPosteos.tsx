import { useMemo, useState } from 'react';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { api } from '../lib/api';
import toast from 'react-hot-toast';

// ── Tipos (espejo del backend) ───────────────────────────────────────────────
interface SocialPost {
  id: string; productKey: string; platform: string; format: string; assetKind: string;
  status: string; contentPillar: string; concept: string; caption: string;
  hashtags: string[]; assetUrl?: string; thumbnailUrl?: string; bufferChannelId: string;
  scheduledAt?: string | null; postedAt?: string | null; bufferPostId?: string | null;
  error?: string; createdAt: string;
}
interface CalendarResp { scheduled: SocialPost[]; backlog: SocialPost[]; }
interface Profile { productKey: string; brandColorsJson: string }

const DOW = ['Dom', 'Lun', 'Mar', 'Mié', 'Jue', 'Vie', 'Sáb'];
const MONTHS = ['Enero', 'Febrero', 'Marzo', 'Abril', 'Mayo', 'Junio', 'Julio', 'Agosto', 'Septiembre', 'Octubre', 'Noviembre', 'Diciembre'];
const FALLBACK = ['#6366f1', '#ec4899', '#10b981', '#f59e0b', '#06b6d4', '#8b5cf6', '#ef4444', '#14b8a6'];

// ── Helpers de fecha (sin libs) ──────────────────────────────────────────────
const startOfDay = (d: Date) => new Date(d.getFullYear(), d.getMonth(), d.getDate());
const addDays = (d: Date, n: number) => new Date(d.getFullYear(), d.getMonth(), d.getDate() + n);
const startOfWeek = (d: Date) => addDays(startOfDay(d), -d.getDay());
const startOfMonth = (d: Date) => new Date(d.getFullYear(), d.getMonth(), 1);
const sameDay = (a: Date, b: Date) => a.getFullYear() === b.getFullYear() && a.getMonth() === b.getMonth() && a.getDate() === b.getDate();
const pad = (n: number) => String(n).padStart(2, '0');

function toLocalInput(iso?: string | null) {
  if (!iso) return '';
  const d = new Date(iso);
  return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}T${pad(d.getHours())}:${pad(d.getMinutes())}`;
}
function fromLocalInput(s: string): string | null {
  if (!s) return null;
  return new Date(s).toISOString();
}
function hhmm(iso?: string | null) {
  if (!iso) return '';
  const d = new Date(iso);
  return `${pad(d.getHours())}:${pad(d.getMinutes())}`;
}

export default function CalendarPosteos() {
  const qc = useQueryClient();
  const [cursor, setCursor] = useState(() => new Date());
  const [view, setView] = useState<'month' | 'week'>('month');
  const [appFilter, setAppFilter] = useState<string>('');
  const [editing, setEditing] = useState<SocialPost | null>(null);
  const [dragId, setDragId] = useState<string | null>(null);

  const today = startOfDay(new Date());

  // Rango visible
  const { gridStart, gridDays } = useMemo(() => {
    if (view === 'week') {
      const s = startOfWeek(cursor);
      return { gridStart: s, gridDays: 7 };
    }
    const s = startOfWeek(startOfMonth(cursor));
    return { gridStart: s, gridDays: 42 };
  }, [cursor, view]);
  const gridEnd = addDays(gridStart, gridDays);

  const profilesQ = useQuery({
    queryKey: ['posteos-profiles'],
    queryFn: async () => (await api.get<Profile[]>('/posteos/profiles')).data,
  });
  const apps = profilesQ.data ?? [];
  const colorByApp = useMemo(() => {
    const m: Record<string, string> = {};
    apps.forEach((p, i) => {
      let c = FALLBACK[i % FALLBACK.length];
      try { const j = JSON.parse(p.brandColorsJson); if (j.primary) c = j.primary; } catch { /* noop */ }
      m[p.productKey] = c;
    });
    return m;
  }, [apps]);
  const colorOf = (pk: string) => colorByApp[pk] ?? FALLBACK[Math.abs(hash(pk)) % FALLBACK.length];

  const calQ = useQuery({
    queryKey: ['posteos-calendar', gridStart.toISOString(), gridDays, appFilter],
    queryFn: async () => {
      const params = new URLSearchParams({ from: gridStart.toISOString(), to: gridEnd.toISOString() });
      if (appFilter) params.set('productKey', appFilter);
      return (await api.get<CalendarResp>(`/posteos/calendar?${params}`)).data;
    },
  });
  const scheduled = calQ.data?.scheduled ?? [];
  const backlog = calQ.data?.backlog ?? [];

  const byDay = useMemo(() => {
    const m = new Map<string, SocialPost[]>();
    for (const p of scheduled) {
      if (!p.scheduledAt) continue;
      const k = startOfDay(new Date(p.scheduledAt)).toISOString();
      (m.get(k) ?? m.set(k, []).get(k)!).push(p);
    }
    for (const arr of m.values()) arr.sort((a, b) => (a.scheduledAt! < b.scheduledAt! ? -1 : 1));
    return m;
  }, [scheduled]);

  function invalidate() {
    qc.invalidateQueries({ queryKey: ['posteos-calendar'] });
  }

  async function reschedule(post: SocialPost, day: Date) {
    const prev = post.scheduledAt ? new Date(post.scheduledAt) : null;
    const d = new Date(day.getFullYear(), day.getMonth(), day.getDate(), prev?.getHours() ?? 12, prev?.getMinutes() ?? 0);
    try {
      await api.put(`/posteos/${post.id}`, { setScheduledAt: true, scheduledAt: d.toISOString() });
      invalidate();
    } catch (e: any) { toast.error(e.response?.data?.error ?? 'No se pudo reprogramar'); }
  }

  function shift(dir: number) {
    const d = new Date(cursor);
    if (view === 'week') d.setDate(d.getDate() + dir * 7);
    else d.setMonth(d.getMonth() + dir);
    setCursor(d);
  }

  const title = view === 'week'
    ? `${gridStart.getDate()} ${MONTHS[gridStart.getMonth()].slice(0, 3)} – ${addDays(gridStart, 6).getDate()} ${MONTHS[addDays(gridStart, 6).getMonth()].slice(0, 3)} ${addDays(gridStart, 6).getFullYear()}`
    : `${MONTHS[cursor.getMonth()]} ${cursor.getFullYear()}`;

  const days: Date[] = Array.from({ length: gridDays }, (_, i) => addDays(gridStart, i));

  return (
    <div className="space-y-4">
      {/* Toolbar */}
      <div className="flex items-center justify-between gap-2 flex-wrap">
        <div>
          <h2 className="text-xl font-bold">Calendario de posteos</h2>
          <p className="text-xs text-slate-500">Todos los posteos de todas las apps. Arrastrá para reprogramar, click para editar.</p>
        </div>
        <div className="flex items-center gap-2 flex-wrap">
          <select className="input" value={appFilter} onChange={(e) => setAppFilter(e.target.value)}>
            <option value="">Todas las apps</option>
            {apps.map((a) => <option key={a.productKey} value={a.productKey}>{a.productKey}</option>)}
          </select>
          <div className="flex rounded-lg border overflow-hidden">
            <button className={`px-3 py-1.5 text-sm ${view === 'month' ? 'bg-slate-800 text-white' : 'bg-white'}`} onClick={() => setView('month')}>Mes</button>
            <button className={`px-3 py-1.5 text-sm ${view === 'week' ? 'bg-slate-800 text-white' : 'bg-white'}`} onClick={() => setView('week')}>Semana</button>
          </div>
          <div className="flex items-center gap-1">
            <button className="btn-secondary px-2" onClick={() => shift(-1)} aria-label="Anterior">‹</button>
            <button className="btn-secondary text-sm" onClick={() => setCursor(new Date())}>Hoy</button>
            <button className="btn-secondary px-2" onClick={() => shift(1)} aria-label="Siguiente">›</button>
          </div>
        </div>
      </div>

      <div className="flex items-center justify-between">
        <h3 className="font-semibold capitalize">{title}</h3>
        {calQ.isFetching && <span className="text-xs text-slate-400">actualizando…</span>}
      </div>

      {/* Grilla */}
      <div className="card overflow-hidden">
        <div className="grid grid-cols-7 bg-slate-50 border-b text-[11px] font-medium text-slate-500">
          {DOW.map((d) => <div key={d} className="px-2 py-1.5 text-center">{d}</div>)}
        </div>
        <div className={`grid grid-cols-7 ${view === 'week' ? '' : 'grid-rows-6'}`}>
          {days.map((day) => {
            const key = startOfDay(day).toISOString();
            const items = byDay.get(key) ?? [];
            const outside = view === 'month' && day.getMonth() !== cursor.getMonth();
            const isToday = sameDay(day, today);
            return (
              <div
                key={key}
                onDragOver={(e) => { if (dragId) e.preventDefault(); }}
                onDrop={() => {
                  if (!dragId) return;
                  const p = [...scheduled, ...backlog].find((x) => x.id === dragId);
                  if (p) reschedule(p, day);
                  setDragId(null);
                }}
                className={`border-b border-r min-h-[96px] ${view === 'week' ? 'min-h-[420px]' : ''} p-1 ${outside ? 'bg-slate-50/60' : 'bg-white'} ${dragId ? 'hover:bg-brand-50' : ''}`}>
                <div className="flex items-center justify-between px-1">
                  <span className={`text-[11px] ${isToday ? 'bg-brand-600 text-white rounded-full w-5 h-5 flex items-center justify-center' : outside ? 'text-slate-300' : 'text-slate-500'}`}>
                    {day.getDate()}
                  </span>
                </div>
                <div className={`mt-1 space-y-1 ${view === 'month' ? 'max-h-[140px] overflow-y-auto' : ''}`}>
                  {items.map((p) => (
                    <Chip key={p.id} post={p} color={colorOf(p.productKey)} onClick={() => setEditing(p)}
                      onDragStart={() => setDragId(p.id)} onDragEnd={() => setDragId(null)} />
                  ))}
                </div>
              </div>
            );
          })}
        </div>
      </div>

      {/* Backlog */}
      <div className="card p-3">
        <div className="flex items-center justify-between mb-2">
          <h3 className="font-semibold text-sm">Sin agendar ({backlog.length})</h3>
          <span className="text-[11px] text-slate-400">arrastrá a un día para programar</span>
        </div>
        {backlog.length === 0
          ? <div className="text-xs text-slate-400">No hay posteos pendientes de agendar.</div>
          : (
            <div className="flex gap-2 flex-wrap">
              {backlog.map((p) => (
                <div key={p.id} draggable onDragStart={() => setDragId(p.id)} onDragEnd={() => setDragId(null)}
                  onClick={() => setEditing(p)}
                  className="cursor-grab active:cursor-grabbing border rounded-lg p-2 w-48 hover:shadow-sm"
                  style={{ borderLeft: `3px solid ${colorOf(p.productKey)}` }}>
                  <div className="text-[10px] uppercase tracking-wide text-slate-400">{p.productKey} · {p.platform}</div>
                  <div className="text-xs font-medium line-clamp-2">{p.concept || p.caption}</div>
                  <StatusChip status={p.status} />
                </div>
              ))}
            </div>
          )}
      </div>

      {editing && (
        <EditModal
          post={editing}
          color={colorOf(editing.productKey)}
          onClose={() => setEditing(null)}
          onChanged={() => { invalidate(); }}
          onCloseAfter={() => { setEditing(null); invalidate(); }}
        />
      )}
    </div>
  );
}

function Chip({ post, color, onClick, onDragStart, onDragEnd }: {
  post: SocialPost; color: string; onClick: () => void; onDragStart: () => void; onDragEnd: () => void;
}) {
  return (
    <button
      type="button" draggable onDragStart={onDragStart} onDragEnd={onDragEnd} onClick={onClick}
      className="w-full text-left rounded px-1.5 py-1 text-[11px] leading-tight hover:opacity-90 cursor-grab active:cursor-grabbing flex items-start gap-1"
      style={{ background: `${color}1a`, borderLeft: `3px solid ${color}` }}
      title={`${post.concept}\n${post.caption}`}>
      <span className="font-semibold text-slate-500 tabular-nums">{hhmm(post.scheduledAt)}</span>
      <span className="truncate flex-1">{post.concept || post.caption || post.platform}</span>
    </button>
  );
}

function EditModal({ post, color, onClose, onChanged, onCloseAfter }: {
  post: SocialPost; color: string; onClose: () => void; onChanged: () => void; onCloseAfter: () => void;
}) {
  const [concept, setConcept] = useState(post.concept);
  const [caption, setCaption] = useState(post.caption);
  const [hashtags, setHashtags] = useState((post.hashtags ?? []).join(' '));
  const [format, setFormat] = useState(post.format);
  const [when, setWhen] = useState(toLocalInput(post.scheduledAt));
  const [busy, setBusy] = useState(false);
  const hasAsset = !!post.assetUrl;

  function parseTags(s: string) {
    return s.split(/[\s,]+/).map((t) => t.trim()).filter(Boolean);
  }

  async function save(extra?: { sendToBacklog?: boolean }) {
    setBusy(true);
    try {
      await api.put(`/posteos/${post.id}`, {
        concept, caption, hashtags: parseTags(hashtags), format,
        setScheduledAt: true,
        scheduledAt: extra?.sendToBacklog ? null : fromLocalInput(when),
      });
      toast.success(extra?.sendToBacklog ? 'Mandado a “sin agendar”' : 'Guardado');
      onCloseAfter();
    } catch (e: any) { toast.error(e.response?.data?.error ?? 'Falló'); }
    finally { setBusy(false); }
  }

  async function genAsset() {
    setBusy(true);
    const t = toast.loading('Generando imagen con IA…');
    try {
      await api.post(`/posteos/${post.id}/generate-asset`);
      toast.success('Imagen generada', { id: t });
      onChanged();
      onClose();
    } catch (e: any) { toast.error(e.response?.data?.error ?? 'Falló', { id: t }); }
    finally { setBusy(false); }
  }

  async function pushBuffer(asDraft: boolean) {
    if (!hasAsset) { toast.error('Generá la imagen primero'); return; }
    setBusy(true);
    const t = toast.loading(asDraft ? 'Subiendo draft a Buffer…' : 'Programando en Buffer…');
    try {
      await api.post(`/posteos/${post.id}/push`, {
        assetUrl: post.assetUrl,
        scheduledAt: fromLocalInput(when),
        asDraft,
      });
      toast.success(asDraft ? 'Subido como draft' : 'Programado (Buffer publica solo)', { id: t });
      onCloseAfter();
    } catch (e: any) { toast.error(e.response?.data?.error ?? 'Falló', { id: t }); }
    finally { setBusy(false); }
  }

  async function del() {
    if (!window.confirm('¿Borrar este posteo? (también se intenta borrar en Buffer)')) return;
    setBusy(true);
    try { await api.delete(`/posteos/${post.id}`); toast.success('Borrado'); onCloseAfter(); }
    catch (e: any) { toast.error(e.response?.data?.error ?? 'Falló'); }
    finally { setBusy(false); }
  }

  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/60 p-4" onClick={onClose}>
      <div className="bg-white rounded-2xl max-w-2xl w-full max-h-[90vh] overflow-y-auto p-5 shadow-xl" onClick={(e) => e.stopPropagation()}>
        <div className="flex items-center justify-between mb-3">
          <div className="flex items-center gap-2">
            <span className="w-3 h-3 rounded-full" style={{ background: color }} />
            <h3 className="font-semibold capitalize">{post.productKey} · {post.platform} · {post.format}</h3>
            <StatusChip status={post.status} />
          </div>
          <button className="text-slate-400 hover:text-slate-600 text-2xl leading-none" onClick={onClose}>×</button>
        </div>

        <div className="grid grid-cols-1 sm:grid-cols-2 gap-4">
          <div>
            {hasAsset
              ? <img src={post.assetUrl} alt="asset" className="w-full rounded-lg border" />
              : (
                <div className="aspect-square rounded-lg border border-dashed flex flex-col items-center justify-center text-slate-400 text-sm gap-2">
                  <span>Sin imagen</span>
                  {post.assetKind === 'Image' && (
                    <button className="btn-secondary text-xs" disabled={busy} onClick={genAsset}>Generar imagen con IA</button>
                  )}
                </div>
              )}
            {hasAsset && post.assetKind === 'Image' && (
              <button className="btn-secondary text-xs mt-2 w-full" disabled={busy} onClick={genAsset}>Regenerar imagen</button>
            )}
          </div>

          <div className="space-y-2">
            <div>
              <label className="text-xs text-slate-500">Concepto</label>
              <input className="input w-full" value={concept} onChange={(e) => setConcept(e.target.value)} />
            </div>
            <div>
              <label className="text-xs text-slate-500">Fecha y hora</label>
              <input type="datetime-local" className="input w-full" value={when} onChange={(e) => setWhen(e.target.value)} />
            </div>
            <div>
              <label className="text-xs text-slate-500">Formato</label>
              <select className="input w-full" value={format} onChange={(e) => setFormat(e.target.value)}>
                {['Post', 'Story', 'Reel', 'Carousel', 'Video'].map((f) => <option key={f} value={f}>{f}</option>)}
              </select>
            </div>
          </div>
        </div>

        <div className="mt-3">
          <label className="text-xs text-slate-500">Caption</label>
          <textarea className="input w-full" rows={4} value={caption} onChange={(e) => setCaption(e.target.value)} />
        </div>
        <div className="mt-2">
          <label className="text-xs text-slate-500">Hashtags (separados por espacio)</label>
          <textarea className="input w-full text-xs" rows={2} value={hashtags} onChange={(e) => setHashtags(e.target.value)} />
        </div>

        {post.bufferPostId && <div className="text-[11px] text-emerald-600 mt-2">Ya está en Buffer (id …{post.bufferPostId.slice(-6)}). Re-subir crea una copia nueva.</div>}
        {post.error && <div className="text-[11px] text-red-600 mt-2">{post.error}</div>}

        <div className="flex flex-wrap gap-2 mt-4 pt-3 border-t">
          <button className="btn-primary text-sm" disabled={busy} onClick={() => save()}>Guardar cambios</button>
          <button className="btn-secondary text-sm disabled:opacity-40" disabled={busy || !hasAsset}
            title={!hasAsset ? 'Generá la imagen primero' : 'Buffer auto-publica en la fecha'}
            onClick={() => pushBuffer(false)}>Programar en Buffer</button>
          <button className="btn-secondary text-sm disabled:opacity-40" disabled={busy || !hasAsset}
            onClick={() => pushBuffer(true)}>Subir como draft</button>
          <button className="btn-secondary text-sm" disabled={busy} onClick={() => save({ sendToBacklog: true })}>Sin agendar</button>
          <button className="text-sm text-red-600 ml-auto" disabled={busy} onClick={del}>Borrar</button>
        </div>
      </div>
    </div>
  );
}

function StatusChip({ status }: { status: string }) {
  const map: Record<string, string> = {
    Idea: 'bg-slate-100 text-slate-600', DraftReady: 'bg-sky-100 text-sky-700',
    GeneratingAsset: 'bg-amber-100 text-amber-700', PushedToBuffer: 'bg-emerald-100 text-emerald-700',
    Scheduled: 'bg-emerald-100 text-emerald-700', Posted: 'bg-emerald-200 text-emerald-800',
    Rejected: 'bg-slate-200 text-slate-500', Error: 'bg-red-100 text-red-700',
  };
  return <span className={`text-[10px] rounded px-1.5 py-0.5 ${map[status] ?? 'bg-slate-100'}`}>{status}</span>;
}

function hash(s: string) {
  let h = 0;
  for (let i = 0; i < s.length; i++) h = (h << 5) - h + s.charCodeAt(i) | 0;
  return h;
}
