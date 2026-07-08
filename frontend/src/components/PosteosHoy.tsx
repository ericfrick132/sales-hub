import { useQuery } from '@tanstack/react-query';
import { Link } from 'react-router-dom';
import { api } from '../lib/api';

type AppHealth = {
  productKey: string;
  channels: number;
  postHours: number[];
  quotaSoFar: number;
  dayQuota: number;
  createdToday: number;
  generating: number;
  drafts: number;
  scheduled: number;
  posted: number;
  errors: number;
};

/**
 * Gauge "Posteos hoy": generado vs cupo del día por app (acumulado por slots ya
 * pasados). Rojo = atrasado o con errores — el outage del 8/7 (0 posteos por un bug
 * de jsonb) fue invisible justamente porque no existía esta vista.
 */
export default function PosteosHoy() {
  const { data } = useQuery({
    queryKey: ['posteos-today-health'],
    queryFn: async () => (await api.get<AppHealth[]>('/posteos/today-health')).data,
    refetchInterval: 60000,
  });

  const apps = data ?? [];
  const totalCreated = apps.reduce((a, x) => a + x.createdToday, 0);
  const totalQuota = apps.reduce((a, x) => a + x.quotaSoFar, 0);
  const totalErrors = apps.reduce((a, x) => a + x.errors, 0);
  const behind = totalQuota > 0 && totalCreated < totalQuota;

  return (
    <div className="card p-4">
      <div className="flex items-baseline justify-between mb-2">
        <h2 className="font-semibold">Posteos hoy</h2>
        <Link to="/calendario" className="text-xs text-blue-600 hover:underline">Ver calendario →</Link>
      </div>
      <div className={`text-sm mb-3 font-medium ${totalErrors > 0 ? 'text-red-600' : behind ? 'text-amber-600' : 'text-emerald-700'}`}>
        {totalCreated}/{totalQuota} generados del cupo de hoy
        {totalErrors > 0 && ` · ${totalErrors} con error`}
        {!behind && totalErrors === 0 && totalQuota > 0 && ' · al día ✓'}
      </div>
      <div className="space-y-2">
        {apps.map((a) => {
          const pct = a.quotaSoFar > 0 ? Math.min(100, (100 * a.createdToday) / a.quotaSoFar) : 100;
          const ok = a.createdToday >= a.quotaSoFar && a.errors === 0;
          return (
            <div key={a.productKey}>
              <div className="flex items-baseline justify-between text-xs">
                <span className="font-medium">{a.productKey}</span>
                <span className="text-slate-500 font-mono tabular-nums">
                  {a.createdToday}/{a.quotaSoFar}
                  {a.generating > 0 && ` · ${a.generating} generando`}
                  {a.scheduled > 0 && ` · ${a.scheduled} agendados`}
                  {a.posted > 0 && ` · ${a.posted} publicados`}
                  {a.errors > 0 && <span className="text-red-600 font-bold"> · {a.errors} error</span>}
                </span>
              </div>
              <div className="h-1.5 rounded-full bg-slate-200 overflow-hidden mt-0.5">
                <div
                  className={`h-full rounded-full transition-all ${a.errors > 0 ? 'bg-red-500' : ok ? 'bg-emerald-500' : 'bg-amber-500'}`}
                  style={{ width: `${pct}%` }}
                />
              </div>
            </div>
          );
        })}
        {apps.length === 0 && <p className="text-sm text-slate-400">Sin perfiles de posteo activos.</p>}
      </div>
      <p className="text-[11px] text-slate-400 mt-3">
        Cupo = canales activos × posteos por horario × slots ya pasados del día. El worker recupera solo los slots perdidos.
      </p>
    </div>
  );
}
