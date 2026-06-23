import { useMemo, useState } from 'react';
import { useQuery } from '@tanstack/react-query';
import { Link } from 'react-router-dom';
import { api } from '../lib/api';

interface CalEvent {
  id: string;
  kind: string;
  title: string;
  at: string;
  status?: string | null;
  productKey: string;
}

const DOW = ['Dom', 'Lun', 'Mar', 'Mié', 'Jue', 'Vie', 'Sáb'];
const MONTHS = ['Enero', 'Febrero', 'Marzo', 'Abril', 'Mayo', 'Junio', 'Julio', 'Agosto', 'Septiembre', 'Octubre', 'Noviembre', 'Diciembre'];

const KIND: Record<string, { color: string; label: string }> = {
  post: { color: '#10b981', label: 'Posteo agendado' },
  'runner-post': { color: '#0ea5e9', label: 'Genera posteos' },
  'runner-capt': { color: '#f59e0b', label: 'Captación de leads' },
};

const startOfDay = (d: Date) => new Date(d.getFullYear(), d.getMonth(), d.getDate());
const addDays = (d: Date, n: number) => new Date(d.getFullYear(), d.getMonth(), d.getDate() + n);
const startOfWeek = (d: Date) => addDays(startOfDay(d), -d.getDay());
const startOfMonth = (d: Date) => new Date(d.getFullYear(), d.getMonth(), 1);
const sameDay = (a: Date, b: Date) => a.toDateString() === b.toDateString();

// El calendario principal de "Hoy": toda la actividad automática (posteos agendados +
// runners de generación/captación) auto-llenada desde la cadencia configurada.
export default function MainCalendar() {
  const [cursor, setCursor] = useState(() => new Date());
  const today = startOfDay(new Date());

  const gridStart = startOfWeek(startOfMonth(cursor));
  const gridEnd = addDays(gridStart, 42);
  const days = Array.from({ length: 42 }, (_, i) => addDays(gridStart, i));

  const q = useQuery({
    queryKey: ['main-calendar', gridStart.toISOString()],
    queryFn: async () =>
      (await api.get<CalEvent[]>(`/calendar?from=${gridStart.toISOString()}&to=${gridEnd.toISOString()}`)).data,
    refetchInterval: 60000,
  });

  const byDay = useMemo(() => {
    const m = new Map<string, CalEvent[]>();
    for (const e of q.data ?? []) {
      const k = startOfDay(new Date(e.at)).toISOString();
      (m.get(k) ?? m.set(k, []).get(k)!).push(e);
    }
    return m;
  }, [q.data]);

  function shiftMonth(dir: number) {
    const d = new Date(cursor);
    d.setMonth(d.getMonth() + dir);
    setCursor(d);
  }

  return (
    <div className="card p-4 md:p-5">
      <div className="flex items-center justify-between flex-wrap gap-2 mb-3">
        <div className="flex items-center gap-3">
          <h2 className="text-lg font-semibold">Calendario</h2>
          <div className="flex items-center gap-1">
            <button className="btn-secondary px-2 py-0.5" onClick={() => shiftMonth(-1)} aria-label="Mes anterior">‹</button>
            <button className="btn-secondary text-xs" onClick={() => setCursor(new Date())}>Hoy</button>
            <button className="btn-secondary px-2 py-0.5" onClick={() => shiftMonth(1)} aria-label="Mes siguiente">›</button>
          </div>
          <span className="font-medium capitalize text-slate-600">{MONTHS[cursor.getMonth()]} {cursor.getFullYear()}</span>
        </div>
        <div className="flex items-center gap-3 text-[11px] text-slate-500">
          {Object.entries(KIND).map(([k, v]) => (
            <span key={k} className="inline-flex items-center gap-1">
              <span className="w-2 h-2 rounded-full" style={{ background: v.color }} /> {v.label}
            </span>
          ))}
          <Link to="/calendario" className="text-brand-600 hover:underline">ver completo →</Link>
        </div>
      </div>

      <div className="border rounded-lg overflow-hidden">
        <div className="grid grid-cols-7 bg-slate-50 border-b text-[11px] font-medium text-slate-500">
          {DOW.map((d) => <div key={d} className="px-2 py-1.5 text-center">{d}</div>)}
        </div>
        <div className="grid grid-cols-7">
          {days.map((day) => {
            const items = byDay.get(startOfDay(day).toISOString()) ?? [];
            const outside = day.getMonth() !== cursor.getMonth();
            const isToday = sameDay(day, today);
            return (
              <div key={day.toISOString()}
                className={`border-b border-r min-h-[72px] p-1 ${outside ? 'bg-slate-50/60' : 'bg-white'}`}>
                <div className="px-1">
                  <span className={`text-[11px] ${isToday ? 'bg-brand-600 text-white rounded-full w-5 h-5 inline-flex items-center justify-center' : outside ? 'text-slate-300' : 'text-slate-500'}`}>
                    {day.getDate()}
                  </span>
                </div>
                <div className="mt-1 space-y-0.5 max-h-[90px] overflow-y-auto">
                  {items.slice(0, 4).map((e) => {
                    const k = KIND[e.kind] ?? { color: '#94a3b8', label: e.kind };
                    return (
                      <div key={e.id} title={e.title}
                        className="text-[10px] leading-tight truncate rounded px-1 py-0.5 flex items-center gap-1"
                        style={{ background: `${k.color}1a` }}>
                        <span className="w-1.5 h-1.5 rounded-full shrink-0" style={{ background: k.color }} />
                        <span className="truncate">{e.title}</span>
                      </div>
                    );
                  })}
                  {items.length > 4 && <div className="text-[10px] text-slate-400 px-1">+{items.length - 4} más</div>}
                </div>
              </div>
            );
          })}
        </div>
      </div>
      {q.isError && <div className="text-xs text-amber-600 mt-2">No se pudo cargar el calendario.</div>}
    </div>
  );
}
