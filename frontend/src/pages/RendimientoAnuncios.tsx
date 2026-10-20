// Rendimiento de anuncios (Marketing → Rendimiento de anuncios): cada anuncio de Meta con su video y
// cómo rinde en el rango elegido. Las métricas salen de la misma tabla que el mapa de anuncios
// (GET /meta-geo/table?dim=ad), el estado y el rubro de /meta-ads, y miniatura y video en vivo de
// /meta-ads/media y /meta-ads/{id}/video (las URLs de Meta vencen: no se guardan).
import { useEffect, useMemo, useState, type ReactNode } from 'react';
import { useSearchParams } from 'react-router-dom';
import { keepPreviousData, useQuery } from '@tanstack/react-query';
import clsx from 'clsx';
import { api } from '../lib/api';

// ── tipos de la API ─────────────────────────────────────────────────────────────────────

type M = {
  spend: number; impressions: number; clicks: number; ctr: number | null; hookRate: number | null; videoViews3s: number;
  conversations: number; costPerConversation: number | null;
  leads: number; leadsQualified: number; leadsClosed: number; costPerLead: number | null;
};
type Filters = { configured: boolean; lastSyncAt: string | null; today: string; products: { key: string; displayName: string }[]; countries: string[] };
type TableData = { from: string; to: string; days: number; rows: { keys: { id: string; label: string }[]; m: M }[] };
type MetaAd = {
  adId: string; name: string; status: string; productKey: string | null; rubro: string | null; angle: string | null;
  title: string | null; body: string | null; prefillText: string | null; videoId: string | null;
};
type Row = { adId: string; name: string; ad: MetaAd | undefined; m: M };

// ── formato ─────────────────────────────────────────────────────────────────────────────

const usdFmt = new Intl.NumberFormat('es-AR', { style: 'currency', currency: 'USD', minimumFractionDigits: 2, maximumFractionDigits: 2 });
const usd = (n: number | null | undefined) => (n == null ? '—' : usdFmt.format(n));
const num = (n: number | null | undefined) => (n == null ? '—' : Math.round(n).toLocaleString('es-AR'));
const pct = (n: number | null | undefined) => (n == null ? '—' : `${n.toLocaleString('es-AR', { minimumFractionDigits: 1, maximumFractionDigits: 1 })} %`);
const todayAr = () => new Date().toLocaleDateString('en-CA', { timeZone: 'America/Argentina/Buenos_Aires' });
const addDays = (iso: string, n: number) => {
  const d = new Date(`${iso}T12:00:00Z`);
  d.setUTCDate(d.getUTCDate() + n);
  return d.toISOString().slice(0, 10);
};

const RANGES = [{ days: 3, label: '3 días' }, { days: 7, label: '7 días' }, { days: 14, label: '14 días' }, { days: 30, label: '30 días' }, { days: 90, label: '90 días' }];
const COUNTRY_NAMES: Record<string, string> = { AR: 'Argentina', UY: 'Uruguay', MX: 'México', ES: 'España' };
const STATUS_LABEL: Record<string, string> = { ACTIVE: 'Activo', PAUSED: 'Pausado', CAMPAIGN_PAUSED: 'Campaña pausada', ADSET_PAUSED: 'Conjunto pausado', IN_PROCESS: 'Procesando', PENDING_REVIEW: 'En revisión', DISAPPROVED: 'Rechazado', WITH_ISSUES: 'Con problemas' };
// En "solo activos" cuentan también los que Meta está revisando: van a salir.
const RUNNING = new Set(['ACTIVE', 'IN_PROCESS', 'PENDING_REVIEW']);

// Solo se pintan los anuncios con datos suficientes.
const MIN_IMPRESSIONS = 1000;

// Escala de 5 colores contra referencias del mercado (no contra nuestro propio promedio).
// Fuentes (oct 2026): enganche = 3 s / impresiones, Meta en frío: <25 % flojo, 30-40 % bueno, 40 %+ excelente
// (Motion, Hawky, Madgicx). CTR (todos los clics): software 0,9-1,3 %, todas las industrias ~1,6 % (Superads,
// Two Minute Reports). Costo por conversación de anuncios a mensajes: USD 0,30-1,50 (Stackmatix). Costo por
// lead: no hay referencia pública para leads de WhatsApp (los de formulario de SaaS rondan USD 36-75 y no son
// comparables): se usa la de chats, un poco más exigente porque no todo chat termina en lead.
const LEVELS = [
  { label: 'Muy bien', cls: 'bg-emerald-200 text-emerald-900 font-semibold' },
  { label: 'Bien', cls: 'bg-emerald-50 text-emerald-800' },
  { label: 'En la media', cls: 'bg-slate-50 text-slate-700' },
  { label: 'Mal', cls: 'bg-orange-100 text-orange-800' },
  { label: 'Muy mal', cls: 'bg-red-200 text-red-900 font-semibold' },
];
// Cortes de "muy bien" a "mal": [muy bien desde, bien desde, media desde, mal desde]; debajo/encima, muy mal.
const BENCH: Partial<Record<ColKey, { cuts: [number, number, number, number]; lowerIsBetter?: boolean; ref: string }>> = {
  hookRate: { cuts: [40, 30, 25, 15], ref: 'mercado: 25-30 % es la media, 40 %+ excelente' },
  ctr: { cuts: [2.5, 1.5, 1, 0.7], ref: 'mercado: 1-1,5 % es la media (software ~1 %)' },
  costPerConversation: { cuts: [0.5, 1, 1.5, 3], lowerIsBetter: true, ref: 'mercado: USD 0,30-1,50 por conversación' },
  costPerLead: { cuts: [1, 2, 3, 5], lowerIsBetter: true, ref: 'sin referencia pública para leads de WhatsApp: USD 2-3 = media' },
};
const levelOf = (k: ColKey, v: number | null) => {
  const b = BENCH[k];
  if (v == null || !b) return null;
  const i = b.cuts.findIndex((c) => (b.lowerIsBetter ? v <= c : v >= c));
  return LEVELS[i < 0 ? 4 : i];
};

type ColKey = 'spend' | 'impressions' | 'hookRate' | 'ctr' | 'conversations' | 'costPerConversation' | 'leads' | 'costPerLead' | 'leadsQualified' | 'leadsClosed';
const COLS: { key: ColKey; label: string; hint: string; fmt: (n: number | null) => string; rank?: 'high' | 'low' }[] = [
  { key: 'spend', label: 'Gasto', hint: 'Lo que gastó en el rango (USD).', fmt: usd },
  { key: 'impressions', label: 'Impresiones', hint: 'Veces que se mostró.', fmt: num },
  { key: 'hookRate', label: 'Enganche', hint: 'De cada 100 que lo vieron, cuántos miraron 3 segundos.', fmt: pct, rank: 'high' },
  { key: 'ctr', label: 'CTR', hint: 'De cada 100 que lo vieron, cuántos tocaron.', fmt: pct, rank: 'high' },
  { key: 'conversations', label: 'Chats', hint: 'Conversaciones de WhatsApp que Meta le atribuye.', fmt: num },
  { key: 'costPerConversation', label: '$ / chat', hint: 'Gasto dividido chats.', fmt: usd, rank: 'low' },
  { key: 'leads', label: 'Leads', hint: 'Leads que entraron a sales-hub desde este anuncio.', fmt: num },
  { key: 'costPerLead', label: '$ / lead', hint: 'Gasto dividido leads de sales-hub.', fmt: usd, rank: 'low' },
  { key: 'leadsQualified', label: 'Interesados', hint: 'Leads interesados, con demo o cerrados.', fmt: num },
  { key: 'leadsClosed', label: 'Cerrados', hint: 'Leads que cerraron.', fmt: num },
];

const P = { days: 'dias', app: 'app', country: 'pais', all: 'todos', sort: 'orden' };

export default function RendimientoAnuncios() {
  const [params, setParams] = useSearchParams();
  const days = Number(params.get(P.days)) || 7;
  const apps = params.getAll(P.app);
  const countries = params.getAll(P.country);
  const onlyRunning = params.get(P.all) !== '1';
  const sortParam = params.get(P.sort) ?? 'spend:desc';
  const [sortKey, sortDir] = sortParam.split(':') as [ColKey, 'asc' | 'desc'];
  const [playing, setPlaying] = useState<Row | null>(null);

  const update = (fn: (sp: URLSearchParams) => void) => {
    const sp = new URLSearchParams(params);
    fn(sp);
    setParams(sp, { replace: true });
  };
  const toggleIn = (key: string, list: string[], v: string) => update((sp) => {
    sp.delete(key);
    (list.includes(v) ? list.filter((x) => x !== v) : [...list, v]).forEach((x) => sp.append(key, x));
  });

  const filters = useQuery({ queryKey: ['meta-geo-filters'], queryFn: async () => (await api.get<Filters>('/meta-geo/filters')).data });
  const metaAds = useQuery({ queryKey: ['meta-ads'], queryFn: async () => (await api.get<{ ads: MetaAd[] }>('/meta-ads')).data });

  const today = filters.data?.today ?? todayAr();
  const qs = useMemo(() => {
    const sp = new URLSearchParams({ dim: 'ad', from: addDays(today, -(days - 1)), to: today });
    apps.forEach((a) => sp.append('product', a));
    countries.forEach((c) => sp.append('country', c));
    return sp.toString();
  }, [today, days, apps.join(','), countries.join(',')]); // eslint-disable-line react-hooks/exhaustive-deps

  const table = useQuery({
    queryKey: ['meta-geo-table', qs],
    queryFn: async () => (await api.get<TableData>(`/meta-geo/table?${qs}`)).data,
    placeholderData: keepPreviousData,
    enabled: !!filters.data,
  });

  const adsById = useMemo(() => new Map((metaAds.data?.ads ?? []).map((a) => [a.adId, a])), [metaAds.data]);
  const rows: Row[] = useMemo(() => {
    const list = (table.data?.rows ?? []).map((r) => ({ adId: r.keys[0].id, name: r.keys[0].label, ad: adsById.get(r.keys[0].id), m: r.m }));
    const shown = onlyRunning ? list.filter((r) => r.ad && RUNNING.has(r.ad.status)) : list;
    const val = (r: Row) => r.m[sortKey];
    return [...shown].sort((a, b) => {
      const va = val(a), vb = val(b);
      if (va == null && vb == null) return 0;
      if (va == null) return 1; // sin dato siempre al final
      if (vb == null) return -1;
      return sortDir === 'asc' ? va - vb : vb - va;
    });
  }, [table.data, adsById, onlyRunning, sortKey, sortDir]);

  const thumbs = useQuery({
    queryKey: ['meta-ads-media', rows.map((r) => r.adId).sort().join(',')],
    queryFn: async () => {
      const sp = new URLSearchParams();
      rows.forEach((r) => sp.append('adId', r.adId));
      return (await api.get<Record<string, string>>(`/meta-ads/media?${sp}`)).data;
    },
    enabled: rows.length > 0,
    staleTime: 30 * 60 * 1000,
  });

  const totals = useMemo(() => {
    const t = rows.reduce((a, r) => ({
      spend: a.spend + r.m.spend, impressions: a.impressions + r.m.impressions, clicks: a.clicks + r.m.clicks,
      video: a.video + r.m.videoViews3s, conversations: a.conversations + r.m.conversations, leads: a.leads + r.m.leads,
      qualified: a.qualified + r.m.leadsQualified, closed: a.closed + r.m.leadsClosed,
    }), { spend: 0, impressions: 0, clicks: 0, video: 0, conversations: 0, leads: 0, qualified: 0, closed: 0 });
    return {
      ...t,
      hookRate: t.impressions ? (100 * t.video) / t.impressions : null,
      ctr: t.impressions ? (100 * t.clicks) / t.impressions : null,
      costPerConversation: t.conversations ? t.spend / t.conversations : null,
      costPerLead: t.leads ? t.spend / t.leads : null,
    };
  }, [rows]);

  const setSort = (k: ColKey) => update((sp) => {
    const dir = sortKey === k ? (sortDir === 'desc' ? 'asc' : 'desc') : (COLS.find((c) => c.key === k)?.rank === 'low' ? 'asc' : 'desc');
    sp.set(P.sort, `${k}:${dir}`);
  });
  const productName = (k: string | null | undefined) => (k ? filters.data?.products.find((p) => p.key === k)?.displayName ?? k : null);

  if (filters.isLoading) return <div className="text-slate-500">Cargando…</div>;
  const f = filters.data;

  return (
    <div className="space-y-4">
      <div className="text-sm text-slate-600">
        Cada anuncio con su video y cómo rinde. Tocá la miniatura para ver el video. Enganche, CTR, $ / chat y $ / lead se pintan
        contra referencias del mercado (solo los anuncios con más de {num(MIN_IMPRESSIONS)} impresiones; pasá el mouse para ver la referencia).
      </div>
      {f && !f.configured && <div className="card p-3 text-sm text-red-700">Falta el token de Meta (Meta:AdsAccessToken) en el servidor.</div>}

      <div className="card p-3 space-y-3">
        <div className="flex flex-wrap items-center gap-1.5">
          <span className="text-xs font-medium text-slate-500 w-16">Fechas</span>
          {RANGES.map((r) => (
            <Chip key={r.days} active={days === r.days} onClick={() => update((sp) => sp.set(P.days, String(r.days)))}>{r.label}</Chip>
          ))}
        </div>
        <div className="flex flex-wrap items-center gap-1.5">
          <span className="text-xs font-medium text-slate-500 w-16">App</span>
          {(f?.products ?? []).map((p) => (
            <Chip key={p.key} active={apps.includes(p.key)} onClick={() => toggleIn(P.app, apps, p.key)}>{p.displayName}</Chip>
          ))}
        </div>
        {(f?.countries ?? []).length > 1 && (
          <div className="flex flex-wrap items-center gap-1.5">
            <span className="text-xs font-medium text-slate-500 w-16">País</span>
            {(f?.countries ?? []).map((c) => (
              <Chip key={c} active={countries.includes(c)} onClick={() => toggleIn(P.country, countries, c)}>{COUNTRY_NAMES[c] ?? c}</Chip>
            ))}
          </div>
        )}
        <div className="flex flex-wrap items-center gap-1.5">
          <span className="text-xs font-medium text-slate-500 w-16">Estado</span>
          <Chip active={onlyRunning} onClick={() => update((sp) => sp.delete(P.all))}>Corriendo ahora</Chip>
          <Chip active={!onlyRunning} onClick={() => update((sp) => sp.set(P.all, '1'))}>Todos con gasto</Chip>
        </div>
      </div>

      <div className="grid grid-cols-2 sm:grid-cols-4 lg:grid-cols-7 gap-2">
        <Kpi label="Anuncios" value={num(rows.length)} />
        <Kpi label="Gasto" value={usd(totals.spend)} />
        <Kpi label="Enganche" value={pct(totals.hookRate)} />
        <Kpi label="CTR" value={pct(totals.ctr)} />
        <Kpi label="Chats" value={num(totals.conversations)} sub={`${usd(totals.costPerConversation)} c/u`} />
        <Kpi label="Leads" value={num(totals.leads)} sub={`${usd(totals.costPerLead)} c/u`} />
        <Kpi label="Cerrados" value={num(totals.closed)} sub={`${num(totals.qualified)} interesados`} />
      </div>

      {table.isError && <div className="text-sm text-red-700">No se pudo cargar el rendimiento.</div>}
      <div className={clsx('card p-0 overflow-x-auto transition-opacity', table.isFetching && 'opacity-60')}>
        {!table.data ? <div className="text-slate-500 text-sm p-4">Cargando…</div> : rows.length === 0 ? (
          <div className="text-sm text-slate-500 py-8 text-center">No hay anuncios con datos para estos filtros.</div>
        ) : (
          <table className="text-sm w-full">
            <thead>
              <tr className="text-left text-slate-500 border-b border-slate-200">
                <th className="py-2 pl-3 pr-2 font-medium">Video</th>
                <th className="py-2 pr-3 font-medium">Anuncio</th>
                {COLS.map((c) => (
                  <th key={c.key} title={c.hint} className="py-2 px-2 font-medium text-right whitespace-nowrap">
                    <button type="button" onClick={() => setSort(c.key)} className={clsx('hover:text-slate-800', sortKey === c.key && 'text-slate-900')}>
                      {c.label}{sortKey === c.key ? (sortDir === 'desc' ? ' ↓' : ' ↑') : ''}
                    </button>
                  </th>
                ))}
              </tr>
            </thead>
            <tbody>
              {rows.map((r) => {
                const thumb = thumbs.data?.[r.adId];
                const sub = [productName(r.ad?.productKey), r.ad?.rubro?.replace(/-/g, ' ')].filter(Boolean).join(' · ');
                return (
                  <tr key={r.adId} className="border-t border-slate-100 align-middle">
                    <td className="py-2 pl-3 pr-2">
                      <button type="button" onClick={() => setPlaying(r)} title="Ver el video"
                        className="relative block w-12 h-[5.25rem] rounded-md overflow-hidden bg-slate-200 group">
                        {thumb && <img src={thumb} alt="" referrerPolicy="no-referrer" className="w-full h-full object-cover" loading="lazy" />}
                        <span className="absolute inset-0 flex items-center justify-center text-white text-lg bg-black/25 opacity-0 group-hover:opacity-100">▶</span>
                      </button>
                    </td>
                    <td className="py-2 pr-3 max-w-[20rem]">
                      <div className="font-medium text-slate-800 truncate" title={r.name}>{r.name}</div>
                      <div className="text-xs text-slate-500 truncate">{sub || '—'}</div>
                      {r.ad && <StatusBadge status={r.ad.status} />}
                    </td>
                    {COLS.map((c) => {
                      const v = r.m[c.key];
                      const lvl = r.m.impressions >= MIN_IMPRESSIONS ? levelOf(c.key, v) : null;
                      return (
                        <td key={c.key} title={lvl ? `${lvl.label} — ${BENCH[c.key]?.ref}` : undefined}
                          className={clsx('py-2 px-2 text-right tabular-nums whitespace-nowrap', lvl?.cls)}>
                          {c.fmt(v)}
                        </td>
                      );
                    })}
                  </tr>
                );
              })}
            </tbody>
          </table>
        )}
      </div>
      <div className="flex flex-wrap items-center gap-1.5 text-[11px] text-slate-500">
        {LEVELS.map((l) => <span key={l.label} className={clsx('px-2 py-0.5 rounded', l.cls)}>{l.label}</span>)}
        <span className="text-slate-400">
          Referencias: enganche media 25-30 % · CTR media 1-1,5 % · $ / chat media USD 1-1,5 · $ / lead media USD 2-3.
        </span>
      </div>
      <div className="text-[11px] text-slate-400">
        Chats = conversaciones que cuenta Meta. Leads = los que llegaron a sales-hub y se reconocieron por el mensaje que deja escrito el anuncio.
      </div>

      {playing && <VideoModal row={playing} productName={productName(playing.ad?.productKey)} onClose={() => setPlaying(null)} />}
    </div>
  );
}

// ── piezas ──────────────────────────────────────────────────────────────────────────────

function VideoModal({ row, productName, onClose }: { row: Row; productName: string | null; onClose: () => void }) {
  const video = useQuery({
    queryKey: ['meta-ad-video', row.adId],
    queryFn: async () => (await api.get<{ url: string }>(`/meta-ads/${row.adId}/video`)).data,
    retry: false,
    staleTime: 60 * 60 * 1000,
  });
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => { if (e.key === 'Escape') onClose(); };
    document.addEventListener('keydown', onKey);
    return () => document.removeEventListener('keydown', onKey);
  }, [onClose]);
  const m = row.m;
  return (
    <div className="fixed inset-0 z-[1200] bg-black/70 flex items-center justify-center p-4" onClick={onClose}>
      <div className="bg-white rounded-xl shadow-2xl max-w-4xl w-full max-h-[92vh] overflow-y-auto flex flex-col md:flex-row" onClick={(e) => e.stopPropagation()}>
        <div className="bg-black md:rounded-l-xl flex items-center justify-center md:w-[22rem] shrink-0">
          {video.isLoading && <div className="text-slate-400 text-sm p-10">Cargando video…</div>}
          {video.isError && <div className="text-slate-300 text-sm p-10 text-center">Este anuncio no tiene video o Meta no lo devolvió.</div>}
          {video.data && (
            <video src={video.data.url} controls autoPlay playsInline className="w-full max-h-[85vh] aspect-[9/16] object-contain" />
          )}
        </div>
        <div className="p-4 space-y-3 flex-1 min-w-0">
          <div className="flex items-start gap-2">
            <div className="min-w-0 flex-1">
              <div className="font-semibold text-slate-900">{row.name}</div>
              <div className="text-xs text-slate-500">{[productName, row.ad?.rubro?.replace(/-/g, ' ')].filter(Boolean).join(' · ')}</div>
              {row.ad && <StatusBadge status={row.ad.status} />}
            </div>
            <button type="button" onClick={onClose} className="text-slate-400 hover:text-slate-700 text-xl leading-none" aria-label="Cerrar">×</button>
          </div>
          <div className="grid grid-cols-2 gap-x-4 gap-y-1 text-sm">
            {COLS.map((c) => (
              <div key={c.key} className="flex justify-between gap-2 border-b border-slate-100 py-0.5" title={c.hint}>
                <span className="text-slate-500">{c.label}</span>
                <span className="tabular-nums font-medium">{c.fmt(m[c.key])}</span>
              </div>
            ))}
          </div>
          {row.ad?.title && <Field label="Título">{row.ad.title}</Field>}
          {row.ad?.body && <Field label="Texto"><span className="whitespace-pre-line">{row.ad.body}</span></Field>}
          {row.ad?.prefillText && <Field label="Mensaje que deja escrito">{row.ad.prefillText}</Field>}
          <div className="text-[11px] text-slate-400">ID {row.adId}</div>
        </div>
      </div>
    </div>
  );
}

function Field({ label, children }: { label: string; children: ReactNode }) {
  return (
    <div>
      <div className="text-[11px] uppercase tracking-wide text-slate-400">{label}</div>
      <div className="text-sm text-slate-700">{children}</div>
    </div>
  );
}

function StatusBadge({ status }: { status: string }) {
  const running = RUNNING.has(status);
  return (
    <span className={clsx('inline-block mt-1 px-1.5 py-0.5 rounded text-[10px] font-medium',
      status === 'ACTIVE' ? 'bg-emerald-100 text-emerald-800' : running ? 'bg-amber-100 text-amber-800'
        : status === 'DISAPPROVED' || status === 'WITH_ISSUES' ? 'bg-red-100 text-red-700' : 'bg-slate-100 text-slate-500')}>
      {STATUS_LABEL[status] ?? status}
    </span>
  );
}

function Chip({ active, onClick, children }: { active: boolean; onClick: () => void; children: ReactNode }) {
  return (
    <button type="button" onClick={onClick}
      className={clsx('px-3 py-1 text-xs font-medium rounded-full whitespace-nowrap',
        active ? 'bg-slate-800 text-white' : 'bg-slate-100 text-slate-600 hover:bg-slate-200')}>
      {children}
    </button>
  );
}

function Kpi({ label, value, sub }: { label: string; value: string; sub?: string }) {
  return (
    <div className="card p-3">
      <div className="text-[11px] uppercase tracking-wide text-slate-400 truncate">{label}</div>
      <div className="text-xl font-bold tabular-nums mt-0.5 truncate">{value}</div>
      {sub && <div className="text-[11px] text-slate-500 mt-0.5 truncate">{sub}</div>}
    </div>
  );
}
