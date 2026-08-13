import { Link } from 'react-router-dom';
import { useQuery } from '@tanstack/react-query';
import { api } from '../lib/api';

/**
 * Lo que ve el que atiende: su tiempo de respuesta de hoy y quién lo está esperando
 * ahora mismo. La idea es que se autogestione sin que nadie lo persiga.
 */

interface SlaStats {
  turns: number; answered: number; unanswered: number;
  medianMin: number | null; p90Min: number | null; avgMin: number | null;
  pctWithinSla: number; pctAnsweredWithinSla: number;
}
interface WaitingRow {
  leadId: string; leadName: string; phone: string; productKey: string;
  waitingSince: string; minutesWaiting: number; pendingMessages: number;
  lastText: string; breached: boolean;
}
interface MyAttention {
  slaMinutes: number;
  today: SlaStats;
  last7d: SlaStats;
  waiting: WaitingRow[];
  waitingBreached: number;
  backlogOlder: number;
}

const fmtMin = (m: number | null) => {
  if (m === null) return '—';
  if (m < 1) return `${Math.round(m * 60)} s`;
  if (m < 60) return `${m.toFixed(1).replace('.', ',')} min`;
  return `${Math.floor(m / 60)} h ${Math.round(m % 60)} min`;
};
const fmtWait = (min: number) => (min < 60 ? `${min} min` : min < 1440 ? `${Math.floor(min / 60)} h` : `${Math.floor(min / 1440)} d`);
const waLink = (phone: string) => `https://wa.me/${(phone || '').replace(/\D/g, '')}`;

export default function MySla() {
  const { data } = useQuery({
    queryKey: ['attention-me'],
    queryFn: async () => (await api.get<MyAttention>('/attention/me')).data,
    refetchInterval: 30000,
  });

  if (!data) return null;
  const sla = data.slaMinutes;
  const queue = data.waiting;
  const ok = data.today.turns === 0 || data.today.pctWithinSla >= 90;

  return (
    <div className={`card p-4 md:p-5 border-2 ${queue.some((q) => q.breached) ? 'border-red-200' : ok ? 'border-emerald-200' : 'border-amber-200'}`}>
      <div className="flex items-baseline justify-between flex-wrap gap-2 mb-3">
        <h2 className="text-lg font-semibold">Mi tiempo de respuesta</h2>
        <span className="text-xs text-slate-400">meta: contestar en menos de {sla} minutos</span>
      </div>

      <div className="grid grid-cols-2 md:grid-cols-4 gap-3 mb-4">
        <Mini
          label="Esperando ahora"
          value={queue.length}
          sub={data.waitingBreached > 0 ? `${data.waitingBreached} pasados del límite` : 'al día'}
          alert={data.waitingBreached > 0}
        />
        <Mini
          label="A tiempo hoy"
          value={data.today.turns > 0 ? `${data.today.pctWithinSla}%` : '—'}
          sub={`${data.today.turns} para contestar`}
        />
        <Mini label="Tardanza típica hoy" value={fmtMin(data.today.medianMin)} sub={`peor 10%: ${fmtMin(data.today.p90Min)}`} />
        <Mini
          label="A tiempo · 7 días"
          value={data.last7d.turns > 0 ? `${data.last7d.pctWithinSla}%` : '—'}
          sub={data.last7d.unanswered > 0 ? `${data.last7d.unanswered} sin responder` : 'nada colgado'}
          alert={data.last7d.unanswered > 0}
        />
      </div>

      {queue.length > 0 && (
        <div>
          <h3 className="text-sm font-semibold mb-2">Te están esperando</h3>
          <div className="divide-y divide-slate-100">
            {queue.slice(0, 8).map((q) => (
              <div key={q.leadId} className="py-2 flex items-center gap-3 text-sm">
                <span className={`badge tabular-nums shrink-0 ${q.breached ? 'bg-red-100 text-red-800' : 'bg-slate-100 text-slate-600'}`}>
                  {fmtWait(q.minutesWaiting)}
                </span>
                <div className="min-w-0 flex-1">
                  <Link to={`/leads/${q.leadId}`} className="font-medium text-brand-700 hover:underline">{q.leadName}</Link>
                  <span className="text-slate-400 text-xs ml-1">{q.productKey}</span>
                  <div className="text-xs text-slate-500 truncate" title={q.lastText}>{q.lastText}</div>
                </div>
                <a href={waLink(q.phone)} target="_blank" rel="noreferrer" className="text-xs text-brand-700 hover:underline shrink-0">
                  Contestar
                </a>
              </div>
            ))}
          </div>
          {queue.length > 8 && <p className="text-xs text-slate-400 mt-2">y {queue.length - 8} más.</p>}
          {data.backlogOlder > 0 && (
            <p className="text-xs text-slate-400 mt-2">
              Además hay {data.backlogOlder} chats viejos sin responder (más de una semana).
            </p>
          )}
        </div>
      )}
    </div>
  );
}

function Mini({ label, value, sub, alert }: { label: string; value: number | string; sub?: string; alert?: boolean }) {
  return (
    <div>
      <div className="text-[11px] uppercase tracking-wide text-slate-400 truncate">{label}</div>
      <div className={`text-xl font-bold tabular-nums ${alert ? 'text-red-600' : 'text-slate-900'}`}>{value}</div>
      {sub && <div className="text-[11px] text-slate-500 truncate">{sub}</div>}
    </div>
  );
}
