import { useEffect, useMemo, useRef, useState } from 'react';
import { Link } from 'react-router-dom';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import toast from 'react-hot-toast';
import { api } from '../lib/api';
import { LEAD_SOURCE_LABEL, LEAD_STATUS_LABEL, type LeadSource, type LeadStatus } from '../lib/types';
import StatusBadge from '../components/StatusBadge';
import Switch from '../components/Switch';

/* ───────────────────────── datos ───────────────────────── */

interface EntryLine {
  instanceName: string; label: string; phone?: string | null; status?: string | null;
  listenOnly: boolean; sellerLine: boolean; registered: boolean;
}
interface Entry {
  line: string; leadId: string; firstAt: string; day: string; status: LeadStatus;
  productKey: string; source: LeadSource; sellerId?: string | null; name: string; phone?: string | null;
}
interface EntriesResponse {
  from: string; to: string; today: string;
  lines: EntryLine[];
  products: { key: string; name: string }[];
  sellers: { id: string; name: string }[];
  entries: Entry[];
}

type Dim = 'line' | 'status' | 'product' | 'source' | 'seller';
const DIMS: { key: Dim; label: string }[] = [
  { key: 'line', label: 'Teléfono' },
  { key: 'status', label: 'Estado' },
  { key: 'product', label: 'App' },
  { key: 'source', label: 'Origen' },
  { key: 'seller', label: 'Vendedor' },
];

const STATUS_ORDER: LeadStatus[] = [
  'New', 'Assigned', 'Queued', 'Sent', 'Replied', 'Interested', 'DemoScheduled', 'Closed', 'Lost', 'Blocked', 'NoWhatsApp'
];
/** Llegó a interesado o más: la señal de que el lead sirvió. */
const ADVANCED: LeadStatus[] = ['Interested', 'DemoScheduled', 'Closed'];

const NO_SELLER = '__none__';
const OTHERS = '__otros__';

/** Paleta categórica validada (orden fijo: es lo que la hace distinguible con daltonismo). */
const SERIES = ['#2a78d6', '#eb6834', '#1baf7a', '#eda100', '#e87ba4', '#008300', '#4a3aa7', '#e34948'];
const OTHERS_COLOR = '#b4b2ab';
const MAX_SERIES = SERIES.length;

const arToday = () => new Date().toLocaleDateString('en-CA', { timeZone: 'America/Argentina/Buenos_Aires' });
const addDays = (iso: string, n: number) => {
  const d = new Date(`${iso}T12:00:00Z`);
  d.setUTCDate(d.getUTCDate() + n);
  return d.toISOString().slice(0, 10);
};
const WEEKDAY = ['dom', 'lun', 'mar', 'mié', 'jue', 'vie', 'sáb'];
const fmtDay = (iso: string) => `${iso.slice(8, 10)}/${iso.slice(5, 7)}`;
const fmtDayLong = (iso: string) => `${WEEKDAY[new Date(`${iso}T12:00:00Z`).getUTCDay()]} ${fmtDay(iso)}`;
const fmtTime = (iso: string) =>
  new Date(iso).toLocaleString('es-AR', {
    timeZone: 'America/Argentina/Buenos_Aires', day: '2-digit', month: '2-digit', hour: '2-digit', minute: '2-digit'
  });

const PRESETS = [
  { days: 7, label: '7 días' },
  { days: 14, label: '14 días' },
  { days: 30, label: '30 días' },
  { days: 90, label: '90 días' },
];

/* ───────────────────────── página ───────────────────────── */

export default function Entradas() {
  const [to, setTo] = useState(arToday());
  const [from, setFrom] = useState(addDays(arToday(), -29));
  const [filters, setFilters] = useState<Record<Dim, string[]>>({ line: [], status: [], product: [], source: [], seller: [] });
  const [stackBy, setStackBy] = useState<Dim | 'none'>('line');
  const [selectedDay, setSelectedDay] = useState<string | null>(null);
  const [showTable, setShowTable] = useState(false);

  const { data, isLoading, isError } = useQuery({
    queryKey: ['lead-entries', from, to],
    queryFn: async () => (await api.get<EntriesResponse>('/lead-entries', { params: { from, to } })).data,
    refetchInterval: 60_000,
    placeholderData: (prev) => prev,
  });

  const lineByName = useMemo(() => new Map((data?.lines ?? []).map(l => [l.instanceName, l])), [data]);
  const productName = useMemo(() => new Map((data?.products ?? []).map(p => [p.key, p.name])), [data]);
  const sellerName = useMemo(() => new Map((data?.sellers ?? []).map(s => [s.id, s.name])), [data]);

  /** Valor de una entrada en cada dimensión (la clave con la que se filtra y apila). */
  const valueOf = (e: Entry, dim: Dim): string => {
    switch (dim) {
      case 'line': return e.line;
      case 'status': return e.status;
      case 'product': return e.productKey || '';
      case 'source': return e.source;
      case 'seller': return e.sellerId ?? NO_SELLER;
    }
  };
  const labelOf = (dim: Dim, v: string): string => {
    if (v === OTHERS) return 'Otros';
    switch (dim) {
      case 'line': return lineByName.get(v)?.label ?? v;
      case 'status': return LEAD_STATUS_LABEL[v as LeadStatus] ?? v;
      case 'product': return v ? (productName.get(v) ?? v) : 'Sin app';
      case 'source': return LEAD_SOURCE_LABEL[v as LeadSource] ?? v;
      case 'seller': return v === NO_SELLER ? 'Sin vendedor' : (sellerName.get(v) ?? 'Vendedor borrado');
    }
  };
  /** Orden estable de cada dimensión: los colores y el apilado siguen a la entidad, no al ranking. */
  const canonicalOrder = (dim: Dim, values: string[]): string[] => {
    const uniq = [...new Set(values)];
    switch (dim) {
      case 'line': {
        const idx = new Map((data?.lines ?? []).map((l, i) => [l.instanceName, i]));
        return uniq.sort((a, b) => (idx.get(a) ?? 999) - (idx.get(b) ?? 999));
      }
      case 'status':
        return uniq.sort((a, b) => STATUS_ORDER.indexOf(a as LeadStatus) - STATUS_ORDER.indexOf(b as LeadStatus));
      default:
        return uniq.sort((a, b) => labelOf(dim, a).localeCompare(labelOf(dim, b), 'es'));
    }
  };

  const all = data?.entries ?? [];
  const matches = (e: Entry, except?: Dim) =>
    DIMS.every(d => d.key === except || filters[d.key].length === 0 || filters[d.key].includes(valueOf(e, d.key)));
  const filtered = useMemo(() => all.filter(e => matches(e)), [all, filters]);

  // Días del rango, sin huecos: un día sin entradas es un cero, no un día que no existe.
  const days = useMemo(() => {
    if (!data) return [];
    const out: string[] = [];
    for (let d = data.from; d <= data.to; d = addDays(d, 1)) out.push(d);
    return out;
  }, [data]);

  // Series del apilado: se calculan sobre el rango SIN filtros, así filtrar no repinta a nadie.
  const series = useMemo(() => {
    if (stackBy === 'none') return [{ key: 'total', label: 'Leads', color: SERIES[0] }];
    const volume = new Map<string, number>();
    for (const e of all) {
      const v = valueOf(e, stackBy);
      volume.set(v, (volume.get(v) ?? 0) + 1);
    }
    let keys = canonicalOrder(stackBy, [...volume.keys()]);
    let folded = false;
    if (keys.length > MAX_SERIES) {
      const top = new Set([...keys].sort((a, b) => (volume.get(b) ?? 0) - (volume.get(a) ?? 0)).slice(0, MAX_SERIES - 1));
      keys = keys.filter(k => top.has(k));
      folded = true;
    }
    const out = keys.map((k, i) => ({ key: k, label: labelOf(stackBy, k), color: SERIES[i] }));
    if (folded) out.push({ key: OTHERS, label: 'Otros', color: OTHERS_COLOR });
    return out;
  }, [all, stackBy, data]);

  const seriesKeyOf = (e: Entry) => {
    if (stackBy === 'none') return 'total';
    const v = valueOf(e, stackBy);
    return series.some(s => s.key === v) ? v : OTHERS;
  };

  const perDay = useMemo(() => {
    const map = new Map<string, Map<string, number>>();
    for (const d of days) map.set(d, new Map());
    for (const e of filtered) {
      const m = map.get(e.day);
      if (!m) continue;
      const k = seriesKeyOf(e);
      m.set(k, (m.get(k) ?? 0) + 1);
    }
    return map;
  }, [filtered, days, series]);

  const totals = useMemo(() => {
    const t = new Map<string, number>();
    for (const e of filtered) t.set(seriesKeyOf(e), (t.get(seriesKeyOf(e)) ?? 0) + 1);
    return t;
  }, [filtered, series]);

  const dayTotal = (d: string) => [...(perDay.get(d)?.values() ?? [])].reduce((a, b) => a + b, 0);
  const best = days.reduce<{ day: string; n: number } | null>((acc, d) => {
    const n = dayTotal(d);
    return !acc || n > acc.n ? { day: d, n } : acc;
  }, null);
  const advanced = filtered.filter(e => ADVANCED.includes(e.status)).length;

  const activeFilters = DIMS.reduce((n, d) => n + filters[d.key].length, 0);
  const toggleFilter = (dim: Dim, v: string) =>
    setFilters(f => ({ ...f, [dim]: f[dim].includes(v) ? f[dim].filter(x => x !== v) : [...f[dim], v] }));

  const setPreset = (n: number) => {
    const today = data?.today ?? arToday();
    setTo(today);
    setFrom(addDays(today, -(n - 1)));
    setSelectedDay(null);
  };
  const presetActive = (n: number) => to === (data?.today ?? arToday()) && from === addDays(to, -(n - 1));

  const drill = (selectedDay ? filtered.filter(e => e.day === selectedDay) : filtered)
    .slice().sort((a, b) => b.firstAt.localeCompare(a.firstAt));

  return (
    <div className="max-w-6xl mx-auto p-4 space-y-4">
      <div>
        <h1 className="text-xl font-bold">Entradas por teléfono</h1>
        <p className="text-sm text-slate-500">
          Cuántos leads le escribieron a cada teléfono escaneado, por día. Un lead cuenta el día de su primer
          mensaje a ese teléfono (hora de Argentina); si vuelve a escribir no suma de nuevo. El estado es el que
          tiene hoy. Los teléfonos se agregan en <Link to="/devices" className="underline">Dispositivos</Link>.
        </p>
      </div>

      {/* ── Filtros: una sola fila arriba del gráfico ── */}
      <div className="card p-3 flex flex-wrap items-center gap-2">
        <div className="flex rounded-md border border-slate-300 overflow-hidden text-xs">
          {PRESETS.map(p => (
            <button key={p.days}
              className={`px-2.5 py-1.5 ${presetActive(p.days) ? 'bg-slate-800 text-white' : 'bg-white hover:bg-slate-100'}`}
              onClick={() => setPreset(p.days)}>
              {p.label}
            </button>
          ))}
        </div>
        <div className="flex items-center gap-1 text-xs text-slate-600">
          <input type="date" className="border rounded px-1.5 py-1" value={from} max={to}
            onChange={e => { if (e.target.value) { setFrom(e.target.value); setSelectedDay(null); } }} />
          <span>a</span>
          <input type="date" className="border rounded px-1.5 py-1" value={to} min={from}
            onChange={e => { if (e.target.value) { setTo(e.target.value); setSelectedDay(null); } }} />
        </div>
        <span className="w-px h-6 bg-slate-200 mx-1" />
        {DIMS.map(d => (
          <MultiSelect
            key={d.key}
            label={d.label}
            selected={filters[d.key]}
            options={canonicalOrder(d.key, all.map(e => valueOf(e, d.key))).map(v => ({
              value: v,
              label: labelOf(d.key, v),
              // Cuenta cruzada: cuántos quedarían con los OTROS filtros aplicados.
              count: all.filter(e => valueOf(e, d.key) === v && matches(e, d.key)).length,
            }))}
            onToggle={v => toggleFilter(d.key, v)}
            onClear={() => setFilters(f => ({ ...f, [d.key]: [] }))}
          />
        ))}
        {activeFilters > 0 && (
          <button className="text-xs text-slate-500 underline"
            onClick={() => setFilters({ line: [], status: [], product: [], source: [], seller: [] })}>
            Limpiar filtros
          </button>
        )}
      </div>

      {isError && <div className="card p-4 text-sm text-red-600">No se pudieron cargar las entradas.</div>}

      {/* ── KPIs ── */}
      <div className="grid grid-cols-2 md:grid-cols-4 gap-3">
        <Stat label="Leads en el rango" value={filtered.length.toLocaleString('es-AR')}
          hint={data ? `${fmtDay(data.from)} al ${fmtDay(data.to)}` : undefined} />
        <Stat label="Promedio por día" value={days.length ? (filtered.length / days.length).toFixed(1).replace('.', ',') : '—'}
          hint={`${days.length} días`} />
        <Stat label="Mejor día" value={best && best.n > 0 ? String(best.n) : '—'}
          hint={best && best.n > 0 ? fmtDayLong(best.day) : 'sin entradas'} />
        <Stat label="Llegaron a interesado o más" value={filtered.length ? `${Math.round((advanced / filtered.length) * 100)}%` : '—'}
          hint={`${advanced} de ${filtered.length}`} />
      </div>

      {/* ── Gráfico ── */}
      <div className="card p-4">
        <div className="flex flex-wrap items-center justify-between gap-2 mb-3">
          <div>
            <h2 className="font-semibold">Leads que entraron por día</h2>
            <p className="text-xs text-slate-500">Tocá una barra para ver los leads de ese día.</p>
          </div>
          <div className="flex items-center gap-2 text-xs">
            <label className="flex items-center gap-1 text-slate-600">
              Apilar por
              <select className="border rounded px-1.5 py-1" value={stackBy}
                onChange={e => setStackBy(e.target.value as Dim | 'none')}>
                {DIMS.map(d => <option key={d.key} value={d.key}>{d.label}</option>)}
                <option value="none">Nada (solo total)</option>
              </select>
            </label>
            <button className="btn-secondary text-xs px-2 py-1" onClick={() => setShowTable(s => !s)}>
              {showTable ? 'Ver gráfico' : 'Ver tabla'}
            </button>
          </div>
        </div>

        {isLoading && !data ? (
          <div className="h-64 flex items-center justify-center text-sm text-slate-400">Cargando…</div>
        ) : showTable ? (
          <DayTable days={days} series={series} perDay={perDay} />
        ) : (
          <StackedBars
            days={days}
            series={series}
            perDay={perDay}
            today={data?.today}
            selectedDay={selectedDay}
            onSelect={d => setSelectedDay(s => (s === d ? null : d))}
          />
        )}

        {/* Leyenda: siempre presente con 2+ series; tocar filtra por ese valor. */}
        {stackBy !== 'none' && series.length > 0 && (
          <div className="flex flex-wrap gap-x-4 gap-y-1.5 mt-3 text-xs">
            {series.map(s => {
              const on = s.key !== OTHERS && filters[stackBy].includes(s.key);
              return (
                <button key={s.key}
                  disabled={s.key === OTHERS}
                  className={`flex items-center gap-1.5 ${on ? 'font-semibold text-slate-900' : 'text-slate-600'} ${s.key === OTHERS ? 'cursor-default' : 'hover:text-slate-900'}`}
                  title={s.key === OTHERS ? undefined : on ? 'Sacar del filtro' : 'Filtrar por este valor'}
                  onClick={() => s.key !== OTHERS && toggleFilter(stackBy, s.key)}>
                  <span className="w-2.5 h-2.5 rounded-sm shrink-0" style={{ background: s.color }} />
                  {s.label}
                  <span className="tabular-nums text-slate-400">{totals.get(s.key) ?? 0}</span>
                </button>
              );
            })}
          </div>
        )}
      </div>

      {/* ── Resumen por teléfono ── */}
      <LinesSummary lines={data?.lines ?? []} entries={filtered} today={data?.today} />

      {/* ── Detalle ── */}
      <div className="card">
        <div className="px-4 py-3 border-b border-slate-100 flex items-center justify-between gap-2">
          <h2 className="font-semibold">
            {selectedDay ? `Leads del ${fmtDayLong(selectedDay)}` : 'Leads del rango'}
            <span className="text-slate-400 font-normal"> · {drill.length}</span>
          </h2>
          {selectedDay && (
            <button className="text-xs text-slate-500 underline" onClick={() => setSelectedDay(null)}>Ver todo el rango</button>
          )}
        </div>
        <div className="overflow-x-auto">
          <table className="min-w-full text-sm">
            <thead className="bg-slate-50 text-xs uppercase tracking-wide text-slate-500">
              <tr>
                <th className="px-3 py-2 text-left">Primer mensaje</th>
                <th className="px-3 py-2 text-left">Lead</th>
                <th className="px-3 py-2 text-left">Entró por</th>
                <th className="px-3 py-2 text-left">App</th>
                <th className="px-3 py-2 text-left">Estado</th>
                <th className="px-3 py-2 text-left">Origen</th>
                <th className="px-3 py-2 text-left">Vendedor</th>
              </tr>
            </thead>
            <tbody>
              {drill.slice(0, 300).map(e => (
                <tr key={`${e.line}-${e.leadId}`} className="border-t border-slate-100">
                  <td className="px-3 py-2 whitespace-nowrap tabular-nums text-slate-600">{fmtTime(e.firstAt)}</td>
                  <td className="px-3 py-2">
                    <Link to={`/leads/${e.leadId}`} className="font-medium hover:underline">{e.name || 'Sin nombre'}</Link>
                    {e.phone && <div className="text-xs text-slate-400 tabular-nums">+{e.phone.replace(/\D/g, '')}</div>}
                  </td>
                  <td className="px-3 py-2">{labelOf('line', e.line)}</td>
                  <td className="px-3 py-2">{labelOf('product', e.productKey)}</td>
                  <td className="px-3 py-2"><StatusBadge status={e.status} /></td>
                  <td className="px-3 py-2 text-slate-600">{labelOf('source', e.source)}</td>
                  <td className="px-3 py-2 text-slate-600">{labelOf('seller', e.sellerId ?? NO_SELLER)}</td>
                </tr>
              ))}
              {drill.length === 0 && (
                <tr><td colSpan={7} className="px-3 py-6 text-center text-slate-400">Sin leads con estos filtros.</td></tr>
              )}
            </tbody>
          </table>
          {drill.length > 300 && (
            <div className="px-3 py-2 text-xs text-slate-400 border-t border-slate-100">
              Mostrando 300 de {drill.length}. Filtrá o elegí un día para ver el resto.
            </div>
          )}
        </div>
      </div>

      <ReportSettingsCard />
    </div>
  );
}

/* ───────────────────────── piezas ───────────────────────── */

function Stat({ label, value, hint }: { label: string; value: string; hint?: string }) {
  return (
    <div className="card p-3">
      <div className="text-xs text-slate-500">{label}</div>
      <div className="text-2xl font-semibold mt-0.5">{value}</div>
      {hint && <div className="text-xs text-slate-400 mt-0.5">{hint}</div>}
    </div>
  );
}

function MultiSelect({ label, options, selected, onToggle, onClear }: {
  label: string;
  options: { value: string; label: string; count: number }[];
  selected: string[];
  onToggle: (v: string) => void;
  onClear: () => void;
}) {
  const [open, setOpen] = useState(false);
  const ref = useRef<HTMLDivElement>(null);
  useEffect(() => {
    if (!open) return;
    const close = (ev: MouseEvent) => { if (ref.current && !ref.current.contains(ev.target as Node)) setOpen(false); };
    document.addEventListener('mousedown', close);
    return () => document.removeEventListener('mousedown', close);
  }, [open]);

  const summary = selected.length === 0 ? 'Todos'
    : selected.length === 1 ? (options.find(o => o.value === selected[0])?.label ?? '1')
    : `${selected.length}`;

  return (
    <div className="relative" ref={ref}>
      <button
        className={`text-xs border rounded-md px-2.5 py-1.5 flex items-center gap-1 max-w-[220px] ${selected.length ? 'border-slate-800 bg-slate-800 text-white' : 'border-slate-300 bg-white hover:bg-slate-100'}`}
        onClick={() => setOpen(o => !o)}>
        <span className={selected.length ? '' : 'text-slate-500'}>{label}:</span>
        <span className="truncate">{summary}</span>
        <span className="opacity-60">▾</span>
      </button>
      {open && (
        <div className="absolute z-30 mt-1 w-64 max-h-80 overflow-y-auto bg-white border border-slate-200 rounded-md shadow-lg py-1">
          <button className="w-full text-left px-3 py-1.5 text-xs hover:bg-slate-50 flex justify-between"
            onClick={onClear}>
            <span className={selected.length === 0 ? 'font-semibold' : ''}>Todos</span>
          </button>
          {options.length === 0 && <div className="px-3 py-2 text-xs text-slate-400">Sin valores en el rango</div>}
          {options.map(o => (
            <label key={o.value} className="px-3 py-1.5 text-xs hover:bg-slate-50 flex items-center gap-2 cursor-pointer">
              <input type="checkbox" checked={selected.includes(o.value)} onChange={() => onToggle(o.value)} />
              <span className="flex-1 truncate">{o.label}</span>
              <span className="tabular-nums text-slate-400">{o.count}</span>
            </label>
          ))}
        </div>
      )}
    </div>
  );
}

type Series = { key: string; label: string; color: string };

/** Escala del eje Y en números redondos (0, 5, 10… / 0, 20, 40…). */
function niceScale(max: number): { top: number; step: number } {
  if (max <= 0) return { top: 4, step: 1 };
  const raw = max / 4;
  const pow = Math.pow(10, Math.floor(Math.log10(raw)));
  const step = [1, 2, 5, 10].map(m => m * pow).find(s => s >= raw) ?? raw;
  const s = Math.max(1, Math.round(step));
  return { top: Math.ceil(max / s) * s, step: s };
}

function StackedBars({ days, series, perDay, today, selectedDay, onSelect }: {
  days: string[];
  series: Series[];
  perDay: Map<string, Map<string, number>>;
  today?: string;
  selectedDay: string | null;
  onSelect: (day: string) => void;
}) {
  const wrap = useRef<HTMLDivElement>(null);
  const [width, setWidth] = useState(800);
  const [hover, setHover] = useState<{ day: string; x: number } | null>(null);

  useEffect(() => {
    if (!wrap.current) return;
    const ro = new ResizeObserver(([e]) => setWidth(Math.max(320, e.contentRect.width)));
    ro.observe(wrap.current);
    return () => ro.disconnect();
  }, []);

  const H = 260, padL = 34, padR = 8, padT = 18, padB = 26;
  const plotW = width - padL - padR, plotH = H - padT - padB;
  const totalOf = (d: string) => [...(perDay.get(d)?.values() ?? [])].reduce((a, b) => a + b, 0);
  const max = Math.max(0, ...days.map(totalOf));
  const { top, step } = niceScale(max);
  const band = days.length ? plotW / days.length : plotW;
  const barW = Math.max(2, Math.min(24, band * 0.72));
  const y = (v: number) => padT + plotH - (v / top) * plotH;
  const labelEvery = Math.max(1, Math.ceil(days.length / Math.max(2, Math.floor(plotW / 56))));
  const maxDay = max > 0 ? days.find(d => totalOf(d) === max) : undefined;
  const GAP = 2;

  const ticks: number[] = [];
  for (let v = 0; v <= top; v += step) ticks.push(v);

  const hovered = hover ? perDay.get(hover.day) : undefined;

  return (
    <div ref={wrap} className="relative select-none">
      <svg width={width} height={H} role="img" aria-label="Leads que entraron por día">
        {/* grilla y eje: recesivos */}
        {ticks.map(v => (
          <g key={v}>
            <line x1={padL} x2={width - padR} y1={y(v)} y2={y(v)} stroke={v === 0 ? '#c3c2b7' : '#e8e7e2'} strokeWidth={1} />
            <text x={padL - 6} y={y(v) + 3.5} textAnchor="end" fontSize={10} fill="#898781" className="tabular-nums">{v}</text>
          </g>
        ))}

        {days.map((d, i) => {
          const x0 = padL + i * band;
          const bx = x0 + (band - barW) / 2;
          const m = perDay.get(d);
          const segs = series.map(s => ({ s, n: m?.get(s.key) ?? 0 })).filter(z => z.n > 0);
          let acc = 0;
          const isSel = selectedDay === d;
          return (
            <g key={d}>
              {(isSel || hover?.day === d) && (
                <rect x={x0} y={padT} width={band} height={plotH} fill={isSel ? '#e6effb' : '#f3f3f0'} />
              )}
              {segs.map(({ s, n }, si) => {
                const yTop = y(acc + n);
                const yBot = y(acc);
                acc += n;
                const isTop = si === segs.length - 1;
                // Hueco de 2px del color de la superficie entre segmentos apilados.
                const h = Math.max(1, yBot - yTop - (si > 0 ? GAP : 0));
                const yy = yTop;
                const r = isTop ? Math.min(4, barW / 2, h) : 0;
                return (
                  <path key={s.key} fill={s.color}
                    d={`M${bx},${yy + h} V${yy + r} Q${bx},${yy} ${bx + r},${yy} H${bx + barW - r} Q${bx + barW},${yy} ${bx + barW},${yy + r} V${yy + h} Z`} />
                );
              })}
              {/* etiqueta directa sólo en el pico */}
              {d === maxDay && (
                <text x={x0 + band / 2} y={y(max) - 5} textAnchor="middle" fontSize={10} fontWeight={600} fill="#52514e">{max}</text>
              )}
              {(i % labelEvery === 0 || d === today) && (
                <text x={x0 + band / 2} y={H - 8} textAnchor="middle" fontSize={10}
                  fill={d === today ? '#0b0b0b' : '#898781'} fontWeight={d === today ? 600 : 400}>
                  {d === today ? 'hoy' : fmtDay(d)}
                </text>
              )}
              {/* zona de hover/click: toda la columna, más grande que la barra */}
              <rect x={x0} y={padT} width={band} height={plotH + padB} fill="transparent" className="cursor-pointer"
                onMouseEnter={() => setHover({ day: d, x: x0 + band / 2 })}
                onMouseLeave={() => setHover(h => (h?.day === d ? null : h))}
                onClick={() => onSelect(d)} />
            </g>
          );
        })}
      </svg>

      {hover && (
        <div
          className="absolute z-20 pointer-events-none bg-white border border-slate-200 rounded-md shadow-lg px-3 py-2 text-xs min-w-[160px]"
          style={{
            top: 4,
            left: Math.min(Math.max(0, hover.x - 80), width - 180),
          }}>
          <div className="font-semibold text-slate-800">{fmtDayLong(hover.day)}</div>
          <div className="text-slate-600 mb-1">{totalOf(hover.day)} leads</div>
          {series.length > 1 && series
            .filter(s => (hovered?.get(s.key) ?? 0) > 0)
            .slice().reverse()
            .map(s => (
              <div key={s.key} className="flex items-center gap-1.5 text-slate-600">
                <span className="w-2 h-2 rounded-sm shrink-0" style={{ background: s.color }} />
                <span className="flex-1 truncate">{s.label}</span>
                <span className="tabular-nums text-slate-800">{hovered?.get(s.key)}</span>
              </div>
            ))}
        </div>
      )}
    </div>
  );
}

/** La misma información que el gráfico, en tabla (para leer valores exactos). */
function DayTable({ days, series, perDay }: { days: string[]; series: Series[]; perDay: Map<string, Map<string, number>> }) {
  const rows = days.slice().reverse();
  return (
    <div className="overflow-x-auto max-h-96 overflow-y-auto">
      <table className="min-w-full text-xs">
        <thead className="bg-slate-50 text-slate-500 sticky top-0">
          <tr>
            <th className="px-2 py-1.5 text-left">Día</th>
            {series.length > 1 && series.map(s => <th key={s.key} className="px-2 py-1.5 text-right whitespace-nowrap">{s.label}</th>)}
            <th className="px-2 py-1.5 text-right">Total</th>
          </tr>
        </thead>
        <tbody>
          {rows.map(d => {
            const m = perDay.get(d);
            const total = [...(m?.values() ?? [])].reduce((a, b) => a + b, 0);
            return (
              <tr key={d} className="border-t border-slate-100">
                <td className="px-2 py-1 whitespace-nowrap">{fmtDayLong(d)}</td>
                {series.length > 1 && series.map(s => (
                  <td key={s.key} className="px-2 py-1 text-right tabular-nums text-slate-600">{m?.get(s.key) || ''}</td>
                ))}
                <td className="px-2 py-1 text-right tabular-nums font-medium">{total}</td>
              </tr>
            );
          })}
        </tbody>
      </table>
    </div>
  );
}

function LinesSummary({ lines, entries, today }: { lines: EntryLine[]; entries: Entry[]; today?: string }) {
  const rows = lines
    .map(l => {
      const mine = entries.filter(e => e.line === l.instanceName);
      return {
        l,
        total: mine.length,
        today: today ? mine.filter(e => e.day === today).length : 0,
        advanced: mine.filter(e => ADVANCED.includes(e.status)).length,
        last: mine.reduce<string | null>((acc, e) => (!acc || e.firstAt > acc ? e.firstAt : acc), null),
      };
    })
    // Los teléfonos propios siempre (aunque estén en cero); las líneas de vendedor o borradas, sólo si trajeron algo.
    .filter(r => r.total > 0 || (r.l.registered && !r.l.sellerLine))
    .sort((a, b) => b.total - a.total);
  const max = Math.max(1, ...rows.map(r => r.total));

  return (
    <div className="card overflow-x-auto">
      <div className="px-4 py-3 border-b border-slate-100">
        <h2 className="font-semibold">Por teléfono</h2>
      </div>
      <table className="min-w-full text-sm">
        <thead className="bg-slate-50 text-xs uppercase tracking-wide text-slate-500">
          <tr>
            <th className="px-3 py-2 text-left">Teléfono</th>
            <th className="px-3 py-2 text-left">Conexión</th>
            <th className="px-3 py-2 text-left w-1/3">Leads en el rango</th>
            <th className="px-3 py-2 text-right">Hoy</th>
            <th className="px-3 py-2 text-right">Interesado o más</th>
            <th className="px-3 py-2 text-left">Último lead</th>
          </tr>
        </thead>
        <tbody>
          {rows.map(({ l, total, today: t, advanced, last }) => (
            <tr key={l.instanceName} className="border-t border-slate-100">
              <td className="px-3 py-2">
                <div className="font-medium">{l.label}</div>
                {l.phone && <div className="text-xs text-slate-400 tabular-nums">+{l.phone}</div>}
              </td>
              <td className="px-3 py-2 text-xs">
                {!l.registered ? <span className="text-slate-400">borrada</span>
                  : l.status === 'Connected' ? <span className="text-emerald-700">● conectado</span>
                  : <span className="text-red-600">○ desconectado</span>}
              </td>
              <td className="px-3 py-2">
                <div className="flex items-center gap-2">
                  <div className="flex-1 h-2 rounded-full bg-slate-100 overflow-hidden">
                    <div className="h-full rounded-full bg-[#2a78d6]" style={{ width: `${(total / max) * 100}%` }} />
                  </div>
                  <span className="tabular-nums w-10 text-right">{total}</span>
                </div>
              </td>
              <td className="px-3 py-2 text-right tabular-nums">{t}</td>
              <td className="px-3 py-2 text-right tabular-nums">
                {advanced}{total > 0 && <span className="text-slate-400"> ({Math.round((advanced / total) * 100)}%)</span>}
              </td>
              <td className="px-3 py-2 text-xs text-slate-500 whitespace-nowrap">{last ? fmtTime(last) : '—'}</td>
            </tr>
          ))}
          {rows.length === 0 && (
            <tr><td colSpan={6} className="px-3 py-6 text-center text-slate-400">
              No hay teléfonos cargados. Agregalos en <Link to="/devices" className="underline">Dispositivos</Link>.
            </td></tr>
          )}
        </tbody>
      </table>
    </div>
  );
}

/* ───────────────────────── reporte diario ───────────────────────── */

interface ReportSettings {
  enabled: boolean; recipientPhone: string; sendHour: number; sendInstanceName?: string | null;
  lastReportedDay?: string | null; lastSentAt?: string | null; lastAttemptAt?: string | null; lastError?: string | null;
  lines: { instanceName: string; label: string; phone?: string | null; status: string; listenOnly: boolean }[];
}

function ReportSettingsCard() {
  const qc = useQueryClient();
  const { data } = useQuery({
    queryKey: ['lead-entries-report'],
    queryFn: async () => (await api.get<ReportSettings>('/lead-entries/report')).data,
    refetchInterval: 60_000,
  });
  const [draft, setDraft] = useState<ReportSettings | null>(null);
  const [preview, setPreview] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  useEffect(() => { if (data && !draft) setDraft(data); }, [data]);
  if (!draft || !data) return null;

  const dirty = draft.enabled !== data.enabled || draft.recipientPhone !== data.recipientPhone
    || draft.sendHour !== data.sendHour || (draft.sendInstanceName ?? '') !== (data.sendInstanceName ?? '');

  async function save(next = draft!) {
    setBusy(true);
    try {
      await api.put('/lead-entries/report', {
        enabled: next.enabled, recipientPhone: next.recipientPhone,
        sendHour: next.sendHour, sendInstanceName: next.sendInstanceName || null,
      });
      toast.success('Reporte guardado');
      const fresh = (await api.get<ReportSettings>('/lead-entries/report')).data;
      qc.setQueryData(['lead-entries-report'], fresh);
      setDraft(fresh);
    } catch (e: any) {
      toast.error(e.response?.data?.error ?? 'No se pudo guardar');
    } finally {
      setBusy(false);
    }
  }

  async function showPreview() {
    try {
      const { data: p } = await api.get<{ text: string }>('/lead-entries/report/preview');
      setPreview(p.text);
    } catch {
      toast.error('No se pudo armar el reporte');
    }
  }

  async function sendNow() {
    setBusy(true);
    try {
      const { data: r } = await api.post<{ ok: boolean; via?: string; error?: string }>('/lead-entries/report/send-now');
      if (r.ok) toast.success(`Reporte enviado por ${r.via}`);
      else toast.error(r.error ?? 'No salió', { duration: 8000 });
    } catch {
      toast.error('No se pudo mandar');
    } finally {
      setBusy(false);
    }
  }

  const connected = draft.lines.filter(l => l.status === 'Connected');

  return (
    <div className="card p-4 space-y-3">
      <div className="flex items-start justify-between gap-3">
        <div>
          <h2 className="font-semibold">Reporte diario por WhatsApp</h2>
          <p className="text-xs text-slate-500">
            Todos los días, a la hora elegida, manda cuántos leads entraron ayer a cada teléfono, en qué estado
            están, los últimos 7 días y qué teléfonos están sin conexión.
          </p>
        </div>
        <div className="flex items-center gap-2 shrink-0 text-sm">
          <Switch on={draft.enabled} onClick={() => { const next = { ...draft, enabled: !draft.enabled }; setDraft(next); save(next); }}
            title="Prender o apagar el reporte automático" />
          <span>{draft.enabled ? 'Prendido' : 'Apagado'}</span>
        </div>
      </div>

      <div className="grid md:grid-cols-3 gap-3 text-sm">
        <label className="block">
          <span className="text-xs text-slate-600">Número que lo recibe</span>
          <input className="input mt-1 tabular-nums" value={draft.recipientPhone}
            onChange={e => setDraft({ ...draft, recipientPhone: e.target.value })} placeholder="541169370050" />
        </label>
        <label className="block">
          <span className="text-xs text-slate-600">Hora (Argentina)</span>
          <select className="input mt-1" value={draft.sendHour}
            onChange={e => setDraft({ ...draft, sendHour: Number(e.target.value) })}>
            {Array.from({ length: 24 }, (_, h) => <option key={h} value={h}>{String(h).padStart(2, '0')}:00</option>)}
          </select>
        </label>
        <label className="block">
          <span className="text-xs text-slate-600">Sale por la línea</span>
          <select className="input mt-1" value={draft.sendInstanceName ?? ''}
            onChange={e => setDraft({ ...draft, sendInstanceName: e.target.value || null })}>
            <option value="">Automática (la primera conectada)</option>
            {draft.lines.map(l => (
              <option key={l.instanceName} value={l.instanceName}>
                {l.label}{l.phone ? ` (+${l.phone})` : ''}{l.status === 'Connected' ? '' : ' — desconectada'}
              </option>
            ))}
          </select>
        </label>
      </div>

      <div className="text-xs text-slate-500 space-y-0.5">
        {data.lastSentAt && data.lastReportedDay && (
          <div>Último enviado: {fmtTime(data.lastSentAt)} (reporte del {fmtDayLong(data.lastReportedDay)})</div>
        )}
        {data.lastError && (
          <div className="text-red-600">Último intento{data.lastAttemptAt ? ` (${fmtTime(data.lastAttemptAt)})` : ''} no salió: {data.lastError}</div>
        )}
        {connected.length === 0 && (
          <div className="text-amber-700">
            Hoy no hay ninguna línea conectada: el reporte no puede salir hasta que escanees un teléfono en{' '}
            <Link to="/devices" className="underline">Dispositivos</Link>.
          </div>
        )}
        <div>Si la línea es de solo escucha igual lo manda: es un aviso a tu número, no un mensaje a un lead.</div>
      </div>

      <div className="flex flex-wrap gap-2">
        <button className="btn-primary text-xs" disabled={!dirty || busy} onClick={() => save()}>Guardar</button>
        <button className="btn-secondary text-xs" onClick={showPreview}>Ver cómo queda</button>
        <button className="btn-secondary text-xs" disabled={busy} onClick={sendNow}>Mandar ahora</button>
      </div>

      {preview && (
        <pre className="bg-slate-50 border border-slate-200 rounded p-3 text-xs whitespace-pre-wrap font-sans">{preview}</pre>
      )}
    </div>
  );
}
