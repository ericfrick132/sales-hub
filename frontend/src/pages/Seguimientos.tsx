import { useState } from 'react';
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import toast from 'react-hot-toast';
import { api } from '../lib/api';
import Switch from '../components/Switch';

type Step = { order: number; delayMinutes: number; channel: string; subject: string | null; body: string };
type Sequence = {
  id: string; productKey: string; displayName: string; trigger: string; enabled: boolean; steps: Step[];
  backlogColdMessage: string | null; backlogWarmMessage: string | null; backlogColdAfterDays: number | null;
};
type ProductRef = { productKey: string; displayName: string; reengageActive: boolean };
type SequencesResp = { sequences: Sequence[]; products: ProductRef[] };

type MetricRow = {
  productKey: string; trigger: string; triggered: number; sent: number;
  byChannel: Record<string, number>; converted: number; gaveUp: number; conversionRate: number;
};
type MetricsResp = { period: string; rows: MetricRow[] };

const PERIODS = [
  { key: 'today', label: 'Hoy' },
  { key: '7d', label: '7 días' },
  { key: '30d', label: '30 días' },
  { key: 'all', label: 'Todo' },
] as const;

const CHANNELS = ['whatsapp', 'email'];

/**
 * Config CENTRAL (fuente de verdad) del follow-up de abandono por app + reportes por app.
 * Cada app baja estas secuencias (pull-and-persist) y las ejecuta con su motor/canales,
 * y reporta eventos acá. sales-hub es agnóstico: el `trigger` lo define la app.
 */
export default function Seguimientos() {
  const qc = useQueryClient();
  const [period, setPeriod] = useState<string>('7d');

  const { data, isLoading } = useQuery({
    queryKey: ['followup-sequences'],
    queryFn: async () => (await api.get<SequencesResp>('/followup-sequences')).data,
  });
  const metrics = useQuery({
    queryKey: ['followup-metrics', period],
    queryFn: async () => (await api.get<MetricsResp>('/dashboard/followup-metrics', { params: { period } })).data,
    refetchInterval: 60000,
  });

  const invalidate = () => qc.invalidateQueries({ queryKey: ['followup-sequences'] });

  const [newProduct, setNewProduct] = useState('');
  const [newTrigger, setNewTrigger] = useState('');

  const create = useMutation({
    mutationFn: async () => api.put('/followup-sequences', {
      productKey: newProduct, trigger: newTrigger.trim(), enabled: false, steps: [],
      backlogColdMessage: null, backlogWarmMessage: null, backlogColdAfterDays: null,
    }),
    onSuccess: () => { setNewTrigger(''); invalidate(); toast.success('Secuencia creada'); },
    onError: (e: any) => toast.error(e?.response?.data?.error ?? 'No se pudo crear'),
  });

  const sequences = data?.sequences ?? [];
  const products = data?.products ?? [];
  const rows = metrics.data?.rows ?? [];

  return (
    <div className="space-y-5 max-w-4xl">
      <div>
        <h1 className="text-xl md:text-2xl font-bold">Seguimientos (follow-up de abandono)</h1>
        <p className="text-sm text-slate-500">
          Config central por app: tiempos, mensaje y canal de cada paso. Las apps la bajan y la
          ejecutan con sus propios canales. Acá ves los reportes de lo que reportan.
        </p>
      </div>

      {/* COBERTURA POR APP */}
      <div className="card p-4">
        <h2 className="text-lg font-semibold mb-2">Cobertura por app</h2>
        <div className="overflow-x-auto -mx-1">
          <table className="min-w-full text-sm">
            <thead className="text-xs uppercase tracking-wide text-slate-500">
              <tr>
                <th className="px-2 py-1 text-left">App</th>
                <th className="px-2 py-1 text-center">Follow-up de abandono</th>
                <th className="px-2 py-1 text-center">Re-enganche inteligente</th>
              </tr>
            </thead>
            <tbody className="divide-y divide-slate-100">
              {products.map((p) => {
                const hasFollowup = sequences.some((s) => s.productKey === p.productKey);
                return (
                  <tr key={p.productKey} className="hover:bg-slate-50">
                    <td className="px-2 py-1.5 font-medium">{p.displayName}</td>
                    <td className="px-2 py-1.5 text-center">
                      {hasFollowup
                        ? <span className="text-emerald-600 font-semibold">✓</span>
                        : <span className="text-slate-300">—</span>}
                    </td>
                    <td className="px-2 py-1.5 text-center">
                      {p.reengageActive
                        ? <span className="text-emerald-600 font-semibold">✓</span>
                        : <span className="badge bg-amber-100 text-amber-700"
                            title="Esta app no pushea sus leads a sales-hub todavía → falta conectar el push de leads (/api/hub/leads) para que el re-enganche inteligente la cubra.">
                            TODO
                          </span>}
                    </td>
                  </tr>
                );
              })}
            </tbody>
          </table>
        </div>
        <p className="text-[11px] text-slate-400 mt-2">
          <b>Abandono</b> = tiene secuencia de follow-up (lo ejecuta la app). <b>Re-enganche inteligente</b> = la app
          alimenta leads a sales-hub y entran al re-enganche analizado + priorizado por score (hoy: gymhero / turnos-pro).
          <b> TODO</b> = falta conectar el push de leads de esa app.
        </p>
      </div>

      {/* REPORTES POR APP */}
      <div className="card p-5">
        <div className="flex items-center justify-between mb-3 gap-2 flex-wrap">
          <h2 className="text-lg font-semibold">Reportes por app</h2>
          <div className="flex gap-1">
            {PERIODS.map((p) => (
              <button key={p.key} onClick={() => setPeriod(p.key)}
                className={`text-xs px-2 py-1 rounded ${period === p.key ? 'bg-brand-600 text-white' : 'bg-slate-100 text-slate-600 hover:bg-slate-200'}`}>
                {p.label}
              </button>
            ))}
          </div>
        </div>
        {metrics.isLoading ? (
          <p className="text-slate-400 text-sm">Cargando…</p>
        ) : rows.length === 0 ? (
          <p className="text-slate-400 text-sm">Todavía no hay eventos reportados en esta ventana.</p>
        ) : (
          <div className="overflow-x-auto -mx-1">
            <table className="min-w-full text-sm">
              <thead className="text-xs uppercase tracking-wide text-slate-500">
                <tr>
                  <th className="px-2 py-1 text-left">App</th>
                  <th className="px-2 py-1 text-left">Trigger</th>
                  <th className="px-2 py-1 text-right">Abandonos</th>
                  <th className="px-2 py-1 text-right">Enviados</th>
                  <th className="px-2 py-1 text-right">Entraron</th>
                  <th className="px-2 py-1 text-right">Se rindieron</th>
                  <th className="px-2 py-1 text-right">% conv.</th>
                </tr>
              </thead>
              <tbody className="divide-y divide-slate-100">
                {rows.map((r) => (
                  <tr key={`${r.productKey}/${r.trigger}`} className="hover:bg-slate-50">
                    <td className="px-2 py-1.5 font-medium">{r.productKey}</td>
                    <td className="px-2 py-1.5 text-slate-600">{r.trigger || '—'}</td>
                    <td className="px-2 py-1.5 text-right tabular-nums">{r.triggered}</td>
                    <td className="px-2 py-1.5 text-right tabular-nums text-slate-600"
                      title={Object.entries(r.byChannel).map(([c, n]) => `${c}: ${n}`).join(' · ')}>
                      {r.sent}
                    </td>
                    <td className="px-2 py-1.5 text-right tabular-nums font-semibold text-emerald-700">{r.converted}</td>
                    <td className="px-2 py-1.5 text-right tabular-nums text-slate-500">{r.gaveUp}</td>
                    <td className="px-2 py-1.5 text-right tabular-nums font-medium">{Math.round(r.conversionRate * 100)}%</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
      </div>

      {/* NUEVA SECUENCIA */}
      <div className="card p-4 space-y-3">
        <label className="text-sm font-medium">Nueva secuencia</label>
        <div className="grid grid-cols-1 sm:grid-cols-[1fr_1fr_auto] gap-2">
          <select className="input" value={newProduct} onChange={(e) => setNewProduct(e.target.value)}>
            <option value="">— App —</option>
            {products.map((p) => <option key={p.productKey} value={p.productKey}>{p.displayName}</option>)}
          </select>
          <input className="input" placeholder="trigger (ej. otp_abandoned)"
            value={newTrigger} onChange={(e) => setNewTrigger(e.target.value)} />
          <button className="btn-primary" disabled={!newProduct || !newTrigger.trim() || create.isPending}
            onClick={() => create.mutate()}>Crear</button>
        </div>
        <p className="text-xs text-slate-400">El <b>trigger</b> lo define la app (qué abandono sigue). Una app puede tener varios.</p>
      </div>

      {/* SECUENCIAS */}
      {isLoading ? (
        <p className="text-slate-400">Cargando…</p>
      ) : sequences.length === 0 ? (
        <p className="text-slate-400 text-sm">Todavía no hay secuencias. Creá una arriba.</p>
      ) : (
        <div className="space-y-3">
          {sequences.map((s) => <SequenceCard key={s.id} seq={s} onChanged={invalidate} />)}
        </div>
      )}
    </div>
  );
}

function SequenceCard({ seq, onChanged }: { seq: Sequence; onChanged: () => void }) {
  const [enabled, setEnabled] = useState(seq.enabled);
  const [steps, setSteps] = useState<Step[]>(seq.steps);
  const [coldMsg, setColdMsg] = useState(seq.backlogColdMessage ?? '');
  const [warmMsg, setWarmMsg] = useState(seq.backlogWarmMessage ?? '');
  const [coldAfterDays, setColdAfterDays] = useState<string>(
    seq.backlogColdAfterDays != null ? String(seq.backlogColdAfterDays) : '');

  const save = useMutation({
    mutationFn: async () => api.put('/followup-sequences', {
      productKey: seq.productKey, trigger: seq.trigger, enabled,
      steps: steps.map((s, i) => ({ ...s, order: i })),
      backlogColdMessage: coldMsg.trim() || null,
      backlogWarmMessage: warmMsg.trim() || null,
      backlogColdAfterDays: coldAfterDays.trim() ? Math.max(1, parseInt(coldAfterDays, 10) || 0) || null : null,
    }),
    onSuccess: () => { onChanged(); toast.success('Guardado'); },
    onError: () => toast.error('No se pudo guardar'),
  });
  const remove = useMutation({
    mutationFn: async () => api.delete(`/followup-sequences/${seq.id}`),
    onSuccess: () => { onChanged(); toast.success('Secuencia borrada'); },
    onError: () => toast.error('No se pudo borrar'),
  });

  const setStep = (i: number, patch: Partial<Step>) =>
    setSteps((arr) => arr.map((s, j) => (j === i ? { ...s, ...patch } : s)));
  const addStep = () =>
    setSteps((arr) => [...arr, { order: arr.length, delayMinutes: 5, channel: 'whatsapp', subject: null, body: '' }]);
  const delStep = (i: number) => setSteps((arr) => arr.filter((_, j) => j !== i));

  return (
    <div className={`card p-4 space-y-3 ${enabled ? '' : 'opacity-75'}`}>
      <div className="flex items-center justify-between gap-2">
        <div>
          <div className="font-semibold">{seq.displayName} <span className="text-slate-400 font-normal">· {seq.trigger}</span></div>
          <div className="text-xs text-slate-500">{steps.length} paso(s)</div>
        </div>
        <div className="flex items-center gap-3">
          <label className="flex items-center gap-2 text-sm text-slate-600">
            <Switch on={enabled} onClick={() => setEnabled((v) => !v)} /> Activa
          </label>
        </div>
      </div>

      <div className="space-y-2">
        {steps.map((s, i) => (
          <div key={i} className="rounded border border-slate-200 p-2 space-y-2">
            <div className="flex items-center gap-2 flex-wrap">
              <span className="text-xs text-slate-400 w-10">#{i + 1}</span>
              <label className="text-xs text-slate-500">a los</label>
              <input type="number" min={0} className="input w-24 py-1" value={s.delayMinutes}
                onChange={(e) => setStep(i, { delayMinutes: Math.max(0, +e.target.value) })} />
              <span className="text-xs text-slate-500">min, por</span>
              <select className="input w-32 py-1" value={s.channel}
                onChange={(e) => setStep(i, { channel: e.target.value })}>
                {CHANNELS.map((c) => <option key={c} value={c}>{c}</option>)}
                {!CHANNELS.includes(s.channel) && <option value={s.channel}>{s.channel}</option>}
              </select>
              <button className="btn-danger text-xs ml-auto px-2 py-1" onClick={() => delStep(i)}>Quitar</button>
            </div>
            {s.channel === 'email' && (
              <input className="input py-1" placeholder="Asunto (email)" value={s.subject ?? ''}
                onChange={(e) => setStep(i, { subject: e.target.value })} />
            )}
            <textarea className="input min-h-[56px]" placeholder="Mensaje (placeholders: {code}, {name}…)"
              value={s.body} onChange={(e) => setStep(i, { body: e.target.value })} />
          </div>
        ))}
        <button className="btn-secondary text-xs" onClick={addStep}>+ Agregar paso</button>
      </div>

      {/* BACKLOG (abandonos viejos): un solo toque, no el flujo de pasos en vivo */}
      <div className="rounded border border-amber-200 bg-amber-50/40 p-3 space-y-2">
        <div className="flex items-center justify-between gap-2 flex-wrap">
          <div className="text-sm font-semibold text-amber-800">Mensajes de backlog (abandonos viejos)</div>
          <label className="flex items-center gap-1.5 text-xs text-slate-500">
            pasan a "frío" después de
            <input type="number" min={1} className="input w-20 py-1" placeholder="14"
              value={coldAfterDays} onChange={(e) => setColdAfterDays(e.target.value)} />
            días
          </label>
        </div>
        <p className="text-[11px] text-slate-500">
          Un solo toque suave para los abandonos rancios (no el flujo de pasos en vivo). Si lo dejás vacío,
          la app usa su texto por defecto. Placeholders: {'{code}'}, {'{name}'}…
        </p>
        <div>
          <label className="text-xs text-slate-500">Tibio (abandono reciente dentro de la ventana)</label>
          <textarea className="input min-h-[64px]" placeholder="(vacío = la app usa su texto por defecto)"
            value={warmMsg} onChange={(e) => setWarmMsg(e.target.value)} />
        </div>
        <div>
          <label className="text-xs text-slate-500">Frío / reactivación (abandono más viejo que el umbral)</label>
          <textarea className="input min-h-[64px]" placeholder="(vacío = la app usa su texto por defecto)"
            value={coldMsg} onChange={(e) => setColdMsg(e.target.value)} />
        </div>
      </div>

      <div className="flex justify-between gap-2">
        <button className="btn-danger text-xs" onClick={() => remove.mutate()}>Borrar secuencia</button>
        <button className="btn-primary" disabled={save.isPending} onClick={() => save.mutate()}>Guardar</button>
      </div>
    </div>
  );
}
