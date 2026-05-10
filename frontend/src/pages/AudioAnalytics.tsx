import { useEffect, useMemo, useState } from 'react';
import { useQuery } from '@tanstack/react-query';
import { api } from '../lib/api';
import type { MediaAsset, MessageStep, Product } from '../lib/types';

interface StrategyMetrics {
  leads: number;
  sent: number;
  replied: number;
  interested: number;
  demoScheduled: number;
  closed: number;
}

interface StrategyDto {
  key: string;
  label: string;
  isDefault: boolean;
  steps: MessageStep[];
  metrics: StrategyMetrics;
}

interface AudioStatRow {
  mediaAssetId: string;
  fileName: string;
  mimeType: string;
  sent: number;
  replied: number;
  interested: number;
  demoScheduled: number;
  closed: number;
}

interface AudioAnalyticsDto {
  productKey: string;
  productDisplayName: string;
  strategies: StrategyDto[];
  audios: AudioStatRow[];
}

const pct = (n: number, d: number) => (d === 0 ? '—' : `${((n / d) * 100).toFixed(1)}%`);

export default function AudioAnalytics() {
  const productsQ = useQuery({
    queryKey: ['products-admin'],
    queryFn: async () => (await api.get<Product[]>('/products')).data
  });

  const [productKey, setProductKey] = useState<string>('');

  // Auto-pick: si no hay selección, agarrar el primero activo (o el primero a secas).
  useEffect(() => {
    if (productKey || !productsQ.data || productsQ.data.length === 0) return;
    const firstActive = productsQ.data.find((p) => p.active) ?? productsQ.data[0];
    setProductKey(firstActive.productKey);
  }, [productsQ.data, productKey]);

  return (
    <div className="space-y-5">
      <header className="flex items-baseline justify-between gap-4 flex-wrap">
        <div>
          <h2 className="text-xl font-bold">Performance de audios y estrategias</h2>
          <p className="text-xs text-slate-500">
            Compará el rendimiento de cada cadencia (combo texto + audio) y de cada archivo de audio
            por separado. Los porcentajes son sobre leads efectivamente enviados.
          </p>
        </div>
        <label className="flex items-center gap-2 text-sm">
          <span className="text-slate-500 text-xs">Producto</span>
          <select
            className="input"
            value={productKey}
            onChange={(e) => setProductKey(e.target.value)}>
            {(productsQ.data ?? []).map((p) => (
              <option key={p.id} value={p.productKey}>
                {p.displayName} {p.active ? '' : '(pausado)'}
              </option>
            ))}
          </select>
        </label>
      </header>

      {productKey && <ProductAnalytics productKey={productKey} />}
    </div>
  );
}

function ProductAnalytics({ productKey }: { productKey: string }) {
  const dataQ = useQuery({
    queryKey: ['audio-analytics', productKey],
    queryFn: async () => (await api.get<AudioAnalyticsDto>(`/audio-analytics/${productKey}`)).data,
    refetchInterval: 60_000
  });
  const mediaQ = useQuery({
    queryKey: ['product-media', productKey],
    queryFn: async () => (await api.get<MediaAsset[]>(`/products/${productKey}/media`)).data
  });

  const mediaById = useMemo(() => {
    const m = new Map<string, MediaAsset>();
    (mediaQ.data ?? []).forEach((a) => m.set(a.id, a));
    return m;
  }, [mediaQ.data]);

  if (dataQ.isLoading) {
    return <div className="card p-6 text-sm text-slate-500">Cargando…</div>;
  }
  if (!dataQ.data) {
    return <div className="card p-6 text-sm text-slate-500">Sin datos.</div>;
  }

  const { strategies, audios } = dataQ.data;

  return (
    <div className="space-y-5">
      <StrategiesPanel strategies={strategies} mediaById={mediaById} />
      <AudioStatsPanel audios={audios} />
    </div>
  );
}

function StrategiesPanel({
  strategies, mediaById
}: { strategies: StrategyDto[]; mediaById: Map<string, MediaAsset> }) {
  // Best por reply rate, mínimo 5 sends para evitar ruido.
  const eligible = strategies.filter((s) => s.metrics.sent >= 5);
  const best = eligible.length === 0
    ? null
    : eligible.reduce((a, b) =>
        (a.metrics.replied / Math.max(1, a.metrics.sent)) >=
        (b.metrics.replied / Math.max(1, b.metrics.sent)) ? a : b);

  return (
    <div className="card p-4 md:p-5 space-y-3">
      <div>
        <h3 className="font-semibold">Performance por estrategia (combo)</h3>
        <p className="text-xs text-slate-500">
          Cada fila es una cadencia configurada en el producto. La cadencia <i>Default</i> agrupa los
          leads cuya categoría no tiene override propio. 🏆 = mejor reply rate (mínimo 5 envíos).
        </p>
      </div>

      {strategies.length === 0 && (
        <div className="text-xs text-slate-500 border border-dashed border-slate-300 rounded p-3 text-center">
          Sin cadencias configuradas para este producto.
        </div>
      )}

      <div className="space-y-3">
        {strategies.map((s) => (
          <StrategyCard
            key={s.key}
            strategy={s}
            mediaById={mediaById}
            isBest={best?.key === s.key}
          />
        ))}
      </div>
    </div>
  );
}

function StrategyCard({
  strategy: s, mediaById, isBest
}: { strategy: StrategyDto; mediaById: Map<string, MediaAsset>; isBest: boolean }) {
  const m = s.metrics;
  return (
    <div className={`border rounded-md p-3 ${isBest ? 'border-emerald-300 bg-emerald-50/40' : 'border-slate-200 bg-slate-50/30'}`}>
      <div className="flex items-baseline justify-between gap-3 flex-wrap mb-2">
        <div className="flex items-center gap-2">
          {isBest && <span title="Mejor reply rate (≥5 envíos)">🏆</span>}
          <span className="font-semibold">{s.label}</span>
          {s.isDefault
            ? <span className="text-[10px] text-slate-500 uppercase tracking-wider">cadencia base</span>
            : <span className="text-[10px] text-emerald-700 uppercase tracking-wider">override de categoría</span>}
        </div>
        <div className="text-xs text-slate-500">
          {s.steps.length} paso{s.steps.length === 1 ? '' : 's'}
        </div>
      </div>

      <MetricsBar metrics={m} />

      <div className="mt-3 flex gap-2 overflow-x-auto pb-1">
        {s.steps.length === 0 && (
          <div className="text-xs text-slate-400 italic">
            Sin pasos configurados — los leads de esta cadencia caen al mensaje legacy.
          </div>
        )}
        {s.steps.map((step, i) => (
          <StepPreview key={i} step={step} index={i} mediaById={mediaById} />
        ))}
      </div>
    </div>
  );
}

function MetricsBar({ metrics: m }: { metrics: StrategyMetrics }) {
  return (
    <div className="grid grid-cols-2 sm:grid-cols-6 gap-2 text-xs">
      <Stat label="Leads" value={m.leads} />
      <Stat label="Enviados" value={m.sent} sub={pct(m.sent, m.leads)} />
      <Stat label="Respondieron" value={m.replied} sub={pct(m.replied, m.sent)} highlight />
      <Stat label="Interesados" value={m.interested} sub={pct(m.interested, m.sent)} />
      <Stat label="Demos" value={m.demoScheduled} sub={pct(m.demoScheduled, m.sent)} />
      <Stat label="Cerrados" value={m.closed} sub={pct(m.closed, m.sent)} />
    </div>
  );
}

function Stat({ label, value, sub, highlight }: {
  label: string; value: number; sub?: string; highlight?: boolean;
}) {
  return (
    <div className={`rounded border px-2 py-1.5 ${highlight ? 'border-emerald-200 bg-emerald-50' : 'border-slate-200 bg-white'}`}>
      <div className="text-[10px] uppercase tracking-wider text-slate-500">{label}</div>
      <div className="font-mono font-semibold">{value}</div>
      {sub !== undefined && <div className="text-[10px] text-slate-400">{sub}</div>}
    </div>
  );
}

function StepPreview({
  step, index, mediaById
}: { step: MessageStep; index: number; mediaById: Map<string, MediaAsset> }) {
  const variantIds = (step.mediaAssetIds && step.mediaAssetIds.length > 0)
    ? step.mediaAssetIds
    : (step.mediaAssetId ? [step.mediaAssetId] : []);
  const assets = variantIds.map((id) => mediaById.get(id)).filter((a): a is MediaAsset => !!a);
  const first = assets[0];
  const kind: 'text' | 'audio' | 'image' | 'document' = !first
    ? 'text'
    : first.mimeType.startsWith('audio/') ? 'audio'
      : first.mimeType.startsWith('image/') ? 'image'
        : 'document';
  const icon = kind === 'audio' ? '🎤' : kind === 'image' ? '🖼️' : kind === 'document' ? '📄' : '💬';

  const delayLabel = index === 0
    ? 'inicial'
    : step.delaySeconds === 0
      ? 'inmediato'
      : step.delaySeconds < 60
        ? `+${step.delaySeconds}s`
        : `+${Math.floor(step.delaySeconds / 60)}m${step.delaySeconds % 60 ? ` ${step.delaySeconds % 60}s` : ''}`;

  return (
    <div className="min-w-[160px] max-w-[220px] flex-shrink-0 border border-slate-200 bg-white rounded p-2 text-xs">
      <div className="flex items-center justify-between text-[10px] text-slate-500 mb-1">
        <span>Paso {index + 1}</span>
        <span>{delayLabel}</span>
      </div>
      <div className="flex items-center gap-1 font-medium text-slate-700 mb-1">
        <span>{icon}</span>
        <span className="truncate">
          {kind === 'text'
            ? 'Texto'
            : kind === 'audio'
              ? `Audio${assets.length > 1 ? ` (×${assets.length})` : ''}`
              : kind === 'image' ? 'Imagen' : 'PDF'}
        </span>
      </div>
      {step.text && (
        <div className="text-slate-600 line-clamp-3 whitespace-pre-wrap break-words">
          {step.text}
        </div>
      )}
      {kind === 'audio' && assets.length > 0 && (
        <div className="text-[10px] text-slate-400 mt-1 truncate" title={assets.map((a) => a.fileName).join('\n')}>
          {assets.map((a) => a.fileName).join(', ')}
        </div>
      )}
    </div>
  );
}

function AudioStatsPanel({ audios }: { audios: AudioStatRow[] }) {
  if (audios.length === 0) {
    return (
      <div className="card p-4 md:p-5 space-y-2">
        <h3 className="font-semibold">Performance por archivo de audio</h3>
        <div className="text-xs text-slate-500 border border-dashed border-slate-300 rounded p-3 text-center">
          Este producto todavía no tiene audios cargados.
        </div>
      </div>
    );
  }

  const eligible = audios.filter((r) => r.sent >= 5);
  const best = eligible.length === 0
    ? null
    : eligible.reduce((a, b) => (a.replied / a.sent) >= (b.replied / b.sent) ? a : b);

  return (
    <div className="card p-4 md:p-5 space-y-2">
      <div>
        <h3 className="font-semibold">Performance por archivo de audio</h3>
        <p className="text-xs text-slate-500">
          Tasa de respuesta y conversión por cada variante de audio enviada. Útil para ver qué versión
          del audio está rindiendo mejor dentro de un mismo paso.
        </p>
      </div>
      <div className="overflow-x-auto">
        <table className="w-full text-xs">
          <thead className="text-slate-500">
            <tr>
              <th className="text-left py-1 pr-2">Archivo</th>
              <th className="text-right py-1 px-2">Enviados</th>
              <th className="text-right py-1 px-2">Respondieron</th>
              <th className="text-right py-1 px-2">Interesados</th>
              <th className="text-right py-1 px-2">Demos</th>
              <th className="text-right py-1 px-2">Cerrados</th>
            </tr>
          </thead>
          <tbody>
            {audios.map((r) => {
              const isBest = best?.mediaAssetId === r.mediaAssetId;
              return (
                <tr key={r.mediaAssetId} className={`border-t border-slate-100 ${isBest ? 'bg-emerald-50' : ''}`}>
                  <td className="py-1 pr-2">
                    <div className="flex items-center gap-1">
                      {isBest && <span title="Mejor reply rate (≥5 envíos)">🏆</span>}
                      <span className="truncate max-w-[280px]" title={r.fileName}>{r.fileName}</span>
                    </div>
                  </td>
                  <td className="text-right py-1 px-2 font-mono">{r.sent}</td>
                  <td className="text-right py-1 px-2 font-mono">
                    {r.replied} <span className="text-slate-400">({pct(r.replied, r.sent)})</span>
                  </td>
                  <td className="text-right py-1 px-2 font-mono">
                    {r.interested} <span className="text-slate-400">({pct(r.interested, r.sent)})</span>
                  </td>
                  <td className="text-right py-1 px-2 font-mono">{r.demoScheduled}</td>
                  <td className="text-right py-1 px-2 font-mono">{r.closed}</td>
                </tr>
              );
            })}
          </tbody>
        </table>
      </div>
    </div>
  );
}
