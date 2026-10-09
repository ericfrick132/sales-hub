// Tab "Negocio" del Dashboard: MRR, clientes pagos, trials, altas y bajas por app.
// Los datos los reporta cada app (GET {app}/api/hub/tenant-metrics) y sales-hub guarda
// una foto diaria (TenantMetricsWorker) → acá se ve la tendencia.
import { useQuery } from '@tanstack/react-query';
import { Area, AreaChart, CartesianGrid, Legend, ResponsiveContainer, Tooltip, XAxis, YAxis } from 'recharts';
import { api } from '../lib/api';

interface AppBusiness {
  productKey: string;
  connected: boolean;
  lastSyncAt: string | null;
  error: string | null;
  mrrUsd: number;
  active: number;
  trial: number;
  pastDue: number;
  newPaid: number;
  cancelled: number;
  churnPct: number | null;
  trialToPaidPct: number | null;
}
interface Business {
  days: number;
  usdArs: number;
  totals: { mrrUsd: number; active: number; trial: number; newPaid: number; cancelled: number };
  apps: AppBusiness[];
  daily: { date: string; byProduct: Record<string, number> }[];
}

const GRID = '#e7e6e2';
const AXIS = '#77766f';
const usd = (n: number) => `US$ ${Math.round(n).toLocaleString('es-AR')}`;
const fmtDay = (iso: string) => { const [, m, d] = iso.split('-'); return `${d}/${m}`; };
const fmtSync = (iso: string | null) => iso
  ? new Date(iso).toLocaleString('es-AR', { timeZone: 'America/Argentina/Buenos_Aires', day: '2-digit', month: '2-digit', hour: '2-digit', minute: '2-digit' })
  : 'nunca';

export default function BusinessTab({ colorOf, nameOf }: { colorOf: (k: string) => string; nameOf: (k: string) => string }) {
  const { data, isLoading } = useQuery({
    queryKey: ['dashboard-business', 90],
    queryFn: async () => (await api.get<Business>('/dashboard/business', { params: { days: 90 } })).data,
    refetchInterval: 300000,
  });
  if (isLoading || !data) return <div className="text-sm text-slate-500">Cargando…</div>;

  const connected = data.apps.filter((a) => a.connected);
  const keys = connected.map((a) => a.productKey).sort();
  const rows = data.daily.map((d) => ({ date: fmtDay(d.date), ...Object.fromEntries(keys.map((k) => [k, Math.round(d.byProduct[k] ?? 0)])) }));

  return (
    <div className="space-y-4">
      <div className="grid grid-cols-2 md:grid-cols-5 gap-3">
        <Tile label="MRR total" value={usd(data.totals.mrrUsd)} sub={data.usdArs > 0 ? `ARS convertido a ${data.usdArs.toLocaleString('es-AR')}` : undefined} />
        <Tile label="Clientes pagos" value={data.totals.active} />
        <Tile label="En trial" value={data.totals.trial} />
        <Tile label={`Altas pagas · ${data.days}d`} value={data.totals.newPaid} />
        <Tile label={`Bajas · ${data.days}d`} value={data.totals.cancelled} />
      </div>

      <section className="card p-4 md:p-5">
        <h3 className="font-semibold">MRR por app</h3>
        <p className="text-xs text-slate-500 mb-3">foto diaria, en USD</p>
        {rows.length === 0 ? (
          <p className="text-sm text-slate-400">Todavía no hay fotos diarias. Aparecen cuando las apps empiezan a reportar.</p>
        ) : (
          <div className="h-72">
            <ResponsiveContainer width="100%" height="100%">
              <AreaChart data={rows} margin={{ top: 4, right: 8, left: 0, bottom: 0 }}>
                <CartesianGrid vertical={false} stroke={GRID} />
                <XAxis dataKey="date" stroke={AXIS} fontSize={11} tickLine={false} interval="preserveStartEnd" minTickGap={16} />
                <YAxis stroke={AXIS} fontSize={11} tickLine={false} />
                <Tooltip contentStyle={{ fontSize: 12, borderRadius: 8 }} formatter={(v: number) => usd(v)} />
                <Legend wrapperStyle={{ fontSize: 12 }} iconType="circle" iconSize={8} />
                {keys.map((k) => (
                  <Area key={k} type="monotone" dataKey={k} name={nameOf(k)} stackId="m"
                    stroke={colorOf(k)} fill={colorOf(k)} fillOpacity={0.25} strokeWidth={2} isAnimationActive={false} />
                ))}
              </AreaChart>
            </ResponsiveContainer>
          </div>
        )}
      </section>

      <section className="card overflow-x-auto">
        <table className="min-w-full text-sm">
          <thead className="bg-slate-50 text-xs uppercase tracking-wide text-slate-500">
            <tr>
              <th className="px-3 py-2 text-left">App</th>
              <th className="px-3 py-2 text-right">MRR</th>
              <th className="px-3 py-2 text-right">Pagos</th>
              <th className="px-3 py-2 text-right">Trial</th>
              <th className="px-3 py-2 text-right">Pago vencido</th>
              <th className="px-3 py-2 text-right">Altas {data.days}d</th>
              <th className="px-3 py-2 text-right">Bajas {data.days}d</th>
              <th className="px-3 py-2 text-right">Churn</th>
              <th className="px-3 py-2 text-right">Trial → pago</th>
              <th className="px-3 py-2 text-left">Última sync</th>
            </tr>
          </thead>
          <tbody className="divide-y divide-slate-100">
            {data.apps.map((a) => (
              <tr key={a.productKey} className={a.connected ? '' : 'text-slate-400'}>
                <td className="px-3 py-2 font-medium">
                  <span className="inline-block w-2 h-2 rounded-full mr-2 align-middle" style={{ background: colorOf(a.productKey) }} />
                  {nameOf(a.productKey)}
                </td>
                {a.connected ? (
                  <>
                    <td className="px-3 py-2 text-right tabular-nums font-semibold">{usd(a.mrrUsd)}</td>
                    <td className="px-3 py-2 text-right tabular-nums">{a.active}</td>
                    <td className="px-3 py-2 text-right tabular-nums">{a.trial}</td>
                    <td className="px-3 py-2 text-right tabular-nums">{a.pastDue}</td>
                    <td className="px-3 py-2 text-right tabular-nums">{a.newPaid}</td>
                    <td className="px-3 py-2 text-right tabular-nums">{a.cancelled}</td>
                    <td className="px-3 py-2 text-right tabular-nums">{a.churnPct == null ? '—' : `${a.churnPct.toFixed(1)}%`}</td>
                    <td className="px-3 py-2 text-right tabular-nums">{a.trialToPaidPct == null ? '—' : `${a.trialToPaidPct.toFixed(0)}%`}</td>
                  </>
                ) : (
                  <td colSpan={8} className="px-3 py-2 text-xs">{a.error ?? 'sin datos: la app todavía no reporta'}</td>
                )}
                <td className="px-3 py-2 text-xs text-slate-500">{fmtSync(a.lastSyncAt)}</td>
              </tr>
            ))}
          </tbody>
        </table>
      </section>
      <p className="text-xs text-slate-400">
        Churn = bajas del período sobre los pagos al inicio del período. Trial → pago = trials que arrancaron en el período y ya pagan.
      </p>
    </div>
  );
}

function Tile({ label, value, sub }: { label: string; value: number | string; sub?: string }) {
  return (
    <div className="card p-4">
      <div className="text-[11px] uppercase tracking-wide text-slate-400 truncate">{label}</div>
      <div className="text-2xl font-bold tabular-nums mt-1">{typeof value === 'number' ? value.toLocaleString('es-AR') : value}</div>
      {sub && <div className="text-[11px] text-slate-500 mt-0.5 truncate">{sub}</div>}
    </div>
  );
}
