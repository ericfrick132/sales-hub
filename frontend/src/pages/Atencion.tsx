import { useState } from 'react';
import { Link } from 'react-router-dom';
import { useQuery } from '@tanstack/react-query';
import { api } from '../lib/api';

/**
 * Panel de ATENCIÓN: cuánto tardamos en contestarle a un lead y quién está esperando
 * ahora. Es el tablero de control de la persona que maneja WhatsApp.
 */

interface SlaStats {
  turns: number;
  answered: number;
  unanswered: number;
  medianMin: number | null;
  p90Min: number | null;
  avgMin: number | null;
  pctWithinSla: number;
  pctAnsweredWithinSla: number;
}
interface DailyPoint { date: string; turns: number; unanswered: number; medianMin: number | null; pctWithinSla: number }
interface GroupRow { key: string; label: string; stats: SlaStats }
interface HourRow { hour: number; turns: number; medianMin: number | null; pctWithinSla: number }
interface AdProductRow {
  productKey: string; newConversations: number; engaged: number; turns: number;
  avgMin: number | null; medianMin: number | null; pctWithinSla: number;
}
interface WaitingRow {
  leadId: string; leadName: string; phone: string; productKey: string; source: string;
  sellerId: string | null; sellerName: string; waitingSince: string; minutesWaiting: number;
  pendingMessages: number; lastText: string; botMuted: boolean; breached: boolean;
}
interface Summary {
  slaMinutes: number;
  windowDays: number;
  generatedAt: string;
  overall: SlaStats;
  today: SlaStats;
  last7d: SlaStats;
  daily: DailyPoint[];
  byProduct: GroupRow[];
  bySeller: GroupRow[];
  byMode: GroupRow[];
  byHour: HourRow[];
  ads: { newConversations: number; byProduct: AdProductRow[] };
  waitingNow: { total: number; recent: number; backlogOlder: number; recentDays: number; breached: number; oldestMinutes: number };
}

const WINDOWS = [7, 30, 90];

const fmtMin = (m: number | null | undefined) => {
  if (m === null || m === undefined) return '—';
  if (m < 1) return `${Math.round(m * 60)} s`;
  if (m < 60) return `${m.toFixed(1).replace('.', ',')} min`;
  const h = Math.floor(m / 60);
  if (h < 24) return `${h} h ${Math.round(m % 60)} min`;
  return `${Math.round(h / 24)} d`;
};
const fmtWait = (min: number) => (min < 60 ? `${min} min` : min < 1440 ? `${Math.floor(min / 60)} h` : `${Math.floor(min / 1440)} d`);
const fmtDay = (iso: string) => { const [, m, d] = iso.split('-'); return `${d}/${m}`; };
const waLink = (phone: string) => `https://wa.me/${(phone || '').replace(/\D/g, '')}`;

/** Estado del cumplimiento: es una escala de ESTADO (bien/atención/mal), no categórica. */
const tone = (pct: number) =>
  pct >= 90 ? { bar: 'bg-emerald-500', text: 'text-emerald-700', chip: 'bg-emerald-100 text-emerald-800' }
  : pct >= 75 ? { bar: 'bg-amber-500', text: 'text-amber-700', chip: 'bg-amber-100 text-amber-800' }
  : { bar: 'bg-red-500', text: 'text-red-700', chip: 'bg-red-100 text-red-800' };

export default function Atencion() {
  const [days, setDays] = useState(30);
  const [showBacklog, setShowBacklog] = useState(false);

  const summary = useQuery({
    queryKey: ['attention-summary', days],
    queryFn: async () => (await api.get<Summary>('/attention/summary', { params: { days } })).data,
    refetchInterval: 60000,
  });

  const waiting = useQuery({
    queryKey: ['attention-waiting', showBacklog],
    queryFn: async () =>
      (await api.get<WaitingRow[]>('/attention/waiting', {
        params: showBacklog ? { limit: 300, maxAgeHours: 0 } : { limit: 100 },
      })).data,
    refetchInterval: 30000,
  });

  if (summary.isLoading || !summary.data) return <div className="text-slate-500">Cargando…</div>;
  const s = summary.data;
  const sla = s.slaMinutes;
  const queue = waiting.data ?? [];

  return (
    <div className="space-y-5">
      <div className="flex items-baseline justify-between flex-wrap gap-2">
        <div>
          <h1 className="text-xl md:text-2xl font-bold">Atención de chats</h1>
          <p className="text-sm text-slate-500">
            Cuánto tarda en contestar el que atiende WhatsApp. Meta: responder en menos de {sla} minutos.
          </p>
        </div>
        <div className="flex gap-1">
          {WINDOWS.map((d) => (
            <button
              key={d}
              onClick={() => setDays(d)}
              className={`px-3 py-1 rounded text-xs font-medium border ${
                days === d ? 'bg-brand-600 text-white border-brand-600' : 'bg-white text-slate-600 border-slate-200 hover:border-slate-300'
              }`}
            >
              {d} días
            </button>
          ))}
        </div>
      </div>

      {/* ══ Los cuatro números que importan ══ */}
      <div className="grid grid-cols-2 lg:grid-cols-4 gap-3">
        <Tile
          label={`Esperando · ${s.waitingNow.recentDays} días`}
          value={s.waitingNow.recent}
          sub={s.waitingNow.breached > 0 ? `${s.waitingNow.breached} pasados de ${sla} min` : 'ninguno pasado del límite'}
          alert={s.waitingNow.breached > 0}
        />
        <Tile
          label={`Respondidos en ${sla} min · hoy`}
          value={s.today.turns > 0 ? `${s.today.pctWithinSla}%` : '—'}
          sub={s.today.turns > 0 ? `${s.today.turns} mensajes para contestar` : 'todavía no escribió nadie'}
          toneClass={s.today.turns > 0 ? tone(s.today.pctWithinSla).text : undefined}
        />
        <Tile
          label="Tardanza típica · hoy"
          value={fmtMin(s.today.medianMin)}
          sub={`peor 10%: ${fmtMin(s.today.p90Min)}`}
        />
        <Tile
          label={`Sin responder · ${days} días`}
          value={s.overall.unanswered}
          sub={`de ${s.overall.turns} mensajes`}
          alert={s.overall.unanswered > 0}
        />
      </div>

      {/* ══ Cola en vivo ══ */}
      <div className="card p-4 md:p-5">
        <div className="flex items-center justify-between flex-wrap gap-2 mb-3">
          <div>
            <h2 className="text-lg font-semibold">Esperando respuesta</h2>
            <p className="text-xs text-slate-500">
              {showBacklog
                ? 'Toda la deuda, del que más espera al que menos.'
                : `Últimos ${s.waitingNow.recentDays} días. El que más espera, primero.`}
            </p>
          </div>
          <div className="flex items-center gap-3">
            {s.waitingNow.backlogOlder > 0 && (
              <button
                onClick={() => setShowBacklog((v) => !v)}
                className="text-xs text-brand-700 hover:underline"
              >
                {showBacklog
                  ? `Ver solo los últimos ${s.waitingNow.recentDays} días`
                  : `Ver ${s.waitingNow.backlogOlder} chats viejos sin responder`}
              </button>
            )}
            <span className="text-xs text-slate-400">cada 30 s</span>
          </div>
        </div>
        {queue.length === 0 ? (
          <p className="text-sm text-slate-500">
            Nadie esperando en esta ventana.
            {s.waitingNow.backlogOlder > 0 && ` Quedan ${s.waitingNow.backlogOlder} chats viejos sin responder.`}
          </p>
        ) : (
          <div className="overflow-x-auto -mx-4 md:mx-0">
            <table className="min-w-full text-sm">
              <thead className="bg-slate-50 text-xs uppercase tracking-wide text-slate-500">
                <tr>
                  <th className="px-3 py-2 text-left">Espera</th>
                  <th className="px-3 py-2 text-left">Lead</th>
                  <th className="px-3 py-2 text-left">App</th>
                  <th className="px-3 py-2 text-left">Línea</th>
                  <th className="px-3 py-2 text-left">Último mensaje</th>
                  <th className="px-3 py-2"></th>
                </tr>
              </thead>
              <tbody className="divide-y divide-slate-100">
                {queue.map((q) => (
                  <tr key={q.leadId} className={q.breached ? 'bg-red-50/60' : undefined}>
                    <td className="px-3 py-2 whitespace-nowrap">
                      <span className={`badge tabular-nums ${q.breached ? 'bg-red-100 text-red-800' : 'bg-slate-100 text-slate-600'}`}>
                        {fmtWait(q.minutesWaiting)}
                      </span>
                    </td>
                    <td className="px-3 py-2">
                      <Link to={`/leads/${q.leadId}`} className="font-medium text-brand-700 hover:underline">{q.leadName}</Link>
                      {q.pendingMessages > 1 && (
                        <span className="ml-1 text-[11px] text-slate-400">({q.pendingMessages} mensajes)</span>
                      )}
                    </td>
                    <td className="px-3 py-2 text-slate-600">{q.productKey}</td>
                    <td className="px-3 py-2 text-slate-600">
                      {q.sellerName}
                      {q.botMuted && <span className="ml-1 text-[10px] text-amber-600">bot pausado</span>}
                    </td>
                    <td className="px-3 py-2 text-slate-500 max-w-xs truncate" title={q.lastText}>{q.lastText}</td>
                    <td className="px-3 py-2 text-right">
                      <a href={waLink(q.phone)} target="_blank" rel="noreferrer" className="text-xs text-brand-700 hover:underline">
                        Abrir chat
                      </a>
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
      </div>

      {/* ══ Cumplimiento por día ══ */}
      <div className="card p-4 md:p-5">
        <h2 className="text-lg font-semibold mb-1">Cumplimiento por día</h2>
        <p className="text-xs text-slate-500 mb-4">
          Porcentaje de mensajes contestados dentro de los {sla} minutos. Los que nunca se contestaron cuentan como incumplidos.
        </p>
        <DailyStrip daily={s.daily} />
      </div>

      {/* ══ Anuncios ══ */}
      <div className="card p-4 md:p-5">
        <div className="flex items-baseline justify-between mb-1">
          <h2 className="text-lg font-semibold">Conversaciones nuevas de anuncios</h2>
          <span className="text-sm text-slate-500">{s.ads.newConversations} en {days} días</span>
        </div>
        <p className="text-xs text-slate-500 mb-3">
          Leads que entraron por Meta Lead Ads o click-to-WhatsApp, con el tiempo que tardamos en atenderlos.
        </p>
        {s.ads.byProduct.length === 0 ? (
          <p className="text-sm text-slate-500">Sin conversaciones de anuncios en la ventana.</p>
        ) : (
          <Table
            head={['App', 'Nuevas', 'Contestaron', 'Mensajes', 'Promedio', 'Típico', `% en ${sla} min`]}
            rows={s.ads.byProduct.map((r) => [
              r.productKey,
              r.newConversations,
              r.engaged,
              r.turns,
              fmtMin(r.avgMin),
              fmtMin(r.medianMin),
              <PctChip key="p" pct={r.pctWithinSla} muted={r.turns === 0} />,
            ])}
          />
        )}
      </div>

      {/* ══ Cortes ══ */}
      <div className="grid grid-cols-1 lg:grid-cols-2 gap-4">
        <GroupCard title="Por app" rows={s.byProduct} sla={sla} />
        <GroupCard title="Por línea" rows={s.bySeller} sla={sla} />
        <GroupCard
          title="Quién contesta"
          subtitle="El bot contesta en segundos. Lo que mide a la persona es la fila de abajo."
          rows={s.byMode}
          sla={sla}
        />
        <div className="card p-4 md:p-5">
          <h2 className="text-lg font-semibold mb-1">Por hora del día</h2>
          <p className="text-xs text-slate-500 mb-3">Hora argentina en la que escribe el lead. Sirve para ver en qué franja se cae la atención.</p>
          <HourStrip rows={s.byHour} />
        </div>
      </div>

      <p className="text-xs text-slate-400">
        Un "mensaje para contestar" es cada vez que el lead rompe el silencio: se mide desde su primer mensaje
        hasta que sale el nuestro. Ventana: {days} días · límite: {sla} minutos.
      </p>
    </div>
  );
}

/** Días sin un solo mensaje: si no se rellenan, el eje se ve continuo y miente. */
function fillGaps(daily: DailyPoint[]): (DailyPoint | { date: string; empty: true })[] {
  if (daily.length === 0) return [];
  const out: (DailyPoint | { date: string; empty: true })[] = [];
  const byDate = new Map(daily.map((d) => [d.date, d]));
  const cursor = new Date(`${daily[0].date}T00:00:00Z`);
  const end = new Date(`${daily[daily.length - 1].date}T00:00:00Z`);
  while (cursor <= end) {
    const iso = cursor.toISOString().slice(0, 10);
    out.push(byDate.get(iso) ?? { date: iso, empty: true });
    cursor.setUTCDate(cursor.getUTCDate() + 1);
  }
  return out;
}

/** Serie única de % de cumplimiento por día: barra por día, color por estado. */
function DailyStrip({ daily }: { daily: DailyPoint[] }) {
  if (daily.length === 0) return <p className="text-sm text-slate-500">Sin datos en la ventana.</p>;
  const shown = fillGaps(daily).slice(-60);
  return (
    <div>
      <div className="relative h-28">
        {/* Referencia de la meta: 90% de cumplimiento. */}
        <div className="absolute inset-x-0 border-t border-dashed border-slate-300 z-10" style={{ bottom: '90%' }}>
          <span className="absolute -top-4 right-0 text-[10px] text-slate-400">meta 90%</span>
        </div>
        <div className="flex items-end gap-[2px] h-full">
          {shown.map((d) =>
            'empty' in d ? (
              <div key={d.date} className="flex-1 min-w-[6px] h-full flex items-end" title={`${fmtDay(d.date)} · sin mensajes`}>
                <div className="w-full h-[2px] bg-slate-200 rounded" />
              </div>
            ) : (
              <div key={d.date} className="flex-1 min-w-[6px] flex flex-col justify-end h-full">
                <div
                  className={`${tone(d.pctWithinSla).bar} rounded-t w-full transition-all`}
                  style={{ height: `${Math.max(3, d.pctWithinSla)}%` }}
                  title={`${fmtDay(d.date)} · ${d.pctWithinSla}% a tiempo · ${d.turns} mensajes · ${d.unanswered} sin responder · típico ${fmtMin(d.medianMin)}`}
                />
              </div>
            )
          )}
        </div>
      </div>
      <div className="flex justify-between text-[10px] text-slate-400 mt-1">
        <span>{fmtDay(shown[0].date)}</span>
        <span className="text-slate-300">gris = día sin mensajes</span>
        <span>{fmtDay(shown[shown.length - 1].date)}</span>
      </div>
    </div>
  );
}

function HourStrip({ rows }: { rows: HourRow[] }) {
  if (rows.length === 0) return <p className="text-sm text-slate-500">Sin datos.</p>;
  const byHour = new Map(rows.map((r) => [r.hour, r]));
  return (
    <div className="flex items-end gap-[2px]">
      {Array.from({ length: 24 }, (_, h) => {
        const r = byHour.get(h);
        const t = r ? tone(r.pctWithinSla) : null;
        return (
          <div key={h} className="flex-1 flex flex-col items-center gap-1">
            <div className="h-16 w-full flex items-end">
              {r ? (
                <div
                  className={`${t!.bar} rounded-t w-full`}
                  style={{ height: `${Math.max(4, r.pctWithinSla)}%` }}
                  title={`${h}:00 · ${r.pctWithinSla}% a tiempo · ${r.turns} mensajes · típico ${fmtMin(r.medianMin)}`}
                />
              ) : (
                <div className="w-full h-[3px] bg-slate-200 rounded" title={`${h}:00 · sin mensajes`} />
              )}
            </div>
            {h % 3 === 0 && <span className="text-[9px] text-slate-400">{h}</span>}
          </div>
        );
      })}
    </div>
  );
}

function GroupCard({ title, subtitle, rows, sla }: { title: string; subtitle?: string; rows: GroupRow[]; sla: number }) {
  return (
    <div className="card p-4 md:p-5">
      <h2 className="text-lg font-semibold mb-1">{title}</h2>
      {subtitle && <p className="text-xs text-slate-500 mb-3">{subtitle}</p>}
      {rows.length === 0 ? (
        <p className="text-sm text-slate-500">Sin datos en la ventana.</p>
      ) : (
        <Table
          head={['', 'Mensajes', 'Sin responder', 'Típico', 'Peor 10%', `% en ${sla} min`]}
          rows={rows.map((r) => [
            r.label,
            r.stats.turns,
            r.stats.unanswered > 0 ? <span key="u" className="text-red-600 font-medium">{r.stats.unanswered}</span> : 0,
            fmtMin(r.stats.medianMin),
            fmtMin(r.stats.p90Min),
            <PctChip key="p" pct={r.stats.pctWithinSla} />,
          ])}
        />
      )}
    </div>
  );
}

function Table({ head, rows }: { head: string[]; rows: React.ReactNode[][] }) {
  return (
    <div className="overflow-x-auto -mx-4 md:mx-0">
      <table className="min-w-full text-sm">
        <thead className="bg-slate-50 text-xs uppercase tracking-wide text-slate-500">
          <tr>
            {head.map((h, i) => (
              <th key={h + i} className={`px-3 py-2 ${i === 0 ? 'text-left' : 'text-right'}`}>{h}</th>
            ))}
          </tr>
        </thead>
        <tbody className="divide-y divide-slate-100">
          {rows.map((r, i) => (
            <tr key={i}>
              {r.map((c, j) => (
                <td key={j} className={`px-3 py-2 ${j === 0 ? 'font-medium' : 'text-right tabular-nums'}`}>{c}</td>
              ))}
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
}

function PctChip({ pct, muted }: { pct: number; muted?: boolean }) {
  if (muted) return <span className="text-slate-400">—</span>;
  return <span className={`badge tabular-nums ${tone(pct).chip}`}>{pct}%</span>;
}

function Tile({ label, value, sub, alert, toneClass }: {
  label: string; value: number | string; sub?: string; alert?: boolean; toneClass?: string;
}) {
  return (
    <div className={`card p-4 ${alert ? 'border-red-200' : ''}`}>
      <div className="text-[11px] uppercase tracking-wide text-slate-400 truncate">{label}</div>
      <div className={`text-2xl md:text-3xl font-bold tabular-nums mt-1 ${toneClass ?? (alert ? 'text-red-600' : 'text-slate-900')}`}>
        {typeof value === 'number' ? value.toLocaleString('es-AR') : value}
      </div>
      {sub && <div className="text-[11px] text-slate-500 mt-0.5 truncate">{sub}</div>}
    </div>
  );
}
