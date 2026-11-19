import { Fragment, useEffect, useMemo, useState } from 'react';
import { Link } from 'react-router-dom';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import toast from 'react-hot-toast';
import { api } from '../lib/api';
import Switch from '../components/Switch';
import QrConnectModal from '../components/QrConnectModal';

// Menú Leads (Eric 19/11/2026): ranking de prospectos con puntaje, búsqueda en Places por rubro × zona y
// la línea outbound, que le escribe sola a los mejores con un tope de contactos nuevos por día.

type Rubro = { key: string; label: string; searchTerms: string[] };
type Zone = { key: string; label: string };
type Part = { key: string; label: string; points: number; max: number; why: string };
type Detail = { rubro: string | null; zona: string | null; parts: Part[] };

type Contact = {
  leadId: string; name: string; score: number; status: string; createdAt: string;
  sentAt: string | null; repliedAt: string | null; message: string; error: string | null;
};

type Line = {
  id: string; instanceName: string; label: string; phone: string | null; status: string; listenOnly: boolean;
  productKey: string | null; cap: number; used: number; total: number; minScore: number; rubros: string[];
  senderName: string | null; senderNameEffective: string | null; template: string | null; paused: boolean;
  requireWhatsapp: boolean;
  canSend: boolean; reason: string; nextAt: string | null; eligible: number; contacts: Contact[];
};

type Reader = {
  account: string; at: string;
  state: { version?: string; state?: string; queue?: number; igToday?: number; lastError?: string | null; restUntil?: string | null; blockedUntil?: string | null };
};

type Info = {
  lines: Line[];
  readers: Reader[];
  products: { productKey: string; displayName: string }[];
  rubros: Rubro[];
  zones: Zone[];
  defaultTemplate: string;
};

type Row = {
  leadId: string; name: string; rubro: string | null; city: string | null; address: string | null;
  rating: number | null; reviews: number | null; website: string | null; instagram: string | null;
  phone: string | null; phoneOrigin: string | null; bookingPlatform: string | null; score: number | null;
  instagramFollowers: number | null; instagramBioLink: string | null; instagramCheckedAt: string | null;
  detail: Detail | null; status: string; excluded: boolean; reserved: boolean; eligible: boolean;
  blocked: string | null; mapsUrl: string | null; createdAt: string;
};

type Ranking = {
  rows: Row[];
  counts: {
    eligible: number; eligibleOver50: number; reserved: number; pendingEnrich: number; verified: number; igPending: number; igChecked: number;
    byRubro: { rubro: string | null; count: number; over50: number }[];
  };
};

const ar = (iso: string | null) =>
  iso ? new Date(iso).toLocaleString('es-AR', { timeZone: 'America/Argentina/Buenos_Aires', day: '2-digit', month: '2-digit', hour: '2-digit', minute: '2-digit' }) : '—';

const ORIGIN: Record<string, string> = {
  'whatsapp-publicado': 'WhatsApp (en su web)',
  'whatsapp-instagram': 'WhatsApp (en su Instagram)',
  'maps-celular': 'celular (Maps)',
  'maps-fijo': 'fijo (Maps)',
  instagram: 'del Instagram',
  web: 'de la web',
};

function useInfo() {
  return useQuery({
    queryKey: ['outbound'],
    queryFn: async () => (await api.get<Info>('/outbound')).data,
    refetchInterval: 30_000,
  });
}

function ScoreBadge({ score }: { score: number | null }) {
  const s = score ?? 0;
  const cls = s >= 70 ? 'bg-emerald-600 text-white' : s >= 50 ? 'bg-emerald-100 text-emerald-800' : s >= 35 ? 'bg-amber-100 text-amber-800' : 'bg-slate-100 text-slate-500';
  return <span className={`inline-flex w-10 justify-center rounded-md px-1.5 py-0.5 text-sm font-semibold tabular-nums ${cls}`}>{score ?? '—'}</span>;
}

/**
 * El lector de Instagram: la extensión de Chrome de la Mac de Eric. Arranca sola cuando se prende la Mac (Chrome
 * abre al iniciar sesión), le manda un latido a sales-hub cada 2 minutos y pide de a un perfil: los leads del
 * outbound con Instagram sin leer van primero. Sin latido en 6 minutos = Mac apagada o Chrome cerrado.
 */
const vnum = (v?: string) => (v ?? '0').split('.').map((x) => x.padStart(4, '0')).join('.');

function ReaderStatus({ readers }: { readers: Reader[] }) {
  const isOn = (r: Reader) => Date.now() - new Date(r.at).getTime() < 6 * 60_000;
  // La copia con la versión más nueva es la de la Mac (la extensión se actualiza sola desde el repo).
  const newest = readers.reduce((m, r) => (vnum(r.state.version) > m ? vnum(r.state.version) : m), '');
  const anyOn = readers.some(isOn);
  return (
    <div className={`rounded-lg border px-3 py-2 text-xs space-y-1 ${anyOn ? 'border-emerald-200 bg-emerald-50 text-emerald-900' : 'border-slate-200 bg-slate-50 text-slate-600'}`}>
      <div><b>Lector de Instagram</b> (extensión de Chrome; un latido cada 2 minutos mientras la compu está prendida)</div>
      {readers.length === 0 && <div>Nunca se conectó. Abrí Chrome en el perfil efcloud.tech.</div>}
      {readers.map((r) => {
        const on = isOn(r);
        const old = vnum(r.state.version) < newest;
        return (
          <div key={r.account} className="flex flex-wrap items-center gap-x-2">
            <span className={`inline-block h-2 w-2 rounded-full ${on ? 'bg-emerald-500' : 'bg-slate-400'}`} />
            <b>@{r.account}</b>
            {on
              ? <span>prendido · {r.state.state ?? '—'}{r.state.igToday != null && <> · {r.state.igToday} perfiles hoy</>} · último latido {ar(r.at)}</span>
              : <span>apagado desde {ar(r.at)} (compu apagada o Chrome cerrado; retoma solo al prenderse)</span>}
            {old && <span className="rounded bg-amber-100 px-1.5 text-amber-800">versión vieja: actualizala o sacala</span>}
            {r.state.lastError && <span className="text-rose-700">{r.state.lastError}</span>}
          </div>
        );
      })}
    </div>
  );
}

// ─── Ranking ──────────────────────────────────────────────────────────────────────────

export function RankingProspectos() {
  const qc = useQueryClient();
  const info = useInfo();
  const [productKey, setProductKey] = useState('turnospro');
  const [rubro, setRubro] = useState('');
  const [all, setAll] = useState(false);
  const [open, setOpen] = useState<string | null>(null);

  const q = useQuery({
    queryKey: ['outbound-ranking', productKey, rubro, all],
    queryFn: async () => (await api.get<Ranking>('/outbound/ranking', { params: { productKey, rubro: rubro || undefined, all, take: 200 } })).data,
    refetchInterval: 60_000,
  });

  const rescore = useMutation({
    mutationFn: async () => (await api.post<{ rescored: number }>('/outbound/rescore', null, { params: { productKey } })).data,
    onSuccess: (r) => { toast.success(`${r.rescored} leads recalculados`); qc.invalidateQueries({ queryKey: ['outbound-ranking'] }); },
    onError: (e: any) => toast.error(e?.response?.data?.error ?? 'No se pudo recalcular'),
  });

  const exclude = useMutation({
    mutationFn: async ({ id, excluded }: { id: string; excluded: boolean }) => (await api.post(`/outbound/leads/${id}/exclude`, { excluded })).data,
    onSuccess: () => qc.invalidateQueries({ queryKey: ['outbound-ranking'] }),
    onError: () => toast.error('No se pudo cambiar'),
  });

  const rubroLabel = (k: string | null) => info.data?.rubros.find((r) => r.key === k)?.label ?? (k ?? 'otro');
  const c = q.data?.counts;

  return (
    <div className="space-y-4 max-w-6xl">
      <div>
        <h1 className="text-xl md:text-2xl font-bold">Ranking de prospectos</h1>
        <p className="text-sm text-slate-500 mt-1">
          Qué tan buen cliente puede ser cada negocio, antes de hablarle. Suman: zona (25), reseñas de Maps (25), que el
          número sea WhatsApp (20), web e Instagram (15) y rubro (15). Restan: si ya usa otro sistema de turnos (−20) o es
          una cadena (−10). La línea outbound le escribe a los de arriba. Tocá una fila para ver el desglose.
        </p>
      </div>

      <div className="flex flex-wrap items-center gap-2 text-sm">
        <select className="input" value={productKey} onChange={(e) => setProductKey(e.target.value)}>
          {(info.data?.products ?? [{ productKey: 'turnospro', displayName: 'TurnosPro' }]).map((p) => (
            <option key={p.productKey} value={p.productKey}>{p.displayName}</option>
          ))}
        </select>
        <button className={`rounded-full border px-3 py-1 ${rubro === '' ? 'bg-slate-800 text-white' : 'bg-white'}`} onClick={() => setRubro('')}>
          Todos
        </button>
        {(info.data?.rubros ?? []).map((r) => {
          const n = c?.byRubro.find((b) => b.rubro === r.key);
          return (
            <button key={r.key} className={`rounded-full border px-3 py-1 ${rubro === r.key ? 'bg-slate-800 text-white' : 'bg-white'}`} onClick={() => setRubro(r.key)}>
              {r.label} <span className="opacity-60">{n ? `${n.over50}/${n.count}` : 0}</span>
            </button>
          );
        })}
        <label className="ml-auto flex items-center gap-2 text-slate-600">
          <Switch on={all} onClick={() => setAll(!all)} /> ver también los que no se pueden contactar
        </label>
        <button className="btn-secondary" disabled={rescore.isPending} onClick={() => rescore.mutate()}>
          {rescore.isPending ? 'Recalculando…' : 'Recalcular'}
        </button>
      </div>

      {info.data && <ReaderStatus readers={info.data.readers ?? []} />}

      {c && (
        <div className="text-sm text-slate-600">
          <b>{c.eligible}</b> contactables · <b>{c.verified}</b> con WhatsApp verificado (a esos les escribe la línea) ·{' '}
          {c.eligibleOver50} con 50 o más · {c.reserved} traídos para el outbound
          <div className="mt-1 text-xs text-slate-500">
            Instagram: <b>{c.igChecked}</b> leídos · <b className="text-amber-700">{c.igPending}</b> sin leer. Los sin leer tienen
            puntaje provisorio: al leerlos puede subir (WhatsApp en la bio, seguidores) o bajar (link a AgendaPro/Fresha, cadena).
          </div>
          {c.pendingEnrich > 0 && <> · {c.pendingEnrich} todavía sin nutrir (se lee su web de a 20 por minuto)</>}
        </div>
      )}

      <div className="rounded-xl border bg-white overflow-x-auto">
        <table className="w-full text-sm">
          <thead className="bg-slate-50 text-left text-xs text-slate-500">
            <tr>
              <th className="px-3 py-2">#</th>
              <th className="px-3 py-2">Puntaje</th>
              <th className="px-3 py-2">Negocio</th>
              <th className="px-3 py-2">Rubro</th>
              <th className="px-3 py-2">Maps</th>
              <th className="px-3 py-2">Número</th>
              <th className="px-3 py-2">Web / IG</th>
              <th className="px-3 py-2"></th>
            </tr>
          </thead>
          <tbody>
            {q.isLoading && <tr><td colSpan={8} className="px-3 py-6 text-center text-slate-400">Cargando…</td></tr>}
            {q.data?.rows.length === 0 && (
              <tr><td colSpan={8} className="px-3 py-6 text-center text-slate-400">No hay prospectos todavía. Traé con la pestaña Buscar.</td></tr>
            )}
            {q.data?.rows.map((r, i) => (
              <Fragment key={r.leadId}>
                <tr className={`border-t cursor-pointer hover:bg-slate-50 ${r.eligible ? '' : 'opacity-60'}`} onClick={() => setOpen(open === r.leadId ? null : r.leadId)}>
                  <td className="px-3 py-2 text-slate-400 tabular-nums">{i + 1}</td>
                  <td className="px-3 py-2"><ScoreBadge score={r.score} /></td>
                  <td className="px-3 py-2">
                    <Link to={`/leads/${r.leadId}`} className="font-medium hover:underline" onClick={(e) => e.stopPropagation()}>{r.name}</Link>
                    <div className="text-xs text-slate-500">
                      {r.detail?.zona ?? r.city ?? ''}{r.blocked && <span className="text-rose-600"> · {r.blocked}</span>}
                      {r.instagram && !r.instagramCheckedAt && <span className="ml-1 rounded bg-amber-100 px-1.5 py-0.5 text-[10px] font-medium text-amber-800">Instagram sin leer · puntaje provisorio</span>}
                    </div>
                  </td>
                  <td className="px-3 py-2 whitespace-nowrap">{rubroLabel(r.rubro)}</td>
                  <td className="px-3 py-2 whitespace-nowrap">
                    {r.rating != null ? <>★ {r.rating.toFixed(1)}</> : '—'} <span className="text-slate-400">({r.reviews ?? 0})</span>
                  </td>
                  <td className={`px-3 py-2 whitespace-nowrap text-xs ${r.phoneOrigin?.startsWith('whatsapp-') ? 'text-emerald-700 font-medium' : ''}`}>
                    {r.phoneOrigin ? ORIGIN[r.phoneOrigin] ?? r.phoneOrigin : '—'}
                  </td>
                  <td className="px-3 py-2 text-xs">
                    {r.website && <a href={r.website} target="_blank" rel="noreferrer" className="text-sky-700 hover:underline" onClick={(e) => e.stopPropagation()}>web</a>}
                    {r.instagram && <> {r.website && '· '}<a href={`https://instagram.com/${r.instagram}`} target="_blank" rel="noreferrer" className="text-sky-700 hover:underline" onClick={(e) => e.stopPropagation()}>@{r.instagram}</a></>}
                    {r.instagramCheckedAt && <div className="text-slate-400">IG leído{r.instagramFollowers != null && <> · {r.instagramFollowers.toLocaleString('es-AR')} seg.</>}</div>}
                    {r.bookingPlatform && <div className="text-rose-600">usa {r.bookingPlatform}</div>}
                  </td>
                  <td className="px-3 py-2 text-right whitespace-nowrap">
                    <button className="text-xs text-slate-500 hover:text-slate-900 underline"
                      onClick={(e) => { e.stopPropagation(); exclude.mutate({ id: r.leadId, excluded: !r.excluded }); }}>
                      {r.excluded ? 'volver a incluir' : 'excluir'}
                    </button>
                  </td>
                </tr>
                {open === r.leadId && (
                  <tr className="bg-slate-50">
                    <td></td>
                    <td colSpan={7} className="px-3 py-3">
                      <div className="grid gap-1 sm:grid-cols-2 text-xs">
                        {(r.detail?.parts ?? []).map((p) => (
                          <div key={p.key} className="flex gap-2">
                            <span className={`w-14 text-right tabular-nums font-semibold ${p.points < 0 ? 'text-rose-600' : ''}`}>
                              {p.points}{p.max > 0 && <span className="font-normal text-slate-400">/{p.max}</span>}
                            </span>
                            <span className="text-slate-600">{p.label}:</span>
                            <span>{p.why}</span>
                          </div>
                        ))}
                      </div>
                      <div className="mt-2 text-xs text-slate-500">
                        {r.address} {r.mapsUrl && <> · <a href={r.mapsUrl} target="_blank" rel="noreferrer" className="text-sky-700 underline">ver en Maps</a></>}
                        {r.instagramBioLink && <> · link de la bio: {r.instagramBioLink}</>}
                        {r.phone && <> · {r.phone}</>}
                      </div>
                    </td>
                  </tr>
                )}
              </Fragment>
            ))}
          </tbody>
        </table>
      </div>
    </div>
  );
}

// ─── Buscar ───────────────────────────────────────────────────────────────────────────

type SearchResult = { queries: number; found: number; created: number; duplicates: number; discarded: number };

export function BuscarProspectos() {
  const qc = useQueryClient();
  const info = useInfo();
  const [productKey, setProductKey] = useState('turnospro');
  const [rubros, setRubros] = useState<string[]>([]);
  const [zones, setZones] = useState<string[]>([]);
  const [perQuery, setPerQuery] = useState(10);
  const [running, setRunning] = useState(false);
  const [progress, setProgress] = useState<{ done: number; total: number; now: string; totals: SearchResult } | null>(null);

  useEffect(() => {
    if (info.data && rubros.length === 0 && zones.length === 0) {
      setRubros(info.data.rubros.map((r) => r.key));
      setZones(info.data.zones.map((z) => z.key));
    }
  }, [info.data]);

  const toggle = (list: string[], v: string) => (list.includes(v) ? list.filter((x) => x !== v) : [...list, v]);
  const terms = useMemo(
    () => (info.data?.rubros ?? []).filter((r) => rubros.includes(r.key)).reduce((n, r) => n + r.searchTerms.length, 0),
    [info.data, rubros],
  );
  const queries = terms * zones.length;

  async function run() {
    if (!info.data) return;
    const combos = rubros.flatMap((r) => zones.map((z) => ({ r, z })));
    const totals: SearchResult = { queries: 0, found: 0, created: 0, duplicates: 0, discarded: 0 };
    setRunning(true);
    try {
      for (let i = 0; i < combos.length; i++) {
        const { r, z } = combos[i];
        const label = `${info.data.rubros.find((x) => x.key === r)?.label} en ${info.data.zones.find((x) => x.key === z)?.label}`;
        setProgress({ done: i, total: combos.length, now: label, totals: { ...totals } });
        try {
          const res = (await api.post<SearchResult>('/outbound/search', { productKey, rubro: r, zone: z, perQuery })).data;
          (Object.keys(totals) as (keyof SearchResult)[]).forEach((k) => { totals[k] += res[k]; });
        } catch (e: any) {
          toast.error(`${label}: ${e?.response?.data?.error ?? 'falló'}`);
        }
      }
      setProgress({ done: combos.length, total: combos.length, now: 'listo', totals });
      toast.success(`${totals.created} leads nuevos`);
      qc.invalidateQueries({ queryKey: ['outbound-ranking'] });
    } finally {
      setRunning(false);
    }
  }

  if (!info.data) return <div className="text-sm text-slate-400">Cargando…</div>;

  return (
    <div className="space-y-4 max-w-4xl">
      <div>
        <h1 className="text-xl md:text-2xl font-bold">Traer leads nuevos</h1>
        <p className="text-sm text-slate-500 mt-1">
          Busca en Google Places cada rubro en cada zona. Los nuevos entran reservados para la línea outbound (el reparto
          automático no los toca y no les sale la cadencia de siempre) y se puntúan solos. Los repetidos se saltean.
        </p>
      </div>

      <section className="rounded-xl border bg-white p-4 space-y-4 text-sm">
        <label className="flex items-center gap-2">
          <span className="w-16 text-slate-600">App</span>
          <select className="input" value={productKey} onChange={(e) => setProductKey(e.target.value)}>
            {info.data.products.map((p) => <option key={p.productKey} value={p.productKey}>{p.displayName}</option>)}
          </select>
        </label>
        <div>
          <div className="text-slate-600 mb-1">Rubros</div>
          <div className="flex flex-wrap gap-2">
            {info.data.rubros.map((r) => (
              <button key={r.key} type="button" onClick={() => setRubros(toggle(rubros, r.key))}
                className={`rounded-full border px-3 py-1 ${rubros.includes(r.key) ? 'bg-slate-800 text-white' : 'bg-white'}`}
                title={`Se busca: ${r.searchTerms.join(', ')}`}>
                {r.label}
              </button>
            ))}
          </div>
        </div>
        <div>
          <div className="text-slate-600 mb-1">Zonas</div>
          <div className="flex flex-wrap gap-2">
            {info.data.zones.map((z) => (
              <button key={z.key} type="button" onClick={() => setZones(toggle(zones, z.key))}
                className={`rounded-full border px-3 py-1 ${zones.includes(z.key) ? 'bg-slate-800 text-white' : 'bg-white'}`}>
                {z.label}
              </button>
            ))}
          </div>
        </div>
        <label className="flex items-center gap-2">
          <span className="text-slate-600">Resultados por búsqueda</span>
          <input type="number" min={1} max={20} className="input w-20" value={perQuery} onChange={(e) => setPerQuery(Number(e.target.value))} />
        </label>
        <div className="text-xs text-slate-500">
          {queries} búsquedas · hasta {queries * perQuery} fichas de detalle de Google (cada ficha es una consulta paga de Places).
        </div>
        <button className="btn-primary" disabled={running || queries === 0} onClick={run}>
          {running ? 'Buscando…' : `Buscar ${queries}`}
        </button>
        {progress && (
          <div className="space-y-1">
            <div className="h-2.5 rounded-full bg-slate-100 overflow-hidden">
              <div className="h-full bg-slate-800" style={{ width: `${(100 * progress.done) / Math.max(1, progress.total)}%` }} />
            </div>
            <div className="text-xs text-slate-500">
              {progress.done}/{progress.total} · {progress.now} · {progress.totals.created} nuevos, {progress.totals.duplicates} repetidos,{' '}
              {progress.totals.discarded} descartados (sin teléfono ni web, cerrados o mal puntuados)
            </div>
          </div>
        )}
      </section>
    </div>
  );
}

// ─── Línea outbound ───────────────────────────────────────────────────────────────────

export function LineaOutbound() {
  const qc = useQueryClient();
  const info = useInfo();
  const [qrFor, setQrFor] = useState<Line | null>(null);
  const [creating, setCreating] = useState(false);
  const [form, setForm] = useState({ label: '', productKey: 'turnospro', senderName: '' });

  async function create() {
    if (!form.label.trim()) return toast.error('Poné un nombre para el teléfono');
    if (!form.senderName.trim()) return toast.error('Poné con qué nombre se presenta');
    setCreating(true);
    try {
      const line = (await api.post<{ id: string }>('/phone-lines', {
        label: form.label.trim(), productKey: form.productKey, extraProductKeys: [], listenOnly: false, importHistory: false,
      })).data;
      const saved = (await api.put<Line>(`/outbound/lines/${line.id}`, {
        cap: 10, minScore: 50, rubros: info.data?.rubros.map((r) => r.key) ?? [], senderName: form.senderName.trim(), template: null, paused: false,
        requireWhatsapp: true,
      })).data;
      qc.invalidateQueries({ queryKey: ['outbound'] });
      setForm({ ...form, label: '', senderName: '' });
      setQrFor(saved);
    } catch (e: any) {
      toast.error(e?.response?.data?.error ?? 'No se pudo crear');
    } finally {
      setCreating(false);
    }
  }

  if (!info.data) return <div className="text-sm text-slate-400">Cargando…</div>;

  return (
    <div className="space-y-6 max-w-5xl">
      <div>
        <h1 className="text-xl md:text-2xl font-bold">Línea outbound</h1>
        <p className="text-sm text-slate-500 mt-1">
          Escaneás el QR y la línea le escribe sola a los mejores del ranking, de a uno: lunes a viernes de 10 a 19 y
          sábados de 10 a 13, unos 30 minutos entre uno y otro. Tiene un <b>tope por día</b>: con 10 abre 10 charlas
          nuevas por día, todos los días. Antes de cada envío verifica que el número tenga WhatsApp. Cuando contestan,
          el bot no se mete: le llega el pase a la persona de la app (con copia al maestro) y sigue ella. Al que no
          contesta no se le insiste.
        </p>
      </div>

      <ReaderStatus readers={info.data.readers ?? []} />

      {info.data.lines.map((l) => <LineCard key={l.id} line={l} info={info.data!} onQr={() => setQrFor(l)} />)}

      <section className="rounded-xl border bg-white p-4 space-y-3 text-sm">
        <div className="font-semibold">Nueva línea outbound</div>
        <div className="grid gap-3 sm:grid-cols-3">
          <label className="space-y-1">
            <div className="text-slate-600">Nombre del teléfono</div>
            <input className="input w-full" placeholder="Celu outbound 1" value={form.label} onChange={(e) => setForm({ ...form, label: e.target.value })} />
          </label>
          <label className="space-y-1">
            <div className="text-slate-600">App</div>
            <select className="input w-full" value={form.productKey} onChange={(e) => setForm({ ...form, productKey: e.target.value })}>
              {info.data.products.map((p) => <option key={p.productKey} value={p.productKey}>{p.displayName}</option>)}
            </select>
          </label>
          <label className="space-y-1">
            <div className="text-slate-600">Se presenta como</div>
            <input className="input w-full" placeholder="mateo" value={form.senderName} onChange={(e) => setForm({ ...form, senderName: e.target.value })} />
          </label>
        </div>
        <button className="btn-primary" disabled={creating} onClick={create}>{creating ? 'Creando…' : 'Crear y mostrar QR'}</button>
        <div className="text-xs text-slate-500">Arranca con 10 por día, puntaje mínimo 50, los 6 rubros y solo WhatsApp verificado. Se cambia después.</div>
      </section>

      {qrFor && (
        <QrConnectModal
          title={`Vincular ${qrFor.label}`}
          fetchUrl={`/phone-lines/${qrFor.id}/qr`}
          onClose={() => setQrFor(null)}
          onConnected={() => qc.invalidateQueries({ queryKey: ['outbound'] })}
        />
      )}
    </div>
  );
}

function LineCard({ line, info, onQr }: { line: Line; info: Info; onQr: () => void }) {
  const qc = useQueryClient();
  const initial = () => ({
    cap: line.cap, minScore: line.minScore, rubros: line.rubros, senderName: line.senderName ?? '',
    template: line.template ?? info.defaultTemplate, paused: line.paused, requireWhatsapp: line.requireWhatsapp,
  });
  const [form, setForm] = useState(initial);
  const [preview, setPreview] = useState<{ leadId: string; name: string; score: number; message: string }[] | null>(null);
  useEffect(() => { setForm(initial()); }, [line.id, line.cap, line.minScore, line.senderName, line.template, line.paused, line.requireWhatsapp, line.rubros.join(',')]);

  const save = useMutation({
    mutationFn: async (f: typeof form) => (await api.put<Line>(`/outbound/lines/${line.id}`, f)).data,
    onSuccess: () => { toast.success('Guardado'); qc.invalidateQueries({ queryKey: ['outbound'] }); },
    onError: (e: any) => toast.error(e?.response?.data?.error ?? 'No se pudo guardar'),
  });

  async function loadPreview() {
    try { setPreview((await api.get(`/outbound/lines/${line.id}/preview`)).data); }
    catch (e: any) { toast.error(e?.response?.data?.error ?? 'No se pudo armar la vista previa'); }
  }

  const connected = line.status === 'Connected';
  const toggle = (v: string) => setForm({ ...form, rubros: form.rubros.includes(v) ? form.rubros.filter((x) => x !== v) : [...form.rubros, v] });
  const dirty = JSON.stringify(form) !== JSON.stringify(initial());

  return (
    <section className="rounded-xl border bg-white p-4 space-y-4 text-sm">
      <div className="flex flex-wrap items-start justify-between gap-3">
        <div>
          <div className="font-semibold">{line.label} <span className="font-normal text-slate-400">{line.phone ? `+${line.phone}` : 'sin vincular'}</span></div>
          <div className={`text-xs ${line.canSend ? 'text-emerald-600' : 'text-slate-500'}`}>
            {line.reason}{line.nextAt && !line.canSend && <> · próximo {ar(line.nextAt)}</>} · {line.eligible} leads en condiciones
          </div>
        </div>
        <div className="flex items-center gap-2">
          {!connected && <button className="btn-primary" onClick={onQr}>Escanear QR</button>}
          <label className="flex items-center gap-2 text-slate-600">
            <Switch on={!form.paused} onClick={() => setForm({ ...form, paused: !form.paused })} title="Pausar o reanudar" />
            {form.paused ? 'en pausa' : 'activa'}
          </label>
        </div>
      </div>

      <div>
        <div className="h-3 rounded-full bg-slate-100 overflow-hidden">
          <div className="h-full bg-slate-800" style={{ width: `${(100 * line.used) / Math.max(1, line.cap)}%` }} />
        </div>
        <div className="mt-1 text-xs text-slate-500">{line.used} de {line.cap} contactos hoy · {line.total} en total</div>
      </div>

      <div className="grid gap-3 sm:grid-cols-3">
        <label className="space-y-1">
          <div className="text-slate-600">Contactos nuevos por día</div>
          <input type="number" min={0} max={50} className="input w-full" value={form.cap} onChange={(e) => setForm({ ...form, cap: Number(e.target.value) })} />
        </label>
        <label className="space-y-1">
          <div className="text-slate-600">Puntaje mínimo</div>
          <input type="number" min={0} max={100} className="input w-full" value={form.minScore} onChange={(e) => setForm({ ...form, minScore: Number(e.target.value) })} />
        </label>
        <label className="space-y-1">
          <div className="text-slate-600">Se presenta como</div>
          <input className="input w-full" placeholder={line.senderNameEffective ?? 'nombre de pila'} value={form.senderName} onChange={(e) => setForm({ ...form, senderName: e.target.value })} />
        </label>
      </div>

      <div>
        <div className="text-slate-600 mb-1">Rubros a los que les escribe</div>
        <div className="flex flex-wrap gap-2">
          {info.rubros.map((r) => (
            <button key={r.key} type="button" onClick={() => toggle(r.key)}
              className={`rounded-full border px-3 py-1 ${form.rubros.includes(r.key) ? 'bg-slate-800 text-white' : 'bg-white'}`}>
              {r.label}
            </button>
          ))}
        </div>
      </div>

      <label className="flex items-center gap-2 text-slate-600">
        <Switch on={form.requireWhatsapp} onClick={() => setForm({ ...form, requireWhatsapp: !form.requireWhatsapp })} />
        Solo a los que tienen verificado que toman turnos por WhatsApp (wa.me en su web o su Instagram)
      </label>

      <label className="block space-y-1">
        <div className="text-slate-600">Mensaje</div>
        <textarea className="input w-full font-mono text-xs" rows={4} value={form.template} onChange={(e) => setForm({ ...form, template: e.target.value })} />
        <div className="text-[11px] text-slate-400">
          {'{negocio}'} nombre del negocio (si es muy largo se saca "hablo con ...?") · {'{vendedor}'} · {'{app}'} · {'{rubros}'} "salones de uñas" ·
          {' {clientes}'} "tus clientas/pacientes" · {'{cobro}'} · {'{toman}'}. Sale sin tildes ni ¿.
        </div>
      </label>

      <div className="flex flex-wrap gap-2">
        <button className="btn-primary" disabled={!dirty || save.isPending} onClick={() => save.mutate(form)}>Guardar</button>
        <button className="btn-secondary" onClick={loadPreview}>Ver a quién le escribe y qué</button>
      </div>

      {preview && (
        <div className="space-y-2">
          {preview.length === 0 && <div className="text-xs text-slate-500">No hay leads con ese puntaje y esos rubros.</div>}
          {preview.map((p, i) => (
            <div key={p.leadId} className="rounded-lg border bg-slate-50 p-3">
              <div className="text-xs text-slate-500 mb-1">{i + 1}º · {p.name} · puntaje {p.score}</div>
              <div className="whitespace-pre-wrap">{p.message}</div>
            </div>
          ))}
        </div>
      )}

      {line.contacts.length > 0 && (
        <div className="overflow-x-auto">
          <table className="w-full text-xs">
            <thead className="text-left text-slate-500">
              <tr><th className="py-1 pr-3">Negocio</th><th className="py-1 pr-3">Puntaje</th><th className="py-1 pr-3">Enviado</th><th className="py-1 pr-3">Contestó</th></tr>
            </thead>
            <tbody>
              {line.contacts.map((c) => (
                <tr key={c.leadId} className="border-t" title={c.message}>
                  <td className="py-1 pr-3"><Link to={`/leads/${c.leadId}`} className="hover:underline">{c.name}</Link></td>
                  <td className="py-1 pr-3">{c.score}</td>
                  <td className="py-1 pr-3">{c.status === 'failed' ? <span className="text-rose-600">falló{c.error ? `: ${c.error}` : ''}</span> : ar(c.sentAt ?? c.createdAt)}</td>
                  <td className="py-1 pr-3">{c.repliedAt ? <span className="text-emerald-700 font-medium">{ar(c.repliedAt)}</span> : '—'}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </section>
  );
}
