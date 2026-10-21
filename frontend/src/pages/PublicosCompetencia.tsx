// Públicos de competencia: listados de clientes de la competencia (Wellhub, ATC, Fresha, AgendaPro…)
// para públicos de Meta, guardados en audience_leads. Nunca se contactan desde sales-hub.
// Muestra cuántos tienen WhatsApp y de dónde salió cada número, con el avance de la extensión
// "Públicos IG: WhatsApp + Follow" (Instagram → web) y de la búsqueda en Google.
import { useMemo, useRef, useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import toast from 'react-hot-toast';
import { api } from '../lib/api';

interface SummaryRow {
  productKey: string; competitor: string; dataset: string; segment: string; country: string | null;
  total: number; withPhone: number; whatsapp: number;
  fromCompetitor: number; fromInstagram: number; fromWeb: number; fromLeads: number; fromGoogle: number;
  finderDone: number; finderPending: number; googlePending: number;
}
interface RecentRow {
  name: string; dataset: string; city: string | null; country: string | null; phone: string | null;
  whatsappProbable: boolean; phoneSource: string | null; instagram: string | null; updatedAt: string;
}

interface FinderStep { step: string; result: string; detail: string | null; at: string }
interface EventRow {
  name: string; productKey: string; dataset: string; city: string | null; finderStatus: string | null;
  phone: string | null; phoneSource: string | null; checkedAt: string | null; steps: FinderStep[];
}

interface BatchRow {
  id: string; number: number; productKey: string; dataset: string | null; status: string; note: string | null;
  createdAt: string; retriedAt: string | null; closedAt: string | null; size: number; pending: number; found: number;
  foundInstagram: number; foundWeb: number; foundGoogle: number; follows: number; lastActivity: string | null;
  reasons: Record<string, number>;
}

interface DayRow {
  day: string; productKey: string; processed: number; found: number; foundInstagram: number; foundWeb: number;
  foundGoogle: number; follows: number; reasons: Record<string, number>;
}

const STEP_LABEL: Record<string, string> = {
  ig_profile: 'IG perfil', ig_search: 'IG búsqueda', ig_follow: 'IG follow', web: 'Web', leads: 'Base de leads', google: 'Google Maps', extension: 'Extensión'
};
const RESULT_LABEL: Record<string, string> = {
  ok: 'número ✓', no_phone: 'sin número', not_found: 'no existe', no_match: 'no lo encontró', blocked: 'IG bloqueó',
  login: 'IG pidió login', error: 'error', distinto: 'era otro lugar', sin_web: 'sin web',
  followed: 'seguido ✓', already: 'ya lo seguía', no_button: 'sin botón Seguir'
};
const RESULT_CLASS: Record<string, string> = {
  ok: 'text-emerald-600', followed: 'text-emerald-600', blocked: 'text-rose-600', login: 'text-rose-600', error: 'text-amber-600'
};
const STATUS_LABEL: Record<string, string> = { ok: 'con número', google: 'para Google', error: 'error' };
const fmtAt = (s: string) => new Date(s).toLocaleString('es-AR', { day: '2-digit', month: '2-digit', hour: '2-digit', minute: '2-digit' });

const PRODUCT_LABEL: Record<string, string> = { gymhero: 'GymHero', turnospro: 'TurnosPro', playcrew: 'PlayCrew' };
const SOURCE_LABEL: Record<string, string> = {
  instagram_link: 'IG · link', instagram_bio: 'IG · bio', web_whatsapp: 'Web · WhatsApp', web_tel: 'Web · tel',
  web_texto: 'Web · texto', lead: 'Base de leads', google_places: 'Google Maps'
};
const NUM_KEYS = ['total', 'withPhone', 'whatsapp', 'fromCompetitor', 'fromInstagram', 'fromWeb', 'fromLeads',
  'fromGoogle', 'finderDone', 'finderPending', 'googlePending'] as const;

const fmt = (n: number) => n.toLocaleString('es-AR');
const pct = (a: number, b: number) => (b ? `${Math.round((100 * a) / b)}%` : '—');

function sum(rows: SummaryRow[]) {
  const t = Object.fromEntries(NUM_KEYS.map((k) => [k, 0])) as Record<(typeof NUM_KEYS)[number], number>;
  for (const r of rows) for (const k of NUM_KEYS) t[k] += r[k];
  return t;
}

export default function PublicosCompetencia() {
  const qc = useQueryClient();
  const [product, setProduct] = useState<string>('');
  const fileRef = useRef<HTMLInputElement>(null);

  const summary = useQuery({
    queryKey: ['audience-summary'],
    queryFn: async () => (await api.get<SummaryRow[]>('/audience/summary')).data,
    refetchInterval: 30_000,
  });
  const recent = useQuery({
    queryKey: ['audience-recent'],
    queryFn: async () => (await api.get<RecentRow[]>('/audience/recent', { params: { limit: 40 } })).data,
    refetchInterval: 30_000,
  });
  const events = useQuery({
    queryKey: ['audience-events'],
    queryFn: async () => (await api.get<EventRow[]>('/audience/events', { params: { limit: 60 } })).data,
    refetchInterval: 30_000,
  });
  const days = useQuery({
    queryKey: ['audience-days'],
    queryFn: async () => (await api.get<DayRow[]>('/audience/finder-days', { params: { days: 14 } })).data,
    refetchInterval: 60_000,
  });

  const batches = useQuery({
    queryKey: ['audience-batches'],
    queryFn: async () => (await api.get<BatchRow[]>('/audience/batches')).data,
    refetchInterval: 20_000,
  });
  const [bApp, setBApp] = useState('playcrew');
  const [bDataset, setBDataset] = useState('');
  const [bSize, setBSize] = useState(50);
  const refreshBatches = () => { qc.invalidateQueries({ queryKey: ['audience-batches'] }); qc.invalidateQueries({ queryKey: ['audience-summary'] }); };
  const createBatch = useMutation({
    mutationFn: async () => (await api.post<BatchRow>('/audience/batches', { productKey: bApp, dataset: bDataset || null, size: bSize })).data,
    onSuccess: (b) => { toast.success(`Batch #${b.number}: ${b.size} negocios`); refreshBatches(); },
    onError: (e: any) => toast.error(e?.response?.data?.error ?? 'No se pudo armar el batch'),
  });
  const batchAction = useMutation({
    mutationFn: async (v: { id: string; action: 'close' | 'retry' }) => (await api.post(`/audience/batches/${v.id}/${v.action}`)).data,
    onSuccess: (d: any, v) => { toast.success(v.action === 'close' ? 'Batch cerrado' : `${d?.requeued ?? 0} vuelven a la cola`); refreshBatches(); },
  });

  const importFile = useMutation({
    mutationFn: async (file: File) => {
      const fd = new FormData();
      fd.append('file', file);
      return (await api.post<{ rows: number; inserted: number; updated: number }>('/audience/import', fd)).data;
    },
    onSuccess: (d) => {
      toast.success(`${fmt(d.rows)} filas: ${fmt(d.inserted)} nuevas, ${fmt(d.updated)} actualizadas`);
      qc.invalidateQueries({ queryKey: ['audience-summary'] });
    },
    onError: (e: any) => toast.error(e?.response?.data?.error ?? 'No se pudo cargar el archivo'),
  });

  const places = useMutation({
    mutationFn: async (v: { dataset: string | null; max: number }) => (await api.post('/audience/places', v)).data,
    onSuccess: () => toast.success('Buscando en la base de leads y en Google. El avance aparece acá en unos minutos.'),
    onError: () => toast.error('No se pudo arrancar la búsqueda'),
  });

  const rows = useMemo(() => (summary.data ?? []).filter((r) => !product || r.productKey === product), [summary.data, product]);
  const products = useMemo(() => [...new Set((summary.data ?? []).map((r) => r.productKey))], [summary.data]);
  const total = sum(rows);

  function runGoogle(dataset: string | null, pending: number) {
    if (!pending) return;
    const max = Math.min(pending, 500);
    const usd = (max * 0.03).toFixed(0);
    if (!confirm(`Buscar ${fmt(max)} negocios en Google Maps (primero se cruzan gratis con la base de leads). Costo máximo aprox. USD ${usd}. ¿Seguimos?`)) return;
    places.mutate({ dataset, max });
  }

  return (
    <div className="space-y-4">
      <div className="flex items-center justify-between gap-2 flex-wrap">
        <div>
          <h1 className="text-xl md:text-2xl font-bold">Públicos de competencia</h1>
          <p className="text-sm text-slate-500">Clientes de la competencia para públicos de Meta. No son leads: nunca se les escribe.</p>
        </div>
        <div className="flex items-center gap-2 flex-wrap">
          <select className="input w-40" value={product} onChange={(e) => setProduct(e.target.value)}>
            <option value="">Todas las apps</option>
            {products.map((p) => <option key={p} value={p}>{PRODUCT_LABEL[p] ?? p}</option>)}
          </select>
          <button className="btn-secondary text-xs" onClick={() => fileRef.current?.click()} disabled={importFile.isPending}>
            {importFile.isPending ? 'Cargando…' : 'Cargar listado (.json / .json.gz)'}
          </button>
          <input ref={fileRef} type="file" accept=".json,.gz" hidden
            onChange={(e) => { const f = e.target.files?.[0]; if (f) importFile.mutate(f); e.target.value = ''; }} />
        </div>
      </div>

      <div className="grid grid-cols-2 md:grid-cols-4 gap-3">
        <Kpi label="Negocios" value={fmt(total.total)} />
        <Kpi label="Con WhatsApp" value={fmt(total.whatsapp)} sub={pct(total.whatsapp, total.total)} />
        <Kpi label="Encontrados después (IG · web · Google)" value={fmt(total.fromInstagram + total.fromWeb + total.fromLeads + total.fromGoogle)}
          sub={`IG ${fmt(total.fromInstagram)} · web ${fmt(total.fromWeb)} · leads ${fmt(total.fromLeads)} · Google ${fmt(total.fromGoogle)}`} />
        <Kpi label="Falta buscar" value={fmt(total.finderPending)}
          sub={`${fmt(total.googlePending)} esperan Google`} />
      </div>

      <div className="card overflow-x-auto">
        <div className="p-3 border-b border-slate-100 flex items-center gap-2 flex-wrap text-sm">
          <span className="font-medium mr-2">Batches de la extensión</span>
          <select className="input text-xs" value={bApp} onChange={(e) => { setBApp(e.target.value); setBDataset(''); }}>
            {Object.entries(PRODUCT_LABEL).map(([k, v]) => <option key={k} value={k}>{v}</option>)}
          </select>
          <select className="input text-xs" value={bDataset} onChange={(e) => setBDataset(e.target.value)}>
            <option value="">Todos los listados (por prioridad)</option>
            {(summary.data ?? []).filter((r) => r.productKey === bApp && r.finderPending > 0).map((r) => (
              <option key={r.dataset + r.country} value={r.dataset}>{r.competitor} · {r.dataset} ({fmt(r.finderPending)} pendientes)</option>
            ))}
          </select>
          <input className="input text-xs w-20" type="number" min={1} max={2000} value={bSize} onChange={(e) => setBSize(Number(e.target.value) || 1)} />
          <button className="btn-primary text-xs" disabled={createBatch.isPending} onClick={() => createBatch.mutate()}>
            {createBatch.isPending ? 'Armando…' : `Armar batch de ${bSize}`}
          </button>
          <span className="text-xs text-slate-500">La extensión solo trabaja batches abiertos, del más viejo al más nuevo, con la cuenta de IG de esa app.</span>
        </div>
        <table className="w-full text-sm">
          <thead className="text-xs text-slate-500 border-b border-slate-200">
            <tr>
              <th className="text-left p-2">#</th>
              <th className="text-left p-2">Armado</th>
              <th className="text-left p-2">App · listado</th>
              <th className="text-right p-2">Hechos</th>
              <th className="text-right p-2">Con número</th>
              <th className="text-right p-2">IG / Web / Google</th>
              <th className="text-right p-2">Follows</th>
              <th className="text-left p-2">Sin número, por qué</th>
              <th className="text-left p-2">Estado</th>
              <th className="p-2"></th>
            </tr>
          </thead>
          <tbody>
            {(batches.data ?? []).filter((b) => !product || b.productKey === product).map((b) => {
              const done = b.size - b.pending;
              const state = b.status === 'closed' ? 'cerrado' : b.pending === 0 ? 'terminado' : b.lastActivity ? 'en curso' : 'en espera';
              return (
                <tr key={b.id} className="border-b border-slate-100 align-top">
                  <td className="p-2 font-medium">#{b.number}</td>
                  <td className="p-2 whitespace-nowrap">{fmtAt(b.createdAt)}{b.retriedAt && <div className="text-xs text-slate-500">reintento {fmtAt(b.retriedAt)}</div>}</td>
                  <td className="p-2">{PRODUCT_LABEL[b.productKey] ?? b.productKey}{b.dataset && <span className="text-slate-500"> · {b.dataset}</span>}</td>
                  <td className="p-2 text-right whitespace-nowrap">{fmt(done)}/{fmt(b.size)}</td>
                  <td className="p-2 text-right font-medium text-emerald-600">{fmt(b.found)} <span className="text-slate-400 font-normal">{pct(b.found, done)}</span></td>
                  <td className="p-2 text-right">{b.foundInstagram} / {b.foundWeb} / {b.foundGoogle}</td>
                  <td className="p-2 text-right">{fmt(b.follows)}</td>
                  <td className="p-2 text-xs text-slate-600">{Object.entries(b.reasons).sort((x, y) => y[1] - x[1]).map(([r, n]) => `${r}: ${n}`).join(' · ') || '—'}</td>
                  <td className="p-2 text-xs whitespace-nowrap">{state}{b.lastActivity && <div className="text-slate-500">último {fmtAt(b.lastActivity)}</div>}</td>
                  <td className="p-2 text-xs whitespace-nowrap space-x-2">
                    {b.status !== 'closed' && b.pending > 0 && (
                      <button className="underline text-rose-600" disabled={batchAction.isPending}
                        onClick={() => { if (confirm(`¿Cerrar el batch #${b.number}? Los ${b.pending} que faltan quedan libres.`)) batchAction.mutate({ id: b.id, action: 'close' }); }}>Cerrar</button>
                    )}
                    {done - b.found > 0 && (
                      <button className="underline text-brand-600" disabled={batchAction.isPending}
                        onClick={() => { if (confirm(`¿Volver a buscar los ${done - b.found} del batch #${b.number} que salieron sin número?`)) batchAction.mutate({ id: b.id, action: 'retry' }); }}>Reintentar sin número</button>
                    )}
                  </td>
                </tr>
              );
            })}
            {batches.data?.length === 0 && <tr><td colSpan={10} className="p-4 text-slate-500">Todavía no hay batches: armá uno y la extensión lo toma en unos minutos.</td></tr>}
          </tbody>
        </table>
      </div>

      <div className="card overflow-x-auto">
        {summary.isLoading ? <div className="p-5">Cargando…</div> : rows.length === 0 ? (
          <div className="p-5 text-slate-500">Todavía no hay listados cargados.</div>
        ) : (
          <table className="w-full text-sm">
            <thead className="text-xs text-slate-500 border-b border-slate-200">
              <tr>
                <th className="text-left p-2">App</th>
                <th className="text-left p-2">Competidor</th>
                <th className="text-left p-2">Listado</th>
                <th className="text-left p-2">País</th>
                <th className="text-right p-2">Negocios</th>
                <th className="text-right p-2">Con WhatsApp</th>
                <th className="text-right p-2">Del competidor</th>
                <th className="text-right p-2">IG</th>
                <th className="text-right p-2">Web</th>
                <th className="text-right p-2">Leads</th>
                <th className="text-right p-2">Google</th>
                <th className="text-right p-2">Extensión</th>
                <th className="text-right p-2">Google pend.</th>
              </tr>
            </thead>
            <tbody className="divide-y divide-slate-100">
              {rows.map((r) => {
                const toSearch = r.finderDone + r.finderPending;
                return (
                  <tr key={`${r.dataset}|${r.country}`}>
                    <td className="p-2">{PRODUCT_LABEL[r.productKey] ?? r.productKey}</td>
                    <td className="p-2">{r.competitor}{r.segment === 'baja' && <span className="ml-1 text-xs text-amber-600">bajas</span>}</td>
                    <td className="p-2 text-slate-500">{r.dataset}</td>
                    <td className="p-2">{r.country ?? '—'}</td>
                    <td className="p-2 text-right">{fmt(r.total)}</td>
                    <td className="p-2 text-right font-medium">{fmt(r.whatsapp)} <span className="text-xs text-slate-400">{pct(r.whatsapp, r.total)}</span></td>
                    <td className="p-2 text-right">{fmt(r.fromCompetitor)}</td>
                    <td className="p-2 text-right">{fmt(r.fromInstagram)}</td>
                    <td className="p-2 text-right">{fmt(r.fromWeb)}</td>
                    <td className="p-2 text-right">{fmt(r.fromLeads)}</td>
                    <td className="p-2 text-right">{fmt(r.fromGoogle)}</td>
                    <td className="p-2 text-right text-xs">{toSearch ? `${fmt(r.finderDone)}/${fmt(toSearch)}` : '—'}</td>
                    <td className="p-2 text-right">
                      {r.googlePending > 0 ? (
                        <button className="text-brand-600 text-xs underline" disabled={places.isPending}
                          onClick={() => runGoogle(r.dataset, r.googlePending)}>Buscar {fmt(r.googlePending)}</button>
                      ) : <span className="text-slate-400 text-xs">—</span>}
                    </td>
                  </tr>
                );
              })}
            </tbody>
          </table>
        )}
      </div>

      <div className="card overflow-x-auto">
        <div className="p-3 border-b border-slate-100 text-sm font-medium">Por día: cuántos salieron y por qué no el resto</div>
        <table className="w-full text-sm">
          <thead className="text-xs text-slate-500 border-b border-slate-200">
            <tr>
              <th className="text-left p-2">Día</th>
              <th className="text-left p-2">App</th>
              <th className="text-right p-2">Procesados</th>
              <th className="text-right p-2">Con número</th>
              <th className="text-right p-2">IG</th>
              <th className="text-right p-2">Web</th>
              <th className="text-right p-2">Google</th>
              <th className="text-right p-2">Follows</th>
              <th className="text-left p-2">Sin número, por qué</th>
            </tr>
          </thead>
          <tbody>
            {(days.data ?? []).filter((d) => !product || d.productKey === product).map((d) => (
              <tr key={d.day + d.productKey} className="border-b border-slate-100 align-top">
                <td className="p-2 whitespace-nowrap">{new Date(d.day + 'T12:00:00').toLocaleDateString('es-AR', { weekday: 'short', day: '2-digit', month: '2-digit' })}</td>
                <td className="p-2">{PRODUCT_LABEL[d.productKey] ?? d.productKey}</td>
                <td className="p-2 text-right">{fmt(d.processed)}</td>
                <td className="p-2 text-right font-medium text-emerald-600">{fmt(d.found)} <span className="text-slate-400 font-normal">{pct(d.found, d.processed)}</span></td>
                <td className="p-2 text-right">{fmt(d.foundInstagram)}</td>
                <td className="p-2 text-right">{fmt(d.foundWeb)}</td>
                <td className="p-2 text-right">{fmt(d.foundGoogle)}</td>
                <td className="p-2 text-right">{fmt(d.follows)}</td>
                <td className="p-2 text-xs text-slate-600">
                  {Object.entries(d.reasons).sort((a, b) => b[1] - a[1]).map(([r, n]) => `${r}: ${n}`).join(' · ') || '—'}
                </td>
              </tr>
            ))}
            {days.data?.length === 0 && <tr><td colSpan={9} className="p-4 text-slate-500">Todavía no hay búsquedas.</td></tr>}
          </tbody>
        </table>
      </div>

      <div className="card">
        <div className="p-3 border-b border-slate-100 text-sm font-medium">Movimientos (cada paso de la búsqueda, con fecha)</div>
        <div className="divide-y divide-slate-100 text-sm max-h-[32rem] overflow-y-auto">
          {(events.data ?? []).map((e, i) => (
            <div key={i} className="p-2 space-y-1">
              <div className="flex gap-2 flex-wrap items-baseline">
                <span className="font-medium">{e.name}</span>
                <span className="text-xs text-slate-400">{PRODUCT_LABEL[e.productKey] ?? e.productKey} · {e.dataset}{e.city ? ` · ${e.city}` : ''}</span>
                {e.finderStatus && <span className="text-xs text-slate-500">→ {STATUS_LABEL[e.finderStatus] ?? e.finderStatus}</span>}
                {e.phone && <span className="font-mono text-xs">{e.phone}</span>}
              </div>
              <ol className="text-xs text-slate-500 space-y-0.5 pl-3">
                {e.steps.map((st, j) => (
                  <li key={j}>
                    <span className="text-slate-400">{fmtAt(st.at)}</span> · {STEP_LABEL[st.step] ?? st.step}:{' '}
                    <span className={RESULT_CLASS[st.result] ?? ''}>{RESULT_LABEL[st.result] ?? st.result}</span>
                    {st.detail && <span className="text-slate-400"> · {st.detail}</span>}
                  </li>
                ))}
              </ol>
            </div>
          ))}
          {events.data?.length === 0 && <div className="p-4 text-slate-500">Todavía no hay movimientos: la extensión arranca a las 9.</div>}
        </div>
      </div>

      <div className="card">
        <div className="p-3 border-b border-slate-100 text-sm font-medium">Últimos números encontrados</div>
        <div className="divide-y divide-slate-100 text-sm max-h-96 overflow-y-auto">
          {(recent.data ?? []).map((r, i) => (
            <div key={i} className="p-2 flex gap-3 flex-wrap items-center">
              <span className="w-28 text-xs text-slate-500 shrink-0">
                {new Date(r.updatedAt).toLocaleString('es-AR', { day: '2-digit', month: '2-digit', hour: '2-digit', minute: '2-digit' })}
              </span>
              <span className="flex-1 min-w-40">{r.name} <span className="text-xs text-slate-400">· {r.city ?? r.country ?? ''} · {r.dataset}</span></span>
              <span className="font-mono text-xs">{r.phone}</span>
              <span className="text-xs text-slate-500">{SOURCE_LABEL[r.phoneSource ?? ''] ?? r.phoneSource}{r.whatsappProbable ? ' · WhatsApp' : ''}</span>
            </div>
          ))}
          {recent.data?.length === 0 && <div className="p-4 text-slate-500">Todavía no hay números encontrados por la extensión o Google.</div>}
        </div>
      </div>
    </div>
  );
}

function Kpi({ label, value, sub }: { label: string; value: string; sub?: string }) {
  return (
    <div className="card p-4">
      <div className="text-xs text-slate-500">{label}</div>
      <div className="text-2xl font-bold">{value}</div>
      {sub && <div className="text-xs text-slate-500 mt-1">{sub}</div>}
    </div>
  );
}
