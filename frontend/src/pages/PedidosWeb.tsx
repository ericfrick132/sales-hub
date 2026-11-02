// Tab "Pedidos web" del CRM: los que pidieron demo o que los llamen desde el formulario de las landings
// (sección de precios o popup "¿Antes de irte?"). La web promete llamar en 10 minutos: el más nuevo arriba,
// y en rojo los que pasaron ese plazo sin llamar. La llamada se registra con el modo llamadas de siempre
// (POST /crm/leads/:id/calls), así queda en la bitácora del lead y cuenta para el objetivo diario.
import { useEffect, useState } from 'react';
import { Link } from 'react-router-dom';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import toast from 'react-hot-toast';
import { api } from '../lib/api';

interface WebRow {
  id: string; name: string; productKey: string; productName: string | null; phone: string | null;
  email: string | null; business: string | null; plan: string | null; page: string | null; fromExitPopup: boolean;
  requestedAt: string; callState: string; callCount: number; lastCallAt: string | null; lastCallText: string | null;
  lastCallByName: string | null; nextActionAt: string | null; sellerName: string | null; status: string; repeat: boolean;
}
interface Facet { key: string; count: number }
interface WebList { total: number; rows: WebRow[]; products: Facet[]; statuses: Facet[] }

const STATES: { key: string; label: string; tone: string }[] = [
  { key: 'no_atiende', label: 'No atiende', tone: 'bg-slate-100 text-slate-700' },
  { key: 'llamado', label: 'Llamado', tone: 'bg-sky-100 text-sky-800' },
  { key: 'volver', label: 'Volver a llamar', tone: 'bg-amber-100 text-amber-800' },
  { key: 'interesado', label: 'Interesado', tone: 'bg-sky-100 text-sky-800' },
  { key: 'demo', label: 'Demo agendada', tone: 'bg-emerald-100 text-emerald-800' },
  { key: 'no_interesa', label: 'No le interesa', tone: 'bg-rose-100 text-rose-800' },
  { key: 'equivocado', label: 'Número equivocado', tone: 'bg-slate-100 text-slate-500' },
];
const STATUS_CHIPS = [
  { key: 'pendiente', label: 'Sin llamar' },
  { key: 'hoy', label: 'Para llamar hoy' },
  ...STATES.map((s) => ({ key: s.key, label: s.label })),
  { key: '', label: 'Todos' },
];
/** Resultados del diálogo → outcome del modo llamadas (CrmController.CallOutcome). "demo" además mueve la etapa. */
const RESULTS: { key: string; label: string; outcome: string }[] = [
  { key: 'no_atiende', label: 'No atiende', outcome: 'NoAnswer' },
  { key: 'llamado', label: 'Atendió, hablamos', outcome: 'Answered' },
  { key: 'volver', label: 'Volver a llamar', outcome: 'Callback' },
  { key: 'demo', label: 'Demo agendada', outcome: 'Answered' },
  { key: 'interesado', label: 'Interesado', outcome: 'Interested' },
  { key: 'no_interesa', label: 'No le interesa', outcome: 'NotInterested' },
  { key: 'equivocado', label: 'Número equivocado', outcome: 'WrongNumber' },
];
const APP_NAMES: Record<string, string> = {
  gymhero: 'GymHero', turnospro: 'TurnosPro', playcrew: 'PlayCrew', construction: 'ArchiCloud', unistock: 'DashFlow', bunker: 'Bunker',
};
const appName = (key: string, name?: string | null) => APP_NAMES[key] ?? name ?? key;
const PROMISE_MIN = 10;
const fmt = (iso: string | null) =>
  iso ? new Date(iso).toLocaleString('es-AR', { day: '2-digit', month: '2-digit', hour: '2-digit', minute: '2-digit' }) : '';
function ago(iso: string, now: number) {
  const min = Math.max(0, Math.floor((now - new Date(iso).getTime()) / 60000));
  if (min < 1) return 'recién';
  if (min < 60) return `hace ${min} min`;
  const h = Math.floor(min / 60);
  if (h < 24) return `hace ${h} h${min % 60 ? ` ${min % 60} min` : ''}`;
  const d = Math.floor(h / 24);
  return `hace ${d} día${d === 1 ? '' : 's'}`;
}
const waLink = (phone: string) => `https://wa.me/${phone.replace(/\D/g, '')}`;

export default function PedidosWeb() {
  const [productKey, setProductKey] = useState('');
  const [status, setStatus] = useState('pendiente');
  const [q, setQ] = useState('');
  const [calling, setCalling] = useState<WebRow | null>(null);
  const [now, setNow] = useState(() => Date.now());
  useEffect(() => { const t = window.setInterval(() => setNow(Date.now()), 30000); return () => window.clearInterval(t); }, []);

  const list = useQuery({
    queryKey: ['web-requests', productKey, status, q],
    queryFn: async () => (await api.get<WebList>('/crm/web-requests', {
      params: { productKey: productKey || undefined, status: status || undefined, q: q || undefined, limit: 200 },
    })).data,
    refetchInterval: 30000,
  });
  const d = list.data;
  const countOf = (k: string) => (k === '' ? undefined : d?.statuses.find((s) => s.key === k)?.count ?? 0);

  return (
    <div className="space-y-4">
      <div>
        <h1 className="text-xl font-bold mb-1">Pedidos web</h1>
        <p className="text-sm text-slate-500">
          Pidieron una demo o que los llamen desde la web de alguna app (sección de precios o el popup “¿Antes de irte?”).
          La web promete llamar dentro de los próximos {PROMISE_MIN} minutos: en rojo, los que pasaron ese plazo sin llamar.
        </p>
      </div>

      <div className="card p-3 grid grid-cols-1 sm:grid-cols-3 gap-2">
        <select className="input" value={productKey} onChange={(e) => setProductKey(e.target.value)}>
          <option value="">Todas las apps</option>
          {d?.products.map((f) => <option key={f.key} value={f.key}>{appName(f.key)} ({f.count})</option>)}
        </select>
        <input className="input sm:col-span-2" placeholder="Buscar nombre, mail o teléfono" value={q} onChange={(e) => setQ(e.target.value)} />
      </div>

      <div className="flex flex-wrap gap-1.5">
        {STATUS_CHIPS.map((c) => (
          <button key={c.key} type="button" onClick={() => setStatus(c.key)}
            className={`px-3 py-1 text-xs font-medium rounded-full ${status === c.key ? 'bg-brand-600 text-white' : 'bg-slate-100 text-slate-600 hover:bg-slate-200'}`}>
            {c.label}{countOf(c.key) !== undefined && <span className="tabular-nums opacity-75"> · {countOf(c.key)}</span>}
          </button>
        ))}
      </div>

      {list.isLoading && <div className="text-sm text-slate-400">Cargando…</div>}
      {list.isError && <div className="card p-6 text-sm text-rose-600">No se pudieron cargar los pedidos.</div>}
      {d && d.rows.length === 0 && <div className="card p-6 text-sm text-slate-500">No hay pedidos con estos filtros.</div>}
      {d && d.total > d.rows.length && (
        <div className="text-xs text-slate-500">Mostrando {d.rows.length} de {d.total.toLocaleString('es-AR')}, el más nuevo primero.</div>
      )}

      <div className="space-y-2">
        {d?.rows.map((r) => <Row key={r.id} r={r} now={now} onCall={() => setCalling(r)} />)}
      </div>

      {calling && <CallDialog row={calling} onClose={() => setCalling(null)} />}
    </div>
  );
}

function Row({ r, now, onCall }: { r: WebRow; now: number; onCall: () => void }) {
  const st = STATES.find((x) => x.key === r.callState);
  const late = r.callState === 'pendiente' && now - new Date(r.requestedAt).getTime() > PROMISE_MIN * 60000;
  return (
    <div className={`card p-3 flex flex-col md:flex-row md:items-center gap-3 ${late ? 'border-rose-300 bg-rose-50/40' : ''}`}>
      <div className="flex-1 min-w-0">
        <div className="flex flex-wrap items-center gap-2">
          <span className="font-medium">{r.name}</span>
          <span className="text-[11px] bg-slate-100 border border-slate-200 rounded px-1.5 py-0.5">{appName(r.productKey, r.productName)}</span>
          <span className={`text-[11px] rounded px-1.5 py-0.5 border ${r.fromExitPopup ? 'bg-violet-50 text-violet-800 border-violet-200' : 'bg-slate-50 text-slate-600 border-slate-200'}`}>
            {r.fromExitPopup ? 'Popup de salida' : 'Sección de precios'}
          </span>
          {r.repeat && <span className="text-[11px] rounded px-1.5 py-0.5 bg-amber-50 text-amber-800 border border-amber-200">Ya estaba en el CRM</span>}
          {st ? <span className={`text-[11px] rounded px-1.5 py-0.5 ${st.tone}`}>{st.label}</span>
            : <span className={`text-[11px] rounded px-1.5 py-0.5 ${late ? 'bg-rose-600 text-white' : 'bg-amber-100 text-amber-800'}`}>Sin llamar</span>}
        </div>
        <div className={`text-xs mt-0.5 ${late ? 'text-rose-700 font-semibold' : 'text-slate-500'}`}>
          Pidió {ago(r.requestedAt, now)} · {fmt(r.requestedAt)}
          {late && <> · pasaron los {PROMISE_MIN} minutos</>}
        </div>
        <div className="text-xs text-slate-500 mt-0.5">
          {[r.business, r.plan && `Plan ${r.plan}`, r.page && `Página ${r.page}`, r.sellerName && `Vendedor: ${r.sellerName}`].filter(Boolean).join(' · ')}
        </div>
        <div className="flex flex-wrap gap-x-4 gap-y-1 mt-1.5 text-sm">
          {r.phone && (
            <>
              <a href={`tel:+${r.phone.replace(/\D/g, '')}`} className="font-medium tabular-nums text-slate-800 hover:text-brand-700">+{r.phone.replace(/\D/g, '')}</a>
              <a href={waLink(r.phone)} target="_blank" rel="noreferrer" className="text-brand-700 hover:underline">WhatsApp</a>
            </>
          )}
          {r.email && <a href={`mailto:${r.email}`} className="text-brand-700 hover:underline break-all">{r.email}</a>}
        </div>
        {r.lastCallAt && (
          <div className="text-xs text-slate-600 mt-1.5">
            Última llamada {fmt(r.lastCallAt)}{r.lastCallByName && <> por {r.lastCallByName}</>} ({r.callCount})
            {r.callState === 'volver' && r.nextActionAt && <> · Volver: <b>{fmt(r.nextActionAt)}</b></>}
            {r.lastCallText && <div className="text-slate-500 mt-0.5 whitespace-pre-line">{r.lastCallText.replace(/^Llamada: /, '')}</div>}
          </div>
        )}
      </div>
      <div className="flex md:flex-col gap-2 shrink-0">
        <button type="button" className="btn-primary text-sm" onClick={onCall}>Registrar llamada</button>
        <Link className="btn-secondary text-sm text-center" to={`/leads/${r.id}`}>Ver lead</Link>
      </div>
    </div>
  );
}

function CallDialog({ row, onClose }: { row: WebRow; onClose: () => void }) {
  const qc = useQueryClient();
  const [result, setResult] = useState('');
  const [notes, setNotes] = useState('');
  const [nextAt, setNextAt] = useState('');
  const [saving, setSaving] = useState(false);
  const needsDate = result === 'volver' || result === 'demo';

  const save = async () => {
    const r = RESULTS.find((x) => x.key === result);
    if (!r) return toast.error('Elegí el resultado de la llamada');
    if (needsDate && !nextAt) return toast.error(result === 'demo' ? 'Poné el día y la hora de la demo' : 'Poné cuándo volver a llamar');
    setSaving(true);
    try {
      const when = nextAt ? new Date(nextAt) : null;
      const demoText = result === 'demo' && when
        ? `Demo agendada para el ${when.toLocaleString('es-AR', { day: '2-digit', month: '2-digit', hour: '2-digit', minute: '2-digit' })}`
        : null;
      const note = [demoText, notes.trim()].filter(Boolean).join(' — ') || null;
      await api.post(`/crm/leads/${row.id}/calls`, {
        outcome: r.outcome, note, callbackAt: result === 'volver' && when ? when.toISOString() : null,
      });
      if (result === 'demo') await api.patch(`/crm/leads/${row.id}/stage`, { stage: 'demo', note: null });
      toast.success(result === 'demo' ? 'Llamada registrada · pasó a Demo agendada' : 'Llamada registrada');
      qc.invalidateQueries({ queryKey: ['web-requests'] });
      onClose();
    } catch (e: any) {
      toast.error(e?.response?.data?.error ?? 'No se pudo guardar');
    } finally {
      setSaving(false);
    }
  };

  return (
    <div className="fixed inset-0 z-50 bg-black/40 flex items-center justify-center p-4" onClick={onClose}>
      <div className="bg-white rounded-xl shadow-xl max-w-lg w-full p-6 max-h-[90vh] overflow-y-auto space-y-4" onClick={(e) => e.stopPropagation()}>
        <div>
          <h2 className="text-xl font-bold mb-1">{row.name}</h2>
          <p className="text-sm text-slate-500">
            {appName(row.productKey, row.productName)} · {row.fromExitPopup ? 'popup de salida' : 'sección de precios'} · ¿cómo salió la llamada?
          </p>
        </div>
        <div className="grid grid-cols-2 gap-2">
          {RESULTS.map((r) => (
            <button key={r.key} type="button" onClick={() => setResult(r.key)}
              className={`text-sm rounded-lg border px-3 py-2 text-left ${result === r.key ? 'border-brand-600 ring-2 ring-brand-200 font-semibold' : 'border-slate-200 hover:bg-slate-50'}`}>
              {r.label}
            </button>
          ))}
        </div>
        {needsDate && (
          <label className="block text-xs text-slate-600">
            {result === 'demo' ? 'Día y hora de la demo' : 'Volver a llamar el'}
            <input type="datetime-local" className="input w-full mt-1" value={nextAt} onChange={(e) => setNextAt(e.target.value)} />
          </label>
        )}
        <label className="block text-xs text-slate-600">
          Notas (qué necesita, cómo gestiona hoy, próximo paso)
          <textarea className="input w-full mt-1" rows={3} value={notes} onChange={(e) => setNotes(e.target.value)} />
        </label>
        <p className="text-xs text-slate-500">Queda en la bitácora del lead, como el modo llamadas.</p>
        <div className="flex justify-end gap-2">
          <button type="button" className="btn-secondary" onClick={onClose}>Cancelar</button>
          <button type="button" className="btn-primary" disabled={saving} onClick={save}>{saving ? 'Guardando…' : 'Guardar'}</button>
        </div>
      </div>
    </div>
  );
}
