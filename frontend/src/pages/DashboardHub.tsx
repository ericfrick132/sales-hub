// Dashboard único del admin: todos los números y gráficos en un solo lugar.
// Reemplaza al viejo Panel (/admin) y absorbe Atención, Entradas y Audios.
import { useState } from 'react';
import { Link } from 'react-router-dom';
import { useQuery } from '@tanstack/react-query';
import {
  Bar, BarChart, CartesianGrid, Legend, Line, LineChart, ResponsiveContainer, Tooltip, XAxis, YAxis,
} from 'recharts';
import { api } from '../lib/api';
import type { AiCost, GlobalMetrics, Product } from '../lib/types';
import TabbedPage from '../components/TabbedPage';
import Collapsible from '../components/Collapsible';
import TeamCompliance from '../components/TeamCompliance';
import AiCostCard from '../components/AiCostCard';
import Effectiveness from '../components/Effectiveness';
import SendingControl from '../components/SendingControl';
import ControlCenter from '../components/ControlCenter';
import MainCalendar from '../components/MainCalendar';
import TodayChecklist from '../components/TodayChecklist';
import PosteosHoy from '../components/PosteosHoy';
import AdLeadsCard from '../components/AdLeadsCard';
import InstagramFollowCard from '../components/InstagramFollowCard';
import Atencion from './Atencion';
import Entradas from './Entradas';
import AudioAnalytics from './AudioAnalytics';
import BusinessTab from './BusinessTab';

interface TrendDay { date: string; leads: number; byProduct: Record<string, number>; sent: number; replied: number; closed: number }
interface Trend {
  days: number; leads: number; sent: number; replied: number; closed: number;
  byProduct: Record<string, number>; bySource: Record<string, number>; daily: TrendDay[];
}

interface DailyActivity { date: string; total: number; byProduct: Record<string, number> }
interface SellerActivity {
  sellerId: string; total7d: number; todayCount: number; yesterdayCount: number;
  topProducts: Record<string, number>; daily: DailyActivity[];
}

// Paleta categórica validada (skill dataviz, modo claro), en orden fijo: el color sigue
// a la app (orden alfabético de productKey), nunca a su ranking.
const SERIES = ['#2a78d6', '#eb6834', '#1baf7a', '#eda100', '#e87ba4', '#008300', '#4a3aa7', '#e34948'];
const OTHER = '#a3a29c';
const GRID = '#e7e6e2';
const AXIS = '#77766f';

const PERIODS = [
  { days: 7, label: '7 días' },
  { days: 30, label: '30 días' },
  { days: 90, label: '90 días' },
];

const pct = (num: number, den: number) => (den > 0 ? `${Math.round((num / den) * 100)}%` : '—');
const usd = (n: number) => (!n ? '$0' : n < 0.01 ? `$${n.toFixed(4)}` : `$${n.toFixed(2)}`);
const fmtDay = (iso: string) => { const [, m, d] = iso.split('-'); return `${d}/${m}`; };

export default function DashboardHub() {
  const [days, setDays] = useState(30);

  const products = useQuery({ queryKey: ['products-min'], queryFn: async () => (await api.get<Product[]>('/products')).data });
  const trend = useQuery({
    queryKey: ['dashboard-trend', days],
    queryFn: async () => (await api.get<Trend>('/dashboard/trend', { params: { days } })).data,
    refetchInterval: 60000,
  });
  const aiCost = useQuery({ queryKey: ['ai-cost'], queryFn: async () => (await api.get<AiCost>('/dashboard/ai-cost')).data, refetchInterval: 60000 });
  // Comparte queryKey con Atención → react-query dedupe.
  const waiting = useQuery({
    queryKey: ['attention-waiting', false],
    queryFn: async () => (await api.get<{ breached: boolean }[]>('/attention/waiting', { params: { limit: 100 } })).data,
    refetchInterval: 30000,
  });

  // Color estable por app: orden alfabético de productKey.
  const appKeys = [...new Set([
    ...(products.data ?? []).map((p) => p.productKey),
    ...Object.keys(trend.data?.byProduct ?? {}),
  ])].filter((k) => k && k !== 'sin-app').sort();
  const colorOf = (k: string) => {
    const i = appKeys.indexOf(k);
    return i >= 0 && i < SERIES.length ? SERIES[i] : OTHER;
  };
  const nameOf = (k: string) => products.data?.find((p) => p.productKey === k)?.displayName || k;

  const t = trend.data;
  const waitingBreached = (waiting.data ?? []).filter((w) => w.breached).length;

  return (
    <div className="space-y-5">
      <div className="flex items-center justify-between flex-wrap gap-3">
        <h1 className="text-xl md:text-2xl font-bold">Dashboard</h1>
        <div className="flex gap-1">
          {PERIODS.map((p) => (
            <button
              key={p.days}
              type="button"
              onClick={() => setDays(p.days)}
              className={`px-3 py-1 text-xs font-medium rounded-full ${days === p.days ? 'bg-brand-600 text-white' : 'bg-slate-100 text-slate-600 hover:bg-slate-200'}`}>
              {p.label}
            </button>
          ))}
        </div>
      </div>

      {/* Una sola fila de KPIs, sin repetir números en otras partes. */}
      <div className="grid grid-cols-2 md:grid-cols-3 xl:grid-cols-6 gap-3">
        <Kpi label="Leads nuevos" value={t?.leads ?? '—'} sub={`últimos ${days} días`} to="/crm?view=lista" />
        <Kpi label="Contactados" value={t?.sent ?? '—'} sub="primer mensaje enviado" />
        <Kpi label="Respondieron" value={t?.replied ?? '—'} sub={t ? `${pct(t.replied, t.sent)} de los contactados` : ''} tone="sky" />
        <Kpi label="Cuentas creadas" value={t?.closed ?? '—'} sub={t ? `${pct(t.closed, t.sent)} de los contactados` : ''} tone="emerald" />
        <Kpi
          label="Esperando respuesta"
          value={waiting.data?.length ?? 0}
          sub={waitingBreached > 0 ? `${waitingBreached} pasados de 10 min` : 'nadie colgado'}
          to="/admin?view=equipo&sub=atencion"
          tone={waitingBreached > 0 ? 'red' : 'emerald'}
        />
        <Kpi label="Gasto IA" value={usd(aiCost.data?.last30dUsd ?? 0)} sub={`${usd(aiCost.data?.todayUsd ?? 0)} hoy · 30 días`} />
      </div>

      <TabbedPage
        tabs={[
          {
            key: 'ventas', label: 'Ventas', element: (
              <TabbedPage param="sub" tabs={[
                {
                  key: 'resumen', label: 'Resumen', element: (
                    <div className="space-y-4">
                      <ChartCard title="Leads que entraron por día" subtitle="apilado por app">
                        {t && <LeadsByDayChart daily={t.daily} appKeys={appKeys} colorOf={colorOf} nameOf={nameOf} />}
                      </ChartCard>
                      <ChartCard title="Embudo por día" subtitle="contactados, respondieron y cuentas creadas, cada uno en su fecha">
                        {t && <FunnelByDayChart daily={t.daily} />}
                      </ChartCard>
                      <div className="grid grid-cols-1 lg:grid-cols-2 gap-4">
                        <ChartCard title="Leads por app" subtitle={`últimos ${days} días`}>
                          {t && <RankBars data={t.byProduct} label={nameOf} colorOf={colorOf} />}
                        </ChartCard>
                        <ChartCard title="Leads por origen" subtitle={`últimos ${days} días`}>
                          {t && <RankBars data={t.bySource} label={(k) => k} colorOf={() => SERIES[0]} />}
                        </ChartCard>
                      </div>
                      <Collapsible title="Efectividad por app" subtitle="embudo leads → cuentas creadas, con tasas" defaultOpen>
                        <Effectiveness />
                      </Collapsible>
                      <Collapsible title="Leads de anuncios por app" subtitle="Meta Lead Ads + WhatsApp Ads">
                        <AdLeadsCard />
                      </Collapsible>
                    </div>
                  ),
                },
                { key: 'telefonos', label: 'Por teléfono', element: <Entradas /> },
                { key: 'audios', label: 'Audios y estrategias', element: <AudioAnalytics /> },
              ]} />
            ),
          },
          {
            key: 'negocio', label: 'Negocio (MRR)', element: <BusinessTab colorOf={colorOf} nameOf={nameOf} />,
          },
          {
            key: 'equipo', label: 'Equipo', element: (
              <TabbedPage param="sub" tabs={[
                { key: 'vendedores', label: 'Vendedores', element: <SellersPerformance /> },
                { key: 'atencion', label: 'Atención (SLA)', element: <Atencion /> },
              ]} />
            ),
          },
          {
            key: 'operacion', label: 'Operación', element: (
              <div className="space-y-4">
                <Collapsible title="Controles del sistema" subtitle="motores, envío por vendedor y piloto de cada app" defaultOpen>
                  <OperationControls />
                </Collapsible>
                <Collapsible title="Gasto de IA (Claude)" subtitle="costo real por tokens, desglosado por feature" defaultOpen>
                  <AiCostCard />
                </Collapsible>
                <Collapsible title="Posteos y agenda" subtitle="posteos de hoy, tareas y calendario">
                  <PosteosHoy />
                  <TodayChecklist />
                  <MainCalendar />
                </Collapsible>
                <Collapsible title="Auto-follow Instagram" subtitle="salud de las cuentas">
                  <InstagramFollowCard />
                </Collapsible>
              </div>
            ),
          },
        ]}
      />
    </div>
  );
}

// ── Gráficos ────────────────────────────────────────────────────────────────

function ChartCard({ title, subtitle, children }: { title: string; subtitle?: string; children: React.ReactNode }) {
  return (
    <section className="card p-4 md:p-5">
      <div className="mb-3">
        <h3 className="font-semibold">{title}</h3>
        {subtitle && <p className="text-xs text-slate-500">{subtitle}</p>}
      </div>
      {children ?? <div className="h-64 grid place-items-center text-sm text-slate-400">Cargando…</div>}
    </section>
  );
}

const axisProps = { stroke: AXIS, fontSize: 11, tickLine: false, axisLine: { stroke: GRID } } as const;

function LeadsByDayChart({ daily, appKeys, colorOf, nameOf }: {
  daily: TrendDay[]; appKeys: string[]; colorOf: (k: string) => string; nameOf: (k: string) => string;
}) {
  const keys = [...appKeys.filter((k) => daily.some((d) => d.byProduct[k])), ...(daily.some((d) => d.byProduct['sin-app']) ? ['sin-app'] : [])];
  const rows = daily.map((d) => ({ date: fmtDay(d.date), ...Object.fromEntries(keys.map((k) => [k, d.byProduct[k] ?? 0])) }));
  return (
    <div className="h-72">
      <ResponsiveContainer width="100%" height="100%">
        <BarChart data={rows} margin={{ top: 4, right: 8, left: -16, bottom: 0 }}>
          <CartesianGrid vertical={false} stroke={GRID} />
          <XAxis dataKey="date" {...axisProps} interval="preserveStartEnd" minTickGap={16} />
          <YAxis {...axisProps} allowDecimals={false} />
          <Tooltip cursor={{ fill: 'rgba(0,0,0,0.04)' }} contentStyle={{ fontSize: 12, borderRadius: 8 }} />
          <Legend wrapperStyle={{ fontSize: 12 }} iconType="circle" iconSize={8} />
          {keys.map((k) => (
            <Bar key={k} dataKey={k} name={k === 'sin-app' ? 'Sin app' : nameOf(k)} stackId="a"
              fill={k === 'sin-app' ? OTHER : colorOf(k)} stroke="#fff" strokeWidth={1} maxBarSize={28} />
          ))}
        </BarChart>
      </ResponsiveContainer>
    </div>
  );
}

function FunnelByDayChart({ daily }: { daily: TrendDay[] }) {
  const rows = daily.map((d) => ({ date: fmtDay(d.date), Contactados: d.sent, Respondieron: d.replied, 'Cuentas creadas': d.closed }));
  return (
    <div className="h-64">
      <ResponsiveContainer width="100%" height="100%">
        <LineChart data={rows} margin={{ top: 4, right: 8, left: -16, bottom: 0 }}>
          <CartesianGrid vertical={false} stroke={GRID} />
          <XAxis dataKey="date" {...axisProps} interval="preserveStartEnd" minTickGap={16} />
          <YAxis {...axisProps} allowDecimals={false} />
          <Tooltip contentStyle={{ fontSize: 12, borderRadius: 8 }} />
          <Legend wrapperStyle={{ fontSize: 12 }} iconType="circle" iconSize={8} />
          <Line type="monotone" dataKey="Contactados" stroke={SERIES[0]} strokeWidth={2} dot={false} activeDot={{ r: 4 }} />
          <Line type="monotone" dataKey="Respondieron" stroke={SERIES[1]} strokeWidth={2} dot={false} activeDot={{ r: 4 }} />
          <Line type="monotone" dataKey="Cuentas creadas" stroke={SERIES[2]} strokeWidth={2} dot={false} activeDot={{ r: 4 }} />
        </LineChart>
      </ResponsiveContainer>
    </div>
  );
}

/** Barras horizontales ordenadas de mayor a menor, con el valor escrito al lado. */
export function RankBars({ data, label, colorOf, max = 10 }: {
  data: Record<string, number>; label: (k: string) => string; colorOf: (k: string) => string; max?: number;
}) {
  const rows = Object.entries(data).sort((a, b) => b[1] - a[1]).slice(0, max);
  const top = rows[0]?.[1] ?? 0;
  if (rows.length === 0) return <p className="text-sm text-slate-400">Sin datos en el período.</p>;
  return (
    <div className="space-y-1.5">
      {rows.map(([k, v]) => (
        <div key={k} className="flex items-center gap-2 text-sm" title={`${label(k)}: ${v.toLocaleString('es-AR')}`}>
          <span className="w-32 shrink-0 truncate text-slate-600">{k === 'sin-app' ? 'Sin app' : label(k)}</span>
          <div className="flex-1 h-3 rounded bg-slate-100 overflow-hidden">
            <div className="h-full rounded" style={{ width: `${top > 0 ? (v / top) * 100 : 0}%`, background: k === 'sin-app' ? OTHER : colorOf(k) }} />
          </div>
          <span className="w-14 text-right tabular-nums font-medium">{v.toLocaleString('es-AR')}</span>
        </div>
      ))}
    </div>
  );
}

// ── Equipo ──────────────────────────────────────────────────────────────────

/** Una sola tabla por vendedor (antes eran tres) + metas + actividad de 14 días. */
function SellersPerformance() {
  const { data } = useQuery({
    queryKey: ['admin-metrics'],
    queryFn: async () => (await api.get<GlobalMetrics>('/dashboard/admin')).data,
    refetchInterval: 15000,
  });
  const activity = useQuery({
    queryKey: ['sellers-activity', 14],
    queryFn: async () => (await api.get<SellerActivity[]>('/dashboard/sellers/activity', { params: { days: 14 } })).data,
    refetchInterval: 30000,
  });
  if (!data) return <div>Cargando…</div>;

  const dateHeaders: string[] = activity.data?.[0]?.daily.slice(0, 14).map((d) => d.date) ?? [];
  const actById = new Map((activity.data ?? []).map((a) => [a.sellerId, a]));
  const sellers = data.sellers
    .filter((s) => s.displayName && (s.isActive || s.leadsClosed > 0))
    .sort((a, b) => b.leadsClosedThisMonth - a.leadsClosedThisMonth || b.leadsClosed - a.leadsClosed);

  return (
    <div className="space-y-5">
      <TeamCompliance />

      <section>
        <h3 className="text-sm font-semibold mb-1">Rendimiento por vendedor</h3>
        <p className="text-xs text-slate-500 mb-2">
          Cada cuenta creada cuenta para quien originó el lead (si una cold caller lo llevó a demo, es de ella).
        </p>
        <div className="card overflow-x-auto">
          <table className="min-w-full text-sm">
            <thead className="bg-slate-50 text-xs uppercase tracking-wide text-slate-500">
              <tr>
                <th className="px-3 py-2 text-left">Vendedor</th>
                <th className="px-3 py-2 text-left">Envío</th>
                <th className="px-3 py-2 text-right">Hoy / cap</th>
                <th className="px-3 py-2 text-right">Asignados</th>
                <th className="px-3 py-2 text-right">Contactados</th>
                <th className="px-3 py-2 text-right">Resp. %</th>
                <th className="px-3 py-2 text-right">Ganados mes</th>
                <th className="px-3 py-2 text-right">Ganados total</th>
                <th className="px-3 py-2 text-right">Cierre %</th>
              </tr>
            </thead>
            <tbody className="divide-y divide-slate-100">
              {sellers.map((s) => (
                <tr key={s.sellerId}>
                  <td className="px-3 py-2 font-medium">
                    <Link to={`/admin/sellers/${s.sellerId}`} className="text-brand-700 hover:underline">{s.displayName}</Link>
                    {!s.isActive && <span className="ml-1 text-[10px] text-slate-400">inactivo</span>}
                  </td>
                  <td className="px-3 py-2">
                    <SendingControl sellerId={s.sellerId} sendingEnabled={s.sendingEnabled}
                      instanceStatus={s.instanceStatus} compact invalidate={[['admin-metrics']]} />
                  </td>
                  <td className="px-3 py-2 text-right tabular-nums">{s.todaySent} / {s.todayCap}</td>
                  <td className="px-3 py-2 text-right tabular-nums">{s.leadsAssigned}</td>
                  <td className="px-3 py-2 text-right tabular-nums">{s.leadsSent}</td>
                  <td className="px-3 py-2 text-right tabular-nums">{(s.replyRate * 100).toFixed(0)}%</td>
                  <td className="px-3 py-2 text-right font-bold tabular-nums">{s.leadsClosedThisMonth}</td>
                  <td className="px-3 py-2 text-right tabular-nums">{s.leadsClosed}</td>
                  <td className="px-3 py-2 text-right tabular-nums">{(s.closeRate * 100).toFixed(0)}%</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      </section>

      <section>
        <h3 className="text-sm font-semibold mb-1">Leads asignados por día (14 días)</h3>
        <p className="text-xs text-slate-500 mb-2">Hover sobre una celda para ver el desglose por app.</p>
        <div className="card overflow-x-auto">
          <table className="min-w-full text-sm">
            <thead className="bg-slate-50 text-xs uppercase tracking-wide text-slate-500">
              <tr>
                <th className="px-3 py-2 text-left sticky left-0 bg-slate-50">Vendedor</th>
                <th className="px-3 py-2 text-right">Hoy</th>
                <th className="px-3 py-2 text-right">Ayer</th>
                <th className="px-3 py-2 text-right">7d</th>
                {dateHeaders.map((d) => <th key={d} className="px-2 py-2 text-right text-[10px] font-medium">{fmtDay(d)}</th>)}
              </tr>
            </thead>
            <tbody className="divide-y divide-slate-100">
              {sellers.filter((s) => s.isActive).map((s) => {
                const a = actById.get(s.sellerId);
                return (
                  <tr key={s.sellerId} className="hover:bg-slate-50">
                    <td className="px-3 py-2 sticky left-0 bg-white font-medium">{s.displayName}</td>
                    <td className="px-3 py-2 text-right font-bold">{a?.todayCount ?? 0}</td>
                    <td className="px-3 py-2 text-right text-slate-600">{a?.yesterdayCount ?? 0}</td>
                    <td className="px-3 py-2 text-right text-slate-600">{a?.total7d ?? 0}</td>
                    {dateHeaders.map((d) => {
                      const day = a?.daily.find((x) => x.date === d);
                      const v = day?.total ?? 0;
                      const tip = day && v > 0 ? Object.entries(day.byProduct).map(([k, n]) => `${k}: ${n}`).join('\n') : '';
                      return (
                        <td key={d} className="px-2 py-2 text-right" title={tip}>
                          {v === 0 ? <span className="text-slate-300">·</span> : (
                            <span className={`inline-block px-1.5 rounded text-xs font-medium ${
                              v >= 15 ? 'bg-emerald-100 text-emerald-700' : v >= 5 ? 'bg-sky-100 text-sky-700' : 'bg-slate-100 text-slate-600'}`}>{v}</span>
                          )}
                        </td>
                      );
                    })}
                  </tr>
                );
              })}
            </tbody>
          </table>
        </div>
      </section>
    </div>
  );
}

// ── Operación ───────────────────────────────────────────────────────────────

function OperationControls() {
  const { data } = useQuery({
    queryKey: ['admin-metrics'],
    queryFn: async () => (await api.get<GlobalMetrics>('/dashboard/admin')).data,
    refetchInterval: 15000,
  });
  if (!data) return <div>Cargando…</div>;
  return <ControlCenter sellers={data.sellers} />;
}

// ── Piezas ──────────────────────────────────────────────────────────────────

const KPI_TONE: Record<string, string> = {
  slate: 'text-slate-900', sky: 'text-sky-600', emerald: 'text-emerald-600', violet: 'text-violet-600', red: 'text-red-600',
};

export function Kpi({ label, value, sub, to, tone = 'slate' }: { label: string; value: number | string; sub?: string; to?: string; tone?: string }) {
  const inner = (
    <div className="card p-4 h-full hover:border-slate-300 hover:shadow-sm transition">
      <div className="text-[11px] uppercase tracking-wide text-slate-400 truncate">{label}</div>
      <div className={`text-2xl md:text-3xl font-bold tabular-nums mt-1 ${KPI_TONE[tone] ?? KPI_TONE.slate}`}>
        {typeof value === 'number' ? value.toLocaleString('es-AR') : value}
      </div>
      {sub && <div className="text-[11px] text-slate-500 mt-0.5 truncate">{sub}</div>}
    </div>
  );
  return to ? <Link to={to} className="block">{inner}</Link> : inner;
}
