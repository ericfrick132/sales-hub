import { useMemo } from 'react';
import { useQuery } from '@tanstack/react-query';
import { api } from '../lib/api';

interface CalEvent { id: string; kind: string; title: string; at: string; status?: string | null; productKey: string }

const COLOR: Record<string, string> = {
  post: '#10b981', 'runner-post': '#0ea5e9', 'runner-capt': '#f59e0b', 'runner-follow': '#8b5cf6', seo: '#0d9488',
};
const hhmm = (iso: string) => { const d = new Date(iso); const p = (n: number) => String(n).padStart(2, '0'); return `${p(d.getHours())}:${p(d.getMinutes())}`; };

// Checklist de lo que el sistema hizo / va a hacer hoy (eventos del calendario del día).
export default function TodayChecklist() {
  const now = new Date();
  const start = new Date(now.getFullYear(), now.getMonth(), now.getDate());
  const end = new Date(start); end.setDate(end.getDate() + 1);

  const q = useQuery({
    queryKey: ['today-checklist', start.toISOString()],
    queryFn: async () => (await api.get<CalEvent[]>(`/calendar?from=${start.toISOString()}&to=${end.toISOString()}`)).data,
    refetchInterval: 60000,
  });

  const events = useMemo(() => [...(q.data ?? [])].sort((a, b) => (a.at < b.at ? -1 : 1)), [q.data]);
  const nowMs = Date.now();
  const done = events.filter((e) => new Date(e.at).getTime() <= nowMs).length;

  return (
    <div className="card p-4 md:p-5">
      <div className="flex items-center justify-between mb-2">
        <h2 className="text-lg font-semibold">Tareas de hoy</h2>
        <span className="text-xs text-slate-500">{done}/{events.length} hechas</span>
      </div>
      {events.length === 0 ? (
        <div className="text-sm text-slate-400">No hay tareas automáticas programadas para hoy.</div>
      ) : (
        <ul className="space-y-1 max-h-72 overflow-y-auto">
          {events.map((e) => {
            const isDone = new Date(e.at).getTime() <= nowMs;
            return (
              <li key={e.id} className="flex items-center gap-2 text-sm">
                <span className={`w-4 h-4 shrink-0 rounded-full grid place-items-center text-[10px] ${isDone ? 'bg-emerald-500 text-white' : 'border-2'}`}
                  style={isDone ? undefined : { borderColor: COLOR[e.kind] ?? '#cbd5e1' }}>
                  {isDone ? '✓' : ''}
                </span>
                <span className="text-[11px] text-slate-400 tabular-nums w-10 shrink-0">{hhmm(e.at)}</span>
                <span className={`flex-1 truncate ${isDone ? 'text-slate-400 line-through' : 'text-slate-700'}`}>{e.title}</span>
              </li>
            );
          })}
        </ul>
      )}
    </div>
  );
}
