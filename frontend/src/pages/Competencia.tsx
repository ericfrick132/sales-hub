// Tab "Competencia" del CRM: negocios de los listados de la competencia (Públicos) que ya tienen teléfono,
// para llamarlos en frío. La ven todos y no se reparte: cada uno toma el siguiente de la lista.
// Nada sale solo: recién con interés o demo agendada se crea el lead del Pipeline (origen Competencia).
import { useState } from 'react';
import { Link } from 'react-router-dom';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import toast from 'react-hot-toast';
import { api } from '../lib/api';

interface CallRow {
  id: string; name: string; productKey: string; competitor: string; segment: string; category: string | null;
  city: string | null; province: string | null; country: string | null;
  phone: string | null; phoneType: string | null; phoneSource: string | null;
  secondaryPhone: string | null; secondaryPhoneSource: string | null;
  instagram: string | null; website: string | null;
  callStatus: string | null; callReason: string | null; callNotes: string | null; callCount: number;
  lastCallAt: string | null; lastCallByName: string | null; nextCallAt: string | null; leadId: string | null;
}
interface Facet { key: string; count: number }
interface CallList { total: number; rows: CallRow[]; products: Facet[]; competitors: Facet[]; provinces: Facet[]; statuses: Facet[] }

const RESULTS: { key: string; label: string; tone: string }[] = [
  { key: 'no_atiende', label: 'No atiende', tone: 'bg-slate-100 text-slate-700' },
  { key: 'volver', label: 'Volver a llamar', tone: 'bg-amber-100 text-amber-800' },
  { key: 'demo', label: 'Demo agendada', tone: 'bg-emerald-100 text-emerald-800' },
  { key: 'interesado', label: 'Interesado', tone: 'bg-sky-100 text-sky-800' },
  { key: 'no_interesa', label: 'No le interesa', tone: 'bg-rose-100 text-rose-800' },
  { key: 'equivocado', label: 'Número equivocado', tone: 'bg-slate-100 text-slate-500' },
];
const STATUS_CHIPS = [
  { key: 'hoy', label: 'Para llamar hoy' },
  { key: 'pendiente', label: 'Sin llamar' },
  ...RESULTS.map((r) => ({ key: r.key, label: r.label })),
  { key: '', label: 'Todos' },
];
const APP_NAMES: Record<string, string> = { playcrew: 'PlayCrew', gymhero: 'GymHero', turnospro: 'TurnosPro' };
const SOURCE_NAMES: Record<string, string> = {
  competidor: 'del listado', instagram_link: 'Instagram', instagram_bio: 'Instagram', web_whatsapp: 'web',
  web_tel: 'web', web_texto: 'web', google_places: 'Google Maps', google_panel: 'Google', google_wa: 'Google', google_tel: 'Google', lead: 'CRM',
};
const srcName = (s: string | null) => (s ? SOURCE_NAMES[s] ?? (s.startsWith('cruce:') ? 'otro listado' : s) : '');
const fmt = (iso: string | null) =>
  iso ? new Date(iso).toLocaleString('es-AR', { day: '2-digit', month: '2-digit', hour: '2-digit', minute: '2-digit' }) : '';

export default function Competencia() {
  const [productKey, setProductKey] = useState('');
  const [competitor, setCompetitor] = useState('');
  const [segment, setSegment] = useState('');
  const [province, setProvince] = useState('');
  const [status, setStatus] = useState('pendiente');
  const [q, setQ] = useState('');
  const [calling, setCalling] = useState<CallRow | null>(null);

  const list = useQuery({
    queryKey: ['competencia', productKey, competitor, segment, province, status, q],
    queryFn: async () => (await api.get<CallList>('/competencia', {
      params: { productKey: productKey || undefined, competitor: competitor || undefined, segment: segment || undefined,
        province: province || undefined, status: status || undefined, q: q || undefined, limit: 100 },
    })).data,
    refetchInterval: 60000,
  });
  const d = list.data;
  const countOf = (k: string) => (k === '' ? undefined : d?.statuses.find((s) => s.key === k)?.count ?? 0);

  return (
    <div className="space-y-4">
      <div>
        <h1 className="text-xl font-bold mb-1">Competencia</h1>
        <p className="text-sm text-slate-500">
          Negocios que usan o dejaron un sistema de la competencia, con teléfono. Se llaman en frío (guion en
          “Cold calling EF Cloud”): no se les escribe. Con interés o demo agendada pasan al Pipeline a tu nombre.
        </p>
      </div>

      <div className="card p-3 grid grid-cols-2 md:grid-cols-5 gap-2">
        <select className="input" value={productKey} onChange={(e) => { setProductKey(e.target.value); setCompetitor(''); setProvince(''); }}>
          <option value="">Todas las apps</option>
          {d?.products.map((f) => <option key={f.key} value={f.key}>{APP_NAMES[f.key] ?? f.key} ({f.count})</option>)}
        </select>
        <select className="input" value={competitor} onChange={(e) => setCompetitor(e.target.value)}>
          <option value="">Todos los competidores</option>
          {d?.competitors.map((f) => <option key={f.key} value={f.key}>{f.key} ({f.count})</option>)}
        </select>
        <select className="input" value={segment} onChange={(e) => setSegment(e.target.value)}>
          <option value="">Activos y bajas</option>
          <option value="baja">Se dieron de baja</option>
          <option value="usa">Lo usan hoy</option>
        </select>
        <select className="input" value={province} onChange={(e) => setProvince(e.target.value)}>
          <option value="">Todas las provincias</option>
          {d?.provinces.map((f) => <option key={f.key} value={f.key}>{f.key} ({f.count})</option>)}
        </select>
        <input className="input col-span-2 md:col-span-1" placeholder="Buscar nombre, ciudad o teléfono" value={q} onChange={(e) => setQ(e.target.value)} />
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
      {d && d.rows.length === 0 && <div className="card p-6 text-sm text-slate-500">No hay negocios con estos filtros.</div>}
      {d && d.total > d.rows.length && (
        <div className="text-xs text-slate-500">Mostrando {d.rows.length} de {d.total.toLocaleString('es-AR')}, en orden de prioridad.</div>
      )}

      <div className="space-y-2">
        {d?.rows.map((r) => <Row key={r.id} r={r} onCall={() => setCalling(r)} />)}
      </div>

      {calling && <CallDialog row={calling} onClose={() => setCalling(null)} />}
    </div>
  );
}

function Row({ r, onCall }: { r: CallRow; onCall: () => void }) {
  const res = RESULTS.find((x) => x.key === r.callStatus);
  return (
    <div className="card p-3 flex flex-col md:flex-row md:items-center gap-3">
      <div className="flex-1 min-w-0">
        <div className="flex flex-wrap items-center gap-2">
          <span className="font-medium">{r.name}</span>
          <span className="text-[11px] bg-slate-100 border border-slate-200 rounded px-1.5 py-0.5">{APP_NAMES[r.productKey] ?? r.productKey}</span>
          <span className={`text-[11px] rounded px-1.5 py-0.5 ${r.segment === 'baja' ? 'bg-amber-50 text-amber-800 border border-amber-200' : 'bg-slate-50 text-slate-600 border border-slate-200'}`}>
            {r.segment === 'baja' ? `Dejó ${r.competitor}` : `Usa ${r.competitor}`}
          </span>
          {res && <span className={`text-[11px] rounded px-1.5 py-0.5 ${res.tone}`}>{res.label}</span>}
        </div>
        <div className="text-xs text-slate-500 mt-0.5">
          {[r.category, [r.city, r.province].filter(Boolean).join(', ')].filter(Boolean).join(' · ')}
        </div>
        <div className="flex flex-wrap gap-x-4 gap-y-1 mt-1.5 text-sm">
          {r.phone && <Phone n={r.phone} label={srcName(r.phoneSource)} />}
          {r.secondaryPhone && <Phone n={r.secondaryPhone} label={`otro · ${srcName(r.secondaryPhoneSource)}`} />}
          {r.instagram && <a className="text-brand-700 hover:underline" href={`https://instagram.com/${r.instagram.replace(/^@/, '')}`} target="_blank" rel="noreferrer">@{r.instagram.replace(/^@/, '')}</a>}
          {r.website && <a className="text-brand-700 hover:underline truncate max-w-[220px]" href={r.website} target="_blank" rel="noreferrer">{r.website.replace(/^https?:\/\/(www\.)?/, '')}</a>}
        </div>
        {(r.lastCallAt || r.callReason || r.callNotes) && (
          <div className="text-xs text-slate-600 mt-1.5">
            {r.lastCallAt && <>Última llamada {fmt(r.lastCallAt)}{r.lastCallByName && <> por {r.lastCallByName}</>} ({r.callCount})</>}
            {r.nextCallAt && <> · {r.callStatus === 'demo' ? 'Demo' : 'Volver'}: <b>{fmt(r.nextCallAt)}</b></>}
            {r.callReason && <> · Motivo: {r.callReason}</>}
            {r.callNotes && <div className="text-slate-500 mt-0.5 whitespace-pre-line">{r.callNotes}</div>}
          </div>
        )}
      </div>
      <div className="flex md:flex-col gap-2 shrink-0">
        <button type="button" className="btn-primary text-sm" onClick={onCall}>Registrar llamada</button>
        {r.leadId && <Link className="btn-secondary text-sm text-center" to={`/leads/${r.leadId}`}>Ver lead</Link>}
      </div>
    </div>
  );
}

function Phone({ n, label }: { n: string; label: string }) {
  return (
    <a href={`tel:${n}`} className="inline-flex items-baseline gap-1 font-medium tabular-nums text-slate-800 hover:text-brand-700">
      {n}{label && <span className="text-[11px] font-normal text-slate-400">{label}</span>}
    </a>
  );
}

function CallDialog({ row, onClose }: { row: CallRow; onClose: () => void }) {
  const qc = useQueryClient();
  const [result, setResult] = useState('');
  const [reason, setReason] = useState(row.callReason ?? '');
  const [notes, setNotes] = useState('');
  const [nextAt, setNextAt] = useState('');
  const [saving, setSaving] = useState(false);
  const needsDate = result === 'volver' || result === 'demo';

  const save = async () => {
    if (!result) return toast.error('Elegí el resultado de la llamada');
    if (needsDate && !nextAt) return toast.error(result === 'demo' ? 'Poné el día y la hora de la demo' : 'Poné cuándo volver a llamar');
    setSaving(true);
    try {
      const notesAll = [row.callNotes, notes.trim() && `${new Date().toLocaleDateString('es-AR')}: ${notes.trim()}`].filter(Boolean).join('\n');
      const { data } = await api.post<{ leadId: string | null }>(`/competencia/${row.id}/call`, {
        result, reason: reason.trim() || null, notes: notesAll || null, nextAt: needsDate ? new Date(nextAt).toISOString() : null,
      });
      toast.success(data.leadId && !row.leadId ? 'Llamada registrada · pasó al Pipeline a tu nombre' : 'Llamada registrada');
      qc.invalidateQueries({ queryKey: ['competencia'] });
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
          <p className="text-sm text-slate-500">{row.segment === 'baja' ? `Dejó ${row.competitor}` : `Usa ${row.competitor}`} · ¿cómo salió la llamada?</p>
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
          Motivo de baja / por qué no le interesa
          <input className="input w-full mt-1" placeholder="Precio, soporte, le faltaba algo, volvió al cuaderno…" value={reason} onChange={(e) => setReason(e.target.value)} />
        </label>
        <label className="block text-xs text-slate-600">
          Notas (cómo gestiona hoy, quién decide, próximo paso)
          <textarea className="input w-full mt-1" rows={3} value={notes} onChange={(e) => setNotes(e.target.value)} />
        </label>
        {(result === 'demo' || result === 'interesado') && !row.leadId && (
          <p className="text-xs text-slate-500">Pasa al Pipeline a tu nombre, sin mensajes automáticos.</p>
        )}
        <div className="flex justify-end gap-2">
          <button type="button" className="btn-secondary" onClick={onClose}>Cancelar</button>
          <button type="button" className="btn-primary" disabled={saving} onClick={save}>{saving ? 'Guardando…' : 'Guardar'}</button>
        </div>
      </div>
    </div>
  );
}
