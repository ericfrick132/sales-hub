import { useQuery } from '@tanstack/react-query';
import { api } from '../lib/api';
import type { SellerGoalProgress } from '../lib/types';

const PERIOD_TITLE: Record<string, string> = {
  Daily: 'Hoy',
  Weekly: 'Esta semana',
};

/**
 * Objetivos del vendedor logueado con barra de progreso (actual / target).
 * Se autorefresca cada 60s. No renderiza nada si no hay objetivos cargados.
 */
export default function GoalsProgress() {
  const { data, isLoading } = useQuery({
    queryKey: ['my-goals-progress'],
    queryFn: async () => (await api.get<SellerGoalProgress[]>('/seller-goals/me/progress')).data,
    refetchInterval: 60000,
  });

  if (isLoading) return null;
  const goals = data ?? [];
  if (goals.length === 0) return null;

  const daily = goals.filter((g) => g.period === 'Daily');
  const weekly = goals.filter((g) => g.period === 'Weekly');

  return (
    <div className="card p-5 space-y-4">
      <h2 className="text-lg font-semibold">Tus objetivos</h2>
      {daily.length > 0 && <GoalGroup title={PERIOD_TITLE.Daily} goals={daily} />}
      {weekly.length > 0 && <GoalGroup title={PERIOD_TITLE.Weekly} goals={weekly} />}
    </div>
  );
}

function GoalGroup({ title, goals }: { title: string; goals: SellerGoalProgress[] }) {
  return (
    <div className="space-y-3">
      <div className="text-xs font-medium uppercase tracking-wide text-slate-400">{title}</div>
      {goals.map((g) => (
        <GoalBar key={g.goalId} g={g} />
      ))}
    </div>
  );
}

function GoalBar({ g }: { g: SellerGoalProgress }) {
  const done = g.percent >= 100;
  return (
    <div>
      <div className="flex items-center justify-between text-sm mb-1">
        <span className="font-medium">
          {g.metric}
          {g.productKey && <span className="text-slate-400"> · {g.productKey}</span>}
        </span>
        <span className={done ? 'text-emerald-600 font-semibold' : 'text-slate-500'}>
          {g.current}/{g.target}
        </span>
      </div>
      <div className="w-full h-2 bg-slate-200 rounded-full overflow-hidden">
        <div
          className={`h-full rounded-full transition-all ${done ? 'bg-emerald-500' : 'bg-brand-500'}`}
          style={{ width: `${Math.min(100, g.percent)}%` }}
        />
      </div>
    </div>
  );
}
