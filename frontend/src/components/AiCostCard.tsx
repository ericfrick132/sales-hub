import { useQuery } from '@tanstack/react-query';
import { api } from '../lib/api';
import type { AiCost } from '../lib/types';

/** Etiquetas legibles para el "dónde se gasta". */
const FEATURE_LABEL: Record<string, string> = {
  conversacion: 'Conversación (ventas)',
  seo: 'SEO',
  social: 'Social',
  unknown: 'Otros',
};

/** Los costos son chicos (Haiku ≈ US$1/M tokens): mostramos 4 decimales si es < 1¢. */
const usd = (n: number) => {
  if (!n) return '$0';
  if (n < 0.01) return `$${n.toFixed(4)}`;
  return `$${n.toFixed(2)}`;
};

/**
 * Widget de gasto de la API de Claude (la key que usa la IA de ventas). Muestra
 * el costo USD de hoy / 7d / 30d y el desglose de HOY por feature: dónde se gasta
 * (conversación de ventas, SEO, social). Costo real calculado por tokens.
 */
export default function AiCostCard() {
  const { data, isLoading } = useQuery({
    queryKey: ['ai-cost'],
    queryFn: async () => (await api.get<AiCost>('/dashboard/ai-cost')).data,
    refetchInterval: 60000,
  });

  if (isLoading || !data) return null;
  const features = data.byFeatureToday ?? [];

  return (
    <div className="card p-5">
      <div className="flex items-center justify-between mb-3">
        <h2 className="text-lg font-semibold">Gasto IA (Claude)</h2>
        <span className="text-xs text-slate-400">key de ventas · USD</span>
      </div>

      <div className="flex items-end gap-6 mb-4">
        <div>
          <div className="text-3xl font-bold tabular-nums">{usd(data.todayUsd)}</div>
          <div className="text-xs text-slate-500">hoy</div>
        </div>
        <div className="text-sm text-slate-600 space-y-0.5">
          <div className="tabular-nums">{usd(data.last7dUsd)} <span className="text-slate-400">· 7 días</span></div>
          <div className="tabular-nums">{usd(data.last30dUsd)} <span className="text-slate-400">· 30 días</span></div>
        </div>
      </div>

      <div>
        <div className="text-xs uppercase tracking-wide text-slate-400 mb-1.5">Dónde se gastó hoy</div>
        {features.length === 0 ? (
          <p className="text-sm text-slate-400">Sin uso de IA hoy.</p>
        ) : (
          <div className="space-y-1">
            {features.map((f) => (
              <div key={f.feature} className="flex items-center justify-between text-sm">
                <span className="text-slate-600">{FEATURE_LABEL[f.feature] ?? f.feature}</span>
                <span className="tabular-nums">
                  {usd(f.costUsd)} <span className="text-slate-400 text-xs">· {f.calls} llamadas</span>
                </span>
              </div>
            ))}
          </div>
        )}
      </div>

      <p className="text-[11px] text-slate-400 mt-3">
        Costo real por tokens (input/output + cache) según el modelo de cada llamada.
      </p>
    </div>
  );
}
