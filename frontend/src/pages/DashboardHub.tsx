// Dashboard único del admin: todos los números y gráficos en un solo lugar.
// Reemplaza al viejo Panel (/admin) y absorbe Atención, Entradas y Audios.
import { useState } from 'react';
import { Link } from 'react-router-dom';
import { useQuery } from '@tanstack/react-query';
import {
  Bar, BarChart, CartesianGrid, Legend, Line, LineChart, ResponsiveContainer, Tooltip, XAxis, YAxis,
} from 'recharts';
import { api } from '../lib/api';
import type { GlobalMetrics, Product } from '../lib/types';
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
import BotCards from '../components/BotCards';
import Atencion from './Atencion';
import Entradas from './Entradas';
import AudioAnalytics from './AudioAnalytics';
import BusinessTab from './BusinessTab';

interface TrendDay {
  date: string; leads: number; byProduct: Record<string, number>; sent: number; replied: number; closed: number;
  adLeads: number; adByProduct: Record<string, number>; adReplied: number;
  /** Conversaciones que cuenta Meta (solo ayer y hoy, en vivo); null si Meta no respondió. */
  metaConversations: number | null; metaByProduct: Record<string, number> | null;
}
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

interface OutboundDay { date: string; whatsapp: number; whatsappReplied: number; instagram: number; instagramReplied: number }
interface OutboundApp {
  productKey: string; displayName: string;
  /** Tope de hoy del canal; null = ese canal no está prendido para la app. */
  whatsappCap: number | null; instagramCap: number | null;
  daily: OutboundDay[];
  /** De dónde salieron y de qué rubro son los leads contactados del período (total y cuántos de esos hoy). */
  sources: OutboundGroup[]; rubros: OutboundGroup[];
}
interface OutboundGroup { label: string; total: number; today: number }
interface OutboundSummary { days: number; apps: OutboundApp[] }

const fmtDay = (iso: string) => { const [, m, d] = iso.split('-'); return `${d}/${m}`; };

export default function DashboardHub() {
  const [days, setDays] = useState(30);

  const products = useQuery({ queryKey: ['products-min'], queryFn: async () => (await api.get<Product[]>('/products')).data });
  const trend = useQuery({
    queryKey: ['dashboard-trend', days],
    queryFn: async () => (await api.get<Trend>('/dashboard/trend', { params: { days } })).data,
    refetchInterval: 60000,
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

      {/* Arriba, solo lo que se mira todos los días: lo que entró por Meta y lo que salió en frío por cada canal.
          El resto (embudo, atención, gasto de IA) vive en sus pestañas. */}
      <div className="grid grid-cols-1 xl:grid-cols-2 gap-3 items-start">
        <MetaLeadsCard daily={t?.daily} colorOf={colorOf} nameOf={nameOf} />
        <OutboundCard colorOf={colorOf} />
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
          { key: 'bot', label: 'Bot: mejoras', element: <BotCards /> },
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

// ── Lo de todos los días: Meta y outbound ───────────────────────────────────

const sum = (xs: number[]) => xs.reduce((a, b) => a + b, 0);
/** "jue 19" a partir de un día ISO (sin pasar por UTC: el día es el de Argentina). */
const dayLabel = (iso: string) => {
  const d = new Date(`${iso}T12:00:00`);
  return `${d.toLocaleDateString('es-AR', { weekday: 'short' }).replace('.', '')} ${d.getDate()}`;
};

/**
 * Leads que entraron por Meta (click-to-WhatsApp + formulario + chats que traen el texto de un anuncio): hoy, ayer
 * y los últimos 7 días (días AR), por app. Abajo, lo que cuenta Meta como conversaciones: incluye a quien ya era
 * lead y volvió a escribir desde un anuncio, y chats sin texto del anuncio ("Hola") que el hub no puede atribuir.
 */
function MetaLeadsCard({ daily, colorOf, nameOf }: { daily?: TrendDay[]; colorOf: (k: string) => string; nameOf: (k: string) => string }) {
  if (!daily?.length) return <section className="card p-4 text-sm text-slate-400">Cargando leads de Meta…</section>;
  const today = daily[daily.length - 1];
  const yesterday = daily[daily.length - 2];
  const week = daily.slice(-7);
  const weekByApp: Record<string, number> = {};
  for (const d of week) for (const [k, n] of Object.entries(d.adByProduct ?? {})) weekByApp[k] = (weekByApp[k] ?? 0) + n;
  const apps = Object.keys(weekByApp).sort((a, b) => weekByApp[b] - weekByApp[a]);
  const cell = (n: number, strong = false) => (
    <td className={`py-1.5 pl-3 text-right tabular-nums ${n === 0 ? 'text-slate-300' : strong ? 'font-bold text-slate-900' : 'text-slate-700'}`}>{n}</td>
  );
  return (
    <section className="card p-4">
      <h2 className="text-[11px] uppercase tracking-wide text-slate-400">Leads de Meta</h2>
      <div className="flex items-baseline gap-2 mt-1 flex-wrap">
        <span className="text-4xl font-bold tabular-nums text-brand-700">{(today.adLeads ?? 0).toLocaleString('es-AR')}</span>
        <span className="text-sm text-slate-600">hoy</span>
        <span className="text-sm text-slate-500">
          {yesterday && <>· ayer <b className="tabular-nums text-slate-700">{yesterday.adLeads ?? 0}</b> </>}
          · {week.length} días <b className="tabular-nums text-slate-700">{sum(week.map((d) => d.adLeads ?? 0))}</b>
        </span>
      </div>
      {apps.length === 0 ? (
        <p className="text-sm text-slate-400 mt-3">No entró ningún lead de anuncios en los últimos {week.length} días.</p>
      ) : (
        <table className="w-full text-sm mt-3">
          <thead>
            <tr className="text-[11px] uppercase tracking-wide text-slate-400 border-b border-slate-100">
              <th className="py-1.5 text-left font-medium">App</th>
              <th className="py-1.5 pl-3 text-right font-medium">Hoy</th>
              <th className="py-1.5 pl-3 text-right font-medium">Ayer</th>
              <th className="py-1.5 pl-3 text-right font-medium">{week.length} días</th>
            </tr>
          </thead>
          <tbody className="divide-y divide-slate-50">
            {apps.map((k) => (
              <tr key={k}>
                <td className="py-1.5">
                  <span className="inline-block w-2 h-2 rounded-full mr-2 align-middle" style={{ background: k === 'sin-app' ? OTHER : colorOf(k) }} />
                  {k === 'sin-app' ? 'Sin app' : nameOf(k)}
                </td>
                {cell(today.adByProduct?.[k] ?? 0, true)}
                {cell(yesterday?.adByProduct?.[k] ?? 0)}
                {cell(weekByApp[k])}
              </tr>
            ))}
          </tbody>
        </table>
      )}
      {today.metaConversations != null && (
        <p className="text-[11px] text-slate-500 mt-2"
          title="Meta también cuenta a quien ya era lead y volvió a escribir desde un anuncio, y los chats que llegan sin el texto del anuncio.">
          Meta cuenta <b className="tabular-nums text-slate-700">{today.metaConversations}</b> conversaciones hoy
          {yesterday?.metaConversations != null && <> · <b className="tabular-nums text-slate-700">{yesterday.metaConversations}</b> ayer</>}
        </p>
      )}
    </section>
  );
}

/**
 * Outbound en frío de cada app por canal: cuántos contactos nuevos salieron hoy por WhatsApp (línea outbound) y
 * cuántos DMs por Instagram (cuenta de la app), contra el tope del día, y lo mismo día por día. "Contestaron" es
 * de los que se mandaron ese día (en WhatsApp no cuenta el saludo automático del negocio).
 */
function OutboundCard({ colorOf }: { colorOf: (k: string) => string }) {
  const { data } = useQuery({
    queryKey: ['dashboard-outbound', 7],
    queryFn: async () => (await api.get<OutboundSummary>('/dashboard/outbound', { params: { days: 7 } })).data,
    refetchInterval: 60000,
  });
  return (
    <section className="card p-4">
      <div className="flex items-baseline justify-between gap-3">
        <h2 className="text-[11px] uppercase tracking-wide text-slate-400">Outbound de hoy · por app y canal</h2>
        <Link to="/leads?view=outbound" className="text-[11px] text-brand-700 hover:underline shrink-0">Línea outbound →</Link>
      </div>
      {!data && <p className="text-sm text-slate-400 mt-3">Cargando outbound…</p>}
      {data && data.apps.length === 0 && (
        <p className="text-sm text-slate-400 mt-3">Ninguna app tiene outbound prendido ni mandó nada en los últimos {data.days} días.</p>
      )}
      <div className="space-y-5">
        {data?.apps.map((app) => <OutboundAppBlock key={app.productKey} app={app} color={colorOf(app.productKey)} />)}
      </div>
    </section>
  );
}

function OutboundAppBlock({ app, color }: { app: OutboundApp; color: string }) {
  const today = app.daily[app.daily.length - 1];
  const channels = [
    { key: 'wa', label: 'WhatsApp', cap: app.whatsappCap, sent: (d: OutboundDay) => d.whatsapp, replied: (d: OutboundDay) => d.whatsappReplied },
    { key: 'ig', label: 'Instagram', cap: app.instagramCap, sent: (d: OutboundDay) => d.instagram, replied: (d: OutboundDay) => d.instagramReplied },
  ];
  return (
    <div className="mt-2">
      <div className="text-sm font-semibold">
        <span className="inline-block w-2 h-2 rounded-full mr-2 align-middle" style={{ background: color }} />
        {app.displayName}
      </div>
      <div className="grid grid-cols-2 gap-3 mt-2">
        {channels.map((c) => {
          const off = c.cap == null;
          const n = today ? c.sent(today) : 0;
          const r = today ? c.replied(today) : 0;
          return (
            <div key={c.key} className="rounded-lg border border-slate-200 px-3 py-2">
              <div className="text-xs text-slate-500">{c.label}</div>
              <div className="flex items-baseline gap-1.5">
                <span className={`text-3xl font-bold tabular-nums ${n > 0 ? 'text-slate-900' : 'text-slate-400'}`}>{n}</span>
                {!off && <span className="text-sm text-slate-500 tabular-nums">de {c.cap}</span>}
              </div>
              <div className="text-[11px] text-slate-500">
                {off && n === 0 ? 'apagado' : r === 1 ? '1 contestó' : `${r} contestaron`}
              </div>
            </div>
          );
        })}
      </div>
      <div className="overflow-x-auto mt-2">
        <table className="w-full text-sm">
          <thead>
            <tr className="text-[11px] text-slate-400 border-b border-slate-100">
              <th className="py-1 text-left font-medium">Últimos {app.daily.length} días</th>
              {app.daily.map((d, i) => (
                <th key={d.date} className={`py-1 pl-2 text-right font-medium whitespace-nowrap ${i === app.daily.length - 1 ? 'text-slate-700' : ''}`}>
                  {i === app.daily.length - 1 ? 'hoy' : dayLabel(d.date)}
                </th>
              ))}
              <th className="py-1 pl-3 text-right font-medium">Total</th>
              <th className="py-1 pl-3 text-right font-medium">Contestaron</th>
            </tr>
          </thead>
          <tbody className="divide-y divide-slate-50">
            {channels.map((c) => (
              <tr key={c.key}>
                <td className="py-1.5 text-slate-600">{c.label}</td>
                {app.daily.map((d, i) => {
                  const n = c.sent(d);
                  const last = i === app.daily.length - 1;
                  return (
                    <td key={d.date} title={`${dayLabel(d.date)}: ${n} enviados · ${c.replied(d)} contestaron`}
                      className={`py-1.5 pl-2 text-right tabular-nums ${n === 0 ? 'text-slate-300' : last ? 'font-bold text-slate-900' : 'text-slate-700'}`}>
                      {n === 0 && !last ? '·' : n}
                    </td>
                  );
                })}
                <td className="py-1.5 pl-3 text-right tabular-nums font-medium">{sum(app.daily.map(c.sent))}</td>
                <td className="py-1.5 pl-3 text-right tabular-nums text-slate-600">{sum(app.daily.map(c.replied))}</td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
      <OutboundGroups title="De dónde salen" groups={app.sources} />
      <OutboundGroups title="Rubros" groups={app.rubros} />
    </div>
  );
}

/** "Rubros: Centro de estética 18 (1 hoy) · Peluquería 7" — de los leads contactados en el período. */
function OutboundGroups({ title, groups }: { title: string; groups?: OutboundGroup[] }) {
  if (!groups?.length) return null;
  return (
    <p className="text-xs text-slate-500 mt-1.5">
      <span className="text-slate-400">{title}: </span>
      {groups.map((g, i) => (
        <span key={g.label}>
          {i > 0 && ' · '}
          <span className="text-slate-700">{g.label}</span> <b className="tabular-nums text-slate-900">{g.total}</b>
          {g.today > 0 && <span className="tabular-nums"> ({g.today} hoy)</span>}
        </span>
      ))}
    </p>
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
              fill={k === 'sin-app' ? OTHER : colorOf(k)} stroke="#fff" strokeWidth={1} maxBarSize={28} isAnimationActive={false} />
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
          <Line type="monotone" dataKey="Contactados" stroke={SERIES[0]} strokeWidth={2} dot={false} activeDot={{ r: 4 }} isAnimationActive={false} />
          <Line type="monotone" dataKey="Respondieron" stroke={SERIES[1]} strokeWidth={2} dot={false} activeDot={{ r: 4 }} isAnimationActive={false} />
          <Line type="monotone" dataKey="Cuentas creadas" stroke={SERIES[2]} strokeWidth={2} dot={false} activeDot={{ r: 4 }} isAnimationActive={false} />
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
