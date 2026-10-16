// Mapa de anuncios por provincia (Marketing → Mapa de anuncios): rendimiento de los anuncios de Meta
// por provincia, tipo BI. Los filtros viven en la URL para poder compartir la vista.
// Datos: GET /meta-geo/filters y /meta-geo/summary (ver MetaGeoController).
import { useEffect, useMemo, useRef, useState, type ReactNode } from 'react';
import { useSearchParams } from 'react-router-dom';
import { keepPreviousData, useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { Bar, BarChart, CartesianGrid, ResponsiveContainer, Tooltip, XAxis, YAxis } from 'recharts';
import toast from 'react-hot-toast';
import clsx from 'clsx';
import { api } from '../lib/api';
import AdsProvinceMap, { quantileBreaks, regionKey } from '../components/AdsProvinceMap';

// ── tipos de la API ─────────────────────────────────────────────────────────────────────

type M = {
  spend: number; impressions: number; reach: number; reachExact: boolean; frequency: number | null;
  clicks: number; uniqueClicks: number; linkClicks: number; ctr: number | null; cpc: number | null; cpm: number | null;
  videoViews3s: number; hookRate: number | null;
  conversations: number; costPerConversation: number | null; firstReplies: number; depth2: number; depth3: number;
  leads: number; leadsQualified: number; leadsClosed: number; costPerLead: number | null;
};
type Filters = {
  configured: boolean; lastSyncAt: string | null; minDate: string | null; maxDate: string | null; today: string;
  products: { key: string; displayName: string }[];
  campaigns: { id: string; name: string; productKey: string | null; spend: number; lastDate: string }[];
  ads: { id: string; name: string; productKey: string | null; campaignId: string; format: string; spend: number; lastDate: string }[];
  formats: { key: string; label: string }[];
  regions: { name: string; country: string }[];
};
type RegionRow = { region: string; country: string; inArgentina: boolean; selected: boolean; m: M };
type AdRow = { adId: string; adName: string; campaignId: string | null; campaignName: string | null; productKey: string | null; format: string; m: M };
type Summary = {
  from: string; to: string; days: number; reachSource: 'meta-live' | 'daily-sum'; lastSyncAt: string | null;
  totals: M; spendPerDay: number;
  leadsByStatus: Record<string, number>; leadsUnknownRegion: number; leadsOutsideAr: { country: string; leads: number }[];
  daily: { date: string; spend: number; impressions: number; clicks: number; conversations: number; leads: number }[];
  regions: RegionRow[]; ads: AdRow[];
  formats: { format: string; label: string; ads: number; m: M }[];
};

// ── formato ─────────────────────────────────────────────────────────────────────────────

const usdFmt = new Intl.NumberFormat('es-AR', { style: 'currency', currency: 'USD', minimumFractionDigits: 2, maximumFractionDigits: 2 });
const usd = (n: number | null | undefined) => (n == null ? '—' : usdFmt.format(n));
const num = (n: number | null | undefined) => (n == null ? '—' : Math.round(n).toLocaleString('es-AR'));
const dec = (n: number | null | undefined) => (n == null ? '—' : n.toLocaleString('es-AR', { minimumFractionDigits: 2, maximumFractionDigits: 2 }));
const pct = (n: number | null | undefined) => (n == null ? '—' : `${dec(n)} %`);
const fmtDay = (iso: string) => { const [, m, d] = iso.split('-'); return `${d}/${m}`; };
const fmtSync = (iso: string | null) => iso
  ? new Date(iso).toLocaleString('es-AR', { timeZone: 'America/Argentina/Buenos_Aires', day: '2-digit', month: '2-digit', hour: '2-digit', minute: '2-digit' })
  : 'nunca';

// ── métricas del selector del mapa / tablas ─────────────────────────────────────────────

type MetricKey = 'spend' | 'reach' | 'impressions' | 'ctr' | 'cpc' | 'cpm' | 'hookRate' | 'conversations' | 'costPerConversation' | 'leads' | 'costPerLead';
const METRICS: { key: MetricKey; label: string; fmt: (n: number | null) => string; lowerIsBetter?: boolean }[] = [
  { key: 'spend', label: 'Gasto', fmt: usd },
  { key: 'reach', label: 'Alcance', fmt: num },
  { key: 'impressions', label: 'Impresiones', fmt: num },
  { key: 'ctr', label: 'CTR', fmt: pct },
  { key: 'cpc', label: 'CPC', fmt: usd, lowerIsBetter: true },
  { key: 'cpm', label: 'CPM', fmt: usd, lowerIsBetter: true },
  { key: 'hookRate', label: 'Hook rate', fmt: pct },
  { key: 'conversations', label: 'Conversaciones', fmt: num },
  { key: 'costPerConversation', label: 'Costo por conversación', fmt: usd, lowerIsBetter: true },
  { key: 'leads', label: 'Leads', fmt: num },
  { key: 'costPerLead', label: 'Costo por lead', fmt: usd, lowerIsBetter: true },
];
const metricValue = (m: M, k: MetricKey): number | null => {
  const v = m[k] as number | null;
  // Volumen 0 = "sin datos" en el mapa (gris), no "el más bajo".
  if (v == null) return null;
  if (['spend', 'reach', 'impressions', 'conversations', 'leads'].includes(k) && v === 0) return null;
  return v;
};

// ── fechas ──────────────────────────────────────────────────────────────────────────────

const PRESETS = [
  { key: 'hoy', label: 'Hoy' },
  { key: '7d', label: '7 días' },
  { key: '14d', label: '14 días' },
  { key: '30d', label: '30 días' },
  { key: '90d', label: '90 días' },
  { key: 'mes', label: 'Este mes' },
  { key: 'mes-pasado', label: 'Mes pasado' },
] as const;

const addDays = (iso: string, n: number) => {
  const d = new Date(`${iso}T12:00:00Z`);
  d.setUTCDate(d.getUTCDate() + n);
  return d.toISOString().slice(0, 10);
};
function presetRange(key: string, today: string): [string, string] {
  const [y, m] = today.split('-').map(Number);
  switch (key) {
    case 'hoy': return [today, today];
    case '7d': return [addDays(today, -6), today];
    case '14d': return [addDays(today, -13), today];
    case '90d': return [addDays(today, -89), today];
    case 'mes': return [`${today.slice(0, 7)}-01`, today];
    case 'mes-pasado': {
      const first = new Date(Date.UTC(m === 1 ? y - 1 : y, m === 1 ? 11 : m - 2, 1)).toISOString().slice(0, 10);
      const last = addDays(`${today.slice(0, 7)}-01`, -1);
      return [first, last];
    }
    default: return [addDays(today, -29), today];
  }
}
const todayAr = () => new Date().toLocaleDateString('en-CA', { timeZone: 'America/Argentina/Buenos_Aires' });

// Claves de la URL (además de ?view= del hub).
const P = { range: 'rango', from: 'desde', to: 'hasta', app: 'app', campaign: 'campania', ad: 'anuncio', format: 'formato', region: 'provincia', metric: 'metrica' };

export default function MapaAnuncios() {
  const qc = useQueryClient();
  const [params, setParams] = useSearchParams();

  const filters = useQuery({
    queryKey: ['meta-geo-filters'],
    queryFn: async () => (await api.get<Filters>('/meta-geo/filters')).data,
  });

  const today = filters.data?.today ?? todayAr();
  const preset = params.get(P.range) ?? (params.get(P.from) ? 'custom' : '30d');
  const [from, to] = preset === 'custom'
    ? [params.get(P.from) ?? addDays(today, -29), params.get(P.to) ?? today]
    : presetRange(preset, today);
  const apps = params.getAll(P.app);
  const campaigns = params.getAll(P.campaign);
  const ads = params.getAll(P.ad);
  const formats = params.getAll(P.format);
  const regions = params.getAll(P.region);
  const metric = (METRICS.find((m) => m.key === params.get(P.metric))?.key ?? 'spend') as MetricKey;
  const metricDef = METRICS.find((m) => m.key === metric)!;
  const anyFilter = apps.length + campaigns.length + ads.length + formats.length + regions.length > 0 || preset !== '30d';

  /** Cambia un parámetro de la URL sin tocar el resto (ni la tab del hub). */
  function update(mut: (sp: URLSearchParams) => void) {
    const sp = new URLSearchParams(params);
    mut(sp);
    setParams(sp, { replace: true });
  }
  const setList = (key: string, values: string[]) => update((sp) => { sp.delete(key); values.forEach((v) => sp.append(key, v)); });
  const toggleIn = (key: string, list: string[], v: string) => setList(key, list.includes(v) ? list.filter((x) => x !== v) : [...list, v]);
  const setPreset = (k: string) => update((sp) => { sp.delete(P.from); sp.delete(P.to); if (k === '30d') sp.delete(P.range); else sp.set(P.range, k); });
  const setCustom = (f: string, t: string) => update((sp) => { sp.set(P.range, 'custom'); sp.set(P.from, f); sp.set(P.to, t); });
  const clearAll = () => update((sp) => { Object.values(P).filter((k) => k !== P.metric).forEach((k) => sp.delete(k)); });
  const toggleRegion = (name: string) => {
    const k = regionKey(name);
    const has = regions.some((r) => regionKey(r) === k);
    setList(P.region, has ? regions.filter((r) => regionKey(r) !== k) : [...regions, name]);
  };

  const qs = useMemo(() => {
    const sp = new URLSearchParams({ from, to });
    apps.forEach((v) => sp.append('product', v));
    campaigns.forEach((v) => sp.append('campaignId', v));
    ads.forEach((v) => sp.append('adId', v));
    formats.forEach((v) => sp.append('format', v));
    regions.forEach((v) => sp.append('region', v));
    return sp.toString();
  }, [from, to, apps.join(), campaigns.join(), ads.join(), formats.join(), regions.join()]); // eslint-disable-line react-hooks/exhaustive-deps

  const summary = useQuery({
    queryKey: ['meta-geo-summary', qs],
    queryFn: async () => (await api.get<Summary>(`/meta-geo/summary?${qs}`)).data,
    placeholderData: keepPreviousData,
  });

  const sync = useMutation({
    mutationFn: async () => (await api.post<{ rows: number; accounts: number }>('/meta-geo/sync')).data,
    onSuccess: (r) => {
      toast.success(`${r.rows.toLocaleString('es-AR')} filas sincronizadas de ${r.accounts} cuentas`);
      qc.invalidateQueries({ queryKey: ['meta-geo-filters'] });
      qc.invalidateQueries({ queryKey: ['meta-geo-summary'] });
    },
    onError: (e: any) => toast.error(e?.response?.data?.error ?? 'No se pudo sincronizar'),
  });

  const f = filters.data;
  const s = summary.data;
  const productName = (k: string | null) => (k ? f?.products.find((p) => p.key === k)?.displayName ?? k : 'sin app');
  const formatLabel = (k: string) => f?.formats.find((x) => x.key === k)?.label ?? k;

  // Opciones dependientes: campañas de las apps elegidas, anuncios de esas apps/campañas.
  const campaignOptions = (f?.campaigns ?? []).filter((c) => apps.length === 0 || (c.productKey && apps.includes(c.productKey)));
  const adOptions = (f?.ads ?? []).filter((a) =>
    (apps.length === 0 || (a.productKey && apps.includes(a.productKey))) &&
    (campaigns.length === 0 || campaigns.includes(a.campaignId)) &&
    (formats.length === 0 || formats.includes(a.format)));

  // ── mapa ──
  const arRows = (s?.regions ?? []).filter((r) => r.inArgentina);
  const outside = (s?.regions ?? []).filter((r) => !r.inArgentina);
  const byKey = useMemo(() => new Map(arRows.map((r) => [regionKey(r.region), r])), [s]); // eslint-disable-line react-hooks/exhaustive-deps
  const legend = useMemo(
    () => quantileBreaks(arRows.map((r) => metricValue(r.m, metric)).filter((v): v is number => v != null)),
    [s, metric], // eslint-disable-line react-hooks/exhaustive-deps
  );
  const selectedKeys = new Set(regions.map(regionKey));
  const reachApprox = s?.reachSource !== 'meta-live';
  const tooltipOf = (name: string) => {
    const r = byKey.get(regionKey(name));
    const isCaba = regionKey(name) === regionKey('Ciudad Autónoma de Buenos Aires');
    const head = `<div style="font-weight:600;color:#0f172a;margin-bottom:4px">${name}${isCaba ? ' <span style="font-weight:400;color:#64748b">(leads: cód. 11 = AMBA)</span>' : ''}</div>`;
    if (!r || (r.m.impressions === 0 && r.m.leads === 0)) return `${head}<div style="color:#64748b">Sin datos en el rango.</div>`;
    const m = r.m;
    const line = (k: string, v: string, strong = false) =>
      `<div style="display:flex;justify-content:space-between;gap:12px"><span style="color:#64748b">${k}</span><span style="font-variant-numeric:tabular-nums;${strong ? 'font-weight:600;color:#0f172a' : ''}">${v}</span></div>`;
    return head + [
      line('Gasto', usd(m.spend), metric === 'spend'),
      line(`Alcance${m.reachExact ? '' : ' (aprox.)'}`, num(m.reach), metric === 'reach'),
      line('Impresiones', num(m.impressions), metric === 'impressions'),
      line('Frecuencia', dec(m.frequency)),
      line('Clicks', num(m.clicks)),
      line('CTR', pct(m.ctr), metric === 'ctr'),
      line('CPC', usd(m.cpc), metric === 'cpc'),
      line('CPM', usd(m.cpm), metric === 'cpm'),
      line('Hook rate', pct(m.hookRate), metric === 'hookRate'),
      line('Conversaciones', num(m.conversations), metric === 'conversations'),
      line('Costo / conv.', usd(m.costPerConversation), metric === 'costPerConversation'),
      line('Leads', num(m.leads), metric === 'leads'),
      line('Costo / lead', usd(m.costPerLead), metric === 'costPerLead'),
    ].join('') + `<div style="color:#94a3b8;margin-top:4px;font-size:11px">Click: ${r.selected ? 'quitar del' : 'agregar al'} filtro</div>`;
  };

  // ── gráfico diario ──
  const [series, setSeries] = useState<'spend' | 'conversations' | 'leads'>('spend');

  if (filters.isLoading) return <div className="text-slate-500">Cargando…</div>;

  return (
    <div className="space-y-4">
      {/* Encabezado */}
      <div className="flex flex-wrap items-center gap-3">
        <div className="text-sm text-slate-600 flex-1 min-w-[16rem]">
          Rendimiento de los anuncios de Meta por provincia. Se sincroniza solo cada 6 h (últimos 3 días).
          {' '}Última: {fmtSync(f?.lastSyncAt ?? null)}.
          {f?.minDate && <> Datos desde {fmtDay(f.minDate)}/{f.minDate.slice(0, 4)}.</>}
        </div>
        <button className="btn-secondary" disabled={sync.isPending || !f?.configured} onClick={() => sync.mutate()}>
          {sync.isPending ? 'Sincronizando…' : 'Sincronizar ahora'}
        </button>
      </div>
      {f && !f.configured && <div className="card p-3 text-sm text-red-700">Falta el token de Meta (Meta:AdsAccessToken) en el servidor.</div>}

      {/* Filtros rápidos */}
      <div className="card p-3 space-y-3">
        <div className="flex flex-wrap items-center gap-1.5">
          <span className="text-xs font-medium text-slate-500 w-16">Fechas</span>
          {PRESETS.map((p) => (
            <Chip key={p.key} active={preset === p.key} onClick={() => setPreset(p.key)}>{p.label}</Chip>
          ))}
          <div className="flex items-center gap-1 ml-1">
            <input type="date" className="input !py-1 !px-2 !w-auto text-xs" value={from} max={to}
              onChange={(e) => e.target.value && setCustom(e.target.value, to)} />
            <span className="text-slate-400 text-xs">a</span>
            <input type="date" className="input !py-1 !px-2 !w-auto text-xs" value={to} min={from} max={today}
              onChange={(e) => e.target.value && setCustom(from, e.target.value)} />
          </div>
        </div>
        <div className="flex flex-wrap items-center gap-1.5">
          <span className="text-xs font-medium text-slate-500 w-16">App</span>
          {(f?.products ?? []).map((p) => (
            <Chip key={p.key} active={apps.includes(p.key)} onClick={() => toggleIn(P.app, apps, p.key)}>{p.displayName}</Chip>
          ))}
          {(f?.products ?? []).length === 0 && <span className="text-xs text-slate-400">sin datos todavía</span>}
        </div>
        <div className="flex flex-wrap items-center gap-1.5">
          <span className="text-xs font-medium text-slate-500 w-16">Formato</span>
          {(f?.formats ?? []).map((x) => (
            <Chip key={x.key} active={formats.includes(x.key)} onClick={() => toggleIn(P.format, formats, x.key)}>{x.label}</Chip>
          ))}
        </div>
        <div className="flex flex-wrap items-center gap-2">
          <MultiSelect label="Campaña" selected={campaigns} onChange={(v) => setList(P.campaign, v)}
            options={campaignOptions.map((c) => ({ value: c.id, label: c.name, hint: productName(c.productKey) }))} />
          <MultiSelect label="Anuncio" selected={ads} onChange={(v) => setList(P.ad, v)}
            options={adOptions.map((a) => ({ value: a.id, label: a.name, hint: `${productName(a.productKey)} · ${formatLabel(a.format)}` }))} />
          <MultiSelect label="Provincia" selected={regions} onChange={(v) => setList(P.region, v)}
            options={(f?.regions ?? []).map((r) => ({ value: r.name, label: r.name, hint: r.country !== 'AR' ? r.country : undefined }))} />
          <div className="flex-1" />
          {anyFilter && <button type="button" className="text-sm text-brand-700 hover:underline" onClick={clearAll}>Limpiar filtros</button>}
        </div>
      </div>

      {summary.isLoading && <div className="text-slate-500 text-sm">Cargando…</div>}
      {summary.isError && <div className="card p-3 text-sm text-red-700">No se pudo cargar el resumen.</div>}

      {s && (
        <div className={clsx('space-y-4 transition-opacity', summary.isFetching && 'opacity-60')}>
          {/* KPIs */}
          <div className="grid grid-cols-2 sm:grid-cols-3 lg:grid-cols-4 xl:grid-cols-6 gap-3">
            <Kpi label="Gasto total" value={usd(s.totals.spend)} sub={`${fmtDay(s.from)} al ${fmtDay(s.to)} · ${s.days} d`} />
            <Kpi label="Gasto por día" value={usd(s.spendPerDay)} />
            <Kpi label="Alcance" value={num(s.totals.reach)} sub={s.totals.reachExact ? 'personas únicas (Meta)' : reachApprox ? 'aprox. (suma diaria)' : 'suma de provincias'} />
            <Kpi label="Impresiones" value={num(s.totals.impressions)} />
            <Kpi label="Frecuencia" value={dec(s.totals.frequency)} />
            <Kpi label="CTR" value={pct(s.totals.ctr)} sub={`${num(s.totals.clicks)} clicks`} />
            <Kpi label="CPC" value={usd(s.totals.cpc)} />
            <Kpi label="CPM" value={usd(s.totals.cpm)} />
            <Kpi label="Hook rate" value={pct(s.totals.hookRate)} sub="vistas de 3 s / impresiones" />
            <Kpi label="Conversaciones" value={num(s.totals.conversations)} sub={`${num(s.totals.firstReplies)} con 1ª respuesta`} />
            <Kpi label="Costo por conversación" value={usd(s.totals.costPerConversation)} />
            <Kpi label="Leads sales-hub" value={num(s.totals.leads)} sub={`${usd(s.totals.costPerLead)} por lead · ${num(s.totals.leadsClosed)} cerrados`} />
          </div>

          {/* Mapa + ranking */}
          <div className="grid grid-cols-1 lg:grid-cols-12 gap-4">
            <section className="card p-3 lg:col-span-8 space-y-2">
              <div className="flex flex-wrap items-center gap-2">
                <h2 className="font-semibold">Mapa por provincia</h2>
                <select className="input !w-auto !py-1 text-sm ml-auto" value={metric}
                  onChange={(e) => update((sp) => (e.target.value === 'spend' ? sp.delete(P.metric) : sp.set(P.metric, e.target.value)))}>
                  {METRICS.map((m) => <option key={m.key} value={m.key}>{m.label}</option>)}
                </select>
              </div>
              <AdsProvinceMap
                version={`${qs}|${metric}|${summary.dataUpdatedAt}`}
                valueOf={(name) => { const r = byKey.get(regionKey(name)); return r ? metricValue(r.m, metric) : null; }}
                tooltipOf={tooltipOf}
                selected={selectedKeys}
                onToggle={toggleRegion}
                legend={legend}
                legendTitle={`${metricDef.label}${metric === 'reach' && reachApprox ? ' (aprox.)' : ''}`}
                legendFormat={(n) => metricDef.fmt(n)}
                legendNote={metricDef.lowerIsBetter ? 'Más oscuro = más caro.' : 'Más oscuro = más.'}
              />
              <p className="text-xs text-slate-500">
                Pasá el mouse por una provincia para ver todo; click la agrega o quita del filtro (el mapa sigue mostrando todas,
                el resto de la página se filtra). Conversaciones = conexiones de mensajería que Meta atribuye a la provincia.
                Leads: provincia por el código de área del celular; el 11 (AMBA = CABA + GBA) se cuenta en CABA.
                {s.leadsUnknownRegion > 0 && <> {s.leadsUnknownRegion} lead{s.leadsUnknownRegion === 1 ? '' : 's'} sin provincia identificada.</>}
              </p>
            </section>
            <section className="card p-3 lg:col-span-4 space-y-2">
              <h2 className="font-semibold">Ranking · {metricDef.label}</h2>
              <RankList rows={arRows} metric={metric} fmt={metricDef.fmt} lowerIsBetter={metricDef.lowerIsBetter}
                selected={selectedKeys} onToggle={toggleRegion} />
              {outside.length > 0 && (
                <div className="pt-2 border-t border-slate-100">
                  <div className="text-xs font-medium text-slate-500 mb-1">Fuera de Argentina (no van en el mapa)</div>
                  {outside.map((r) => (
                    <div key={`${r.country}-${r.region}`} className="flex justify-between text-sm py-0.5">
                      <span>{r.region} <span className="text-slate-400">· {r.country}</span></span>
                      <span className="tabular-nums text-slate-600">{usd(r.m.spend)} · {num(r.m.conversations)} conv.</span>
                    </div>
                  ))}
                </div>
              )}
              {s.leadsOutsideAr.length > 0 && (
                <div className="text-xs text-slate-500">
                  Leads con teléfono de afuera: {s.leadsOutsideAr.map((x) => `${x.country} ${x.leads}`).join(', ')}.
                </div>
              )}
            </section>
          </div>

          {/* Serie diaria */}
          <section className="card p-3 space-y-2">
            <div className="flex flex-wrap items-center gap-2">
              <h2 className="font-semibold">Por día</h2>
              <div className="flex gap-1 ml-auto">
                <Chip active={series === 'spend'} onClick={() => setSeries('spend')}>Gasto</Chip>
                <Chip active={series === 'conversations'} onClick={() => setSeries('conversations')}>Conversaciones</Chip>
                <Chip active={series === 'leads'} onClick={() => setSeries('leads')}>Leads</Chip>
              </div>
            </div>
            <div className="h-56">
              <ResponsiveContainer width="100%" height="100%">
                <BarChart data={s.daily.map((d) => ({ ...d, day: fmtDay(d.date) }))} margin={{ top: 4, right: 8, left: 0, bottom: 0 }}>
                  <CartesianGrid stroke="#e7e6e2" vertical={false} />
                  <XAxis dataKey="day" tick={{ fontSize: 11, fill: '#77766f' }} tickLine={false} axisLine={false} minTickGap={16} />
                  <YAxis tick={{ fontSize: 11, fill: '#77766f' }} tickLine={false} axisLine={false} width={48}
                    tickFormatter={(v: number) => (series === 'spend' ? `$${Math.round(v)}` : String(v))} allowDecimals={series === 'spend'} />
                  <Tooltip contentStyle={{ fontSize: 12, borderRadius: 8 }} cursor={{ fill: '#f1f5f9' }}
                    formatter={(v: number) => [series === 'spend' ? usd(v) : num(v), series === 'spend' ? 'Gasto' : series === 'conversations' ? 'Conversaciones' : 'Leads']} />
                  <Bar dataKey={series} fill="#2a78d6" radius={[4, 4, 0, 0]} maxBarSize={28} />
                </BarChart>
              </ResponsiveContainer>
            </div>
          </section>

          {/* Por formato */}
          {s.formats.length > 0 && (
            <section className="card p-3 space-y-2">
              <h2 className="font-semibold">Por formato</h2>
              <SortTable
                rows={s.formats}
                rowKey={(r) => r.format}
                initialSort="spend"
                columns={[
                  { key: 'format', label: 'Formato', value: (r) => r.label, render: (r) => r.label },
                  { key: 'ads', label: 'Anuncios', value: (r) => r.ads, align: 'right' },
                  ...metricColumns<Summary['formats'][number]>(),
                ]}
              />
            </section>
          )}

          {/* Por anuncio */}
          <section className="card p-3 space-y-2">
            <h2 className="font-semibold">Por anuncio <span className="text-slate-400 font-normal text-sm">· {s.ads.length}</span></h2>
            {s.ads.length === 0 ? <Empty /> : (
              <SortTable
                rows={s.ads}
                rowKey={(r) => r.adId}
                initialSort="spend"
                columns={[
                  {
                    key: 'name', label: 'Anuncio', value: (r) => r.adName,
                    render: (r) => (
                      <div className="min-w-[14rem]">
                        <button type="button" className="text-left hover:text-brand-700" title="Filtrar por este anuncio"
                          onClick={() => toggleIn(P.ad, ads, r.adId)}>{r.adName}</button>
                        <div className="text-[11px] text-slate-400 truncate max-w-[22rem]">{r.campaignName ?? '—'}</div>
                      </div>
                    ),
                  },
                  { key: 'app', label: 'App', value: (r) => productName(r.productKey) },
                  { key: 'format', label: 'Formato', value: (r) => formatLabel(r.format) },
                  ...metricColumns<AdRow>({ reachApprox: true }),
                ]}
              />
            )}
          </section>

          {/* Por provincia */}
          <section className="card p-3 space-y-2">
            <h2 className="font-semibold">Por provincia</h2>
            <SortTable
              rows={arRows.filter((r) => r.m.impressions > 0 || r.m.leads > 0)}
              rowKey={(r) => r.region}
              initialSort="spend"
              columns={[
                {
                  key: 'region', label: 'Provincia', value: (r) => r.region,
                  render: (r) => (
                    <button type="button" onClick={() => toggleRegion(r.region)}
                      className={clsx('text-left whitespace-nowrap hover:text-brand-700', r.selected && 'font-semibold text-brand-700')}>
                      {r.region}
                    </button>
                  ),
                },
                ...metricColumns<RegionRow>({ reachApprox: reachApprox }),
              ]}
            />
          </section>
        </div>
      )}
    </div>
  );
}

// ── piezas ──────────────────────────────────────────────────────────────────────────────

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

function Empty() {
  return <div className="text-sm text-slate-500 py-6 text-center">No hay datos para estos filtros.</div>;
}

/** Desplegable multi-selección con búsqueda (campaña, anuncio, provincia). */
function MultiSelect({ label, options, selected, onChange }: {
  label: string;
  options: { value: string; label: string; hint?: string }[];
  selected: string[];
  onChange: (v: string[]) => void;
}) {
  const [open, setOpen] = useState(false);
  const [q, setQ] = useState('');
  const ref = useRef<HTMLDivElement>(null);
  useEffect(() => {
    if (!open) return;
    const close = (e: MouseEvent) => { if (ref.current && !ref.current.contains(e.target as Node)) setOpen(false); };
    document.addEventListener('mousedown', close);
    return () => document.removeEventListener('mousedown', close);
  }, [open]);
  const nq = regionKey(q);
  const shown = options.filter((o) => !nq || regionKey(`${o.label} ${o.hint ?? ''}`).includes(nq));
  const toggle = (v: string) => onChange(selected.includes(v) ? selected.filter((x) => x !== v) : [...selected, v]);

  return (
    <div className="relative" ref={ref}>
      <button type="button" onClick={() => setOpen((o) => !o)}
        className={clsx('px-3 py-1.5 text-sm rounded-md border whitespace-nowrap',
          selected.length ? 'border-brand-500 bg-brand-50 text-brand-800' : 'border-slate-300 bg-white text-slate-700 hover:bg-slate-50')}>
        {label}{selected.length > 0 && ` (${selected.length})`} ▾
      </button>
      {open && (
        <div className="absolute z-[1100] mt-1 w-[min(22rem,calc(100vw-2rem))] card p-2 shadow-lg">
          <input autoFocus className="input !py-1.5 text-sm" placeholder="Buscar…" value={q} onChange={(e) => setQ(e.target.value)} />
          <div className="max-h-64 overflow-y-auto mt-1">
            {shown.length === 0 && <div className="text-xs text-slate-400 p-2">Sin opciones.</div>}
            {shown.map((o) => (
              <label key={o.value} className="flex items-start gap-2 px-1.5 py-1 rounded hover:bg-slate-50 cursor-pointer text-sm">
                <input type="checkbox" className="mt-0.5" checked={selected.includes(o.value)} onChange={() => toggle(o.value)} />
                <span className="min-w-0">
                  <span className="block truncate">{o.label}</span>
                  {o.hint && <span className="block text-[11px] text-slate-400 truncate">{o.hint}</span>}
                </span>
              </label>
            ))}
          </div>
          {selected.length > 0 && (
            <button type="button" className="text-xs text-brand-700 hover:underline mt-1 px-1.5" onClick={() => onChange([])}>Quitar todos</button>
          )}
        </div>
      )}
    </div>
  );
}

function RankList({ rows, metric, fmt, lowerIsBetter, selected, onToggle }: {
  rows: RegionRow[]; metric: MetricKey; fmt: (n: number | null) => string; lowerIsBetter?: boolean;
  selected: Set<string>; onToggle: (r: string) => void;
}) {
  const ranked = rows
    .map((r) => ({ r, v: metricValue(r.m, metric) }))
    .filter((x): x is { r: RegionRow; v: number } => x.v != null)
    .sort((a, b) => (lowerIsBetter ? a.v - b.v : b.v - a.v));
  if (ranked.length === 0) return <Empty />;
  const max = Math.max(...ranked.map((x) => x.v));
  return (
    <div className="space-y-1 max-h-[480px] overflow-y-auto pr-1">
      {ranked.map(({ r, v }) => (
        <button key={r.region} type="button" onClick={() => onToggle(r.region)}
          className={clsx('w-full text-left rounded px-1.5 py-1 hover:bg-slate-50', selected.has(regionKey(r.region)) && 'bg-brand-50')}>
          <div className="flex justify-between text-sm gap-2">
            <span className="truncate">{r.region}</span>
            <span className="tabular-nums text-slate-700">{fmt(v)}</span>
          </div>
          <div className="h-1.5 rounded bg-slate-100 mt-0.5">
            <div className="h-1.5 rounded bg-[#2a78d6]" style={{ width: `${max > 0 ? Math.max(2, (100 * v) / max) : 0}%` }} />
          </div>
        </button>
      ))}
    </div>
  );
}

type Col<T> = { key: string; label: string; value: (r: T) => number | string | null; render?: (r: T) => ReactNode; align?: 'right'; fmt?: (n: number | null) => string };

/** Columnas de métricas comunes a las tablas (todas ordenables). */
function metricColumns<T extends { m: M }>(opts: { reachApprox?: boolean } = {}): Col<T>[] {
  const c = (key: keyof M, label: string, fmt: (n: number | null) => string): Col<T> =>
    ({ key, label, value: (r) => r.m[key] as number | null, align: 'right', fmt });
  return [
    c('spend', 'Gasto', usd),
    c('reach', opts.reachApprox ? 'Alcance*' : 'Alcance', num),
    c('impressions', 'Impr.', num),
    c('frequency', 'Frec.', dec),
    c('clicks', 'Clicks', num),
    c('ctr', 'CTR', pct),
    c('cpc', 'CPC', usd),
    c('cpm', 'CPM', usd),
    c('hookRate', 'Hook', pct),
    c('conversations', 'Conv.', num),
    c('costPerConversation', 'Costo/conv.', usd),
    c('leads', 'Leads', num),
    c('costPerLead', 'Costo/lead', usd),
  ];
}

function SortTable<T>({ rows, columns, rowKey, initialSort }: { rows: T[]; columns: Col<T>[]; rowKey: (r: T) => string; initialSort: string }) {
  const [sort, setSort] = useState<{ key: string; desc: boolean }>({ key: initialSort, desc: true });
  const col = columns.find((c) => c.key === sort.key) ?? columns[0];
  const sorted = [...rows].sort((a, b) => {
    const va = col.value(a), vb = col.value(b);
    if (va == null && vb == null) return 0;
    if (va == null) return 1; // sin dato siempre al final
    if (vb == null) return -1;
    const cmp = typeof va === 'number' && typeof vb === 'number' ? va - vb : String(va).localeCompare(String(vb), 'es');
    return sort.desc ? -cmp : cmp;
  });
  const hasApprox = columns.some((c) => c.label.endsWith('*'));
  if (rows.length === 0) return <Empty />;
  return (
    <div className="overflow-x-auto -mx-3 px-3">
      <table className="w-full text-sm">
        <thead>
          <tr className="text-left text-slate-500 border-b border-slate-200">
            {columns.map((c) => (
              <th key={c.key} className={clsx('py-1.5 pr-3 font-medium whitespace-nowrap select-none cursor-pointer hover:text-slate-800', c.align === 'right' && 'text-right')}
                onClick={() => setSort((s) => ({ key: c.key, desc: s.key === c.key ? !s.desc : c.align === 'right' }))}>
                {c.label}{sort.key === c.key ? (sort.desc ? ' ↓' : ' ↑') : ''}
              </th>
            ))}
          </tr>
        </thead>
        <tbody>
          {sorted.map((r) => (
            <tr key={rowKey(r)} className="border-t border-slate-100 align-top hover:bg-slate-50">
              {columns.map((c) => (
                <td key={c.key} className={clsx('py-1.5 pr-3', c.align === 'right' && 'text-right tabular-nums whitespace-nowrap')}>
                  {c.render ? c.render(r) : c.fmt ? c.fmt(c.value(r) as number | null) : String(c.value(r) ?? '—')}
                </td>
              ))}
            </tr>
          ))}
        </tbody>
      </table>
      {hasApprox && <div className="text-[11px] text-slate-400 mt-1">* Alcance sumado por día (aprox.: una persona puede contarse más de una vez).</div>}
    </div>
  );
}
